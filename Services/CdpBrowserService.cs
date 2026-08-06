using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChromeProtocol.Domains;
using ChromeProtocol.Runtime.Messaging.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// CDP 浏览器服务 - 管理隐藏浏览器实例，通过 Chrome DevTools Protocol 获取网页元素值
/// </summary>
public class CdpBrowserService : IDisposable
{
    private static readonly Lazy<CdpBrowserService> _instance = new(() => new CdpBrowserService());
    public static CdpBrowserService Instance => _instance.Value;

    // 实例池：key = "{browserId}_{profileId}"
    private readonly Dictionary<string, CdpBrowserInstance> _instances = new();
    private readonly object _lock = new();
    private int _nextPort = 9222;

    // 缓存的 WebSocket CDP 连接（避免每次抓取都新建连接触发浏览器弹窗）
    private DefaultProtocolClient? _cachedClient;
    private int _cachedPort;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private CdpBrowserService() { }

    private static readonly HttpClient _discoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 发现浏览器 CDP WebSocket 端点，返回按优先级排列的候选 URI。
    /// Chrome 150 的 headless 模式可能只监听 IPv6 回环（::1），且无 GUID 的
    /// /devtools/browser 路径返回 404，因此需同时探测 IPv4/IPv6 并从
    /// DevToolsActivePort 文件或 HTTP /json/version 获取完整路径。
    /// </summary>
    public static async Task<List<Uri>> DiscoverWsUrisAsync(int port)
    {
        var result = new List<Uri>();
        void Add(string s) { if (Uri.TryCreate(s, UriKind.Absolute, out var u) && !result.Contains(u)) result.Add(u); }

        // 1. DevToolsActivePort 文件（含 SideDesk 自启动的 HeadlessProfile）：首行端口，次行 WS 路径；
        //    逐个候选校验端口匹配，避免读到其他浏览器实例的文件
        foreach (var activePortFile in FindDevToolsActivePortFiles())
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(activePortFile);
                if (lines.Length >= 2 && int.TryParse(lines[0].Trim(), out int filePort) && filePort == port)
                {
                    var path = lines[1].Trim();
                    if (path.StartsWith("/"))
                    {
                        Add($"ws://127.0.0.1:{port}{path}");
                        Add($"ws://[::1]:{port}{path}");
                    }
                }
            }
            catch { /* 文件可能被占用，忽略 */ }
        }

        // 2. HTTP /json/version 发现（双栈探测：可能只监听 IPv4 或 IPv6）
        foreach (var (host, display) in new[] { ("127.0.0.1", "127.0.0.1"), ("[::1]", "::1") })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var resp = await _discoverHttp.GetAsync($"http://{host}:{port}/json/version", cts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token));

                // 该地址可达：无 GUID 的标准路径排前兼容旧版浏览器
                Add($"ws://{host}:{port}/devtools/browser");

                var wsUrl = json["webSocketDebuggerUrl"]?.Value<string>();
                if (!string.IsNullOrEmpty(wsUrl) && Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri))
                {
                    // 响应里的主机可能是 localhost/IPv6，统一替换为实际可达的地址
                    var builder = new UriBuilder(wsUri) { Host = display };
                    Add(builder.Uri.ToString());
                }
            }
            catch { /* 该地址未监听，继续探测另一地址 */ }
        }

        // 3. 兜底：直接尝试标准路径（双栈）
        Add($"ws://127.0.0.1:{port}/devtools/browser");
        Add($"ws://[::1]:{port}/devtools/browser");

        return result;
    }

    /// <summary>
    /// 连接已运行的浏览器（用户主浏览器，已开启远程调试），获取网页元素文本值。
    /// 不启动新进程，直接通过 WebSocket CDP 连接（Chrome 144+ 不再支持 HTTP /json 发现端点）。
    /// </summary>
    /// <param name="port">浏览器调试端口（如 9222）</param>
    /// <param name="url">目标网址</param>
    /// <param name="selector">CSS Selector 或 XPath（以 // 开头）</param>
    public async Task<string> FetchFromRunningBrowserAsync(int port, string url, string selector)
    {
        Debug.WriteLine($"[CDP] === FetchFromRunningBrowser 开始 === port={port}, url={url}, selector={selector}");
        try
        {
            // 复用缓存的 WebSocket CDP 连接（避免重复新建连接触发浏览器弹窗）
            var client = await GetOrConnectBrowserAsync(port);
            if (client == null)
            {
                Debug.WriteLine($"[CDP] ✗ 无法通过 WebSocket 连接到 127.0.0.1:{port}");
                return "--";
            }
            Debug.WriteLine($"[CDP] ✓ 已获取 CDP 连接 (cached={_cachedClient != null})");

            // 查找匹配 URL 的 Tab
            var targetId = await FindTabByUrlAsync(client, url);
            if (targetId == null)
            {
                Debug.WriteLine($"[CDP] ✗ 未找到匹配 URL 的标签页: {url}");
                return "--";
            }
            Debug.WriteLine($"[CDP] ✓ 找到目标 Tab: targetId={targetId}");

            // Attach 到目标 Tab 并执行 JS
            var result = await EvaluateOnTargetAsync(client, targetId, selector);
            Debug.WriteLine($"[CDP] ✓ 抓取结果: \"{result}\"");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] ✗ FetchFromRunningBrowser 异常: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[CDP]   StackTrace: {ex.StackTrace}");
            // 连接可能已断开，清除缓存以便下次重连
            InvalidateCachedClient();
            return "--";
        }
    }

    /// <summary>
    /// 获取或复用缓存的 WebSocket CDP 连接。
    /// 避免每次数据抓取都新建连接（Edge 每次新连接都会弹出"允许远程调试"对话框）。
    /// </summary>
    public async Task<DefaultProtocolClient?> GetOrConnectBrowserAsync(int port)
    {
        // 快速检查：缓存连接是否可用
        if (_cachedClient != null && _cachedPort == port)
            return _cachedClient;

        // 全局守卫：同端口已有连接尝试进行中（Edge 弹窗未确认）则复用，不重复弹窗
        var client = await CdpConnectGuard.ConnectAsync(port, () => ConnectBrowserAsync(port));

        await _connectLock.WaitAsync();
        try
        {
            // 等待期间可能已有其他调用者完成缓存
            if (_cachedClient != null && _cachedPort == port)
            {
                if (!ReferenceEquals(_cachedClient, client))
                {
                    try { client?.Dispose(); } catch { }
                }
                return _cachedClient;
            }

            // 清除旧连接并缓存本次结果
            InvalidateCachedClient();
            _cachedClient = client;
            _cachedPort = port;
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 使缓存的连接失效（连接断开或出错时调用）
    /// </summary>
    private void InvalidateCachedClient()
    {
        try { _cachedClient?.Dispose(); } catch { }
        _cachedClient = null;
    }

    /// <summary>
    /// 通过 WebSocket CDP 连接到已运行的浏览器，返回协议客户端。
    /// 依次尝试 DiscoverWsUrisAsync 发现的候选端点（DevToolsActivePort 文件路径、
    /// /json/version 返回的带 GUID 路径、IPv4/IPv6 双栈标准路径）。
    /// </summary>
    public async Task<DefaultProtocolClient?> ConnectBrowserAsync(int port)
    {
        foreach (var wsUri in await DiscoverWsUrisAsync(port))
        {
            var client = new DefaultProtocolClient(wsUri, NullLogger.Instance);
            try
            {
                // 超时适当放宽：Edge “允许远程调试”弹窗需用户手动确认，
                // 太短会在用户确认前超时重试、叠加弹窗；连接被拒绝时仍会快速失败。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.ConnectAsync(cts.Token);
                Debug.WriteLine($"[CDP] WebSocket 连接成功: {wsUri}");
                return client;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CDP] 连接 {wsUri} 失败: {ex.Message}");
                client.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// 枚举可能的浏览器 DevToolsActivePort 文件（Edge/Chrome 用户数据目录 + SideDesk 自启动的 HeadlessProfile）
    /// </summary>
    private static IEnumerable<string> FindDevToolsActivePortFiles()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "DevToolsActivePort"),
            Path.Combine(localAppData, "Google", "Chrome", "User Data", "DevToolsActivePort"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data", "DevToolsActivePort"),
        };

        // SideDesk 自启动隐藏浏览器的专属 userData 目录优先（端口匹配概率最高）
        var headlessRoot = Path.Combine(localAppData, "SideDesk", "HeadlessProfile");
        var result = new List<string>();
        try
        {
            if (Directory.Exists(headlessRoot))
                result.AddRange(Directory.GetDirectories(headlessRoot)
                    .Select(d => Path.Combine(d, "DevToolsActivePort"))
                    .Where(File.Exists));
        }
        catch { }

        result.AddRange(candidates.Where(File.Exists));
        return result;
    }

    /// <summary>
    /// 通过 Target.getTargets 查找匹配 URL 的 page 类型 Tab，返回 TargetId
    /// </summary>
    public async Task<Target.TargetIDType?> FindTabByUrlAsync(DefaultProtocolClient client, string url)
    {
        var result = await client.SendCommandAsync(Target.GetTargets());
        var pages = result.TargetInfos.Where(t => t.Type == "page").ToList();
        Debug.WriteLine($"[CDP] GetTargets: 共 {result.TargetInfos.Count} 个 target, 其中 {pages.Count} 个 page");
        foreach (var p in pages)
            Debug.WriteLine($"[CDP]   page: \"{p.Title}\" -> {p.Url}");

        // 精确匹配或前缀匹配
        foreach (var tab in pages)
        {
            var tabUrl = tab.Url ?? "";
            if (tabUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase) || url.StartsWith(tabUrl, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"[CDP] URL 匹配成功: \"{tabUrl}\" ~ \"{url}\"");
                return tab.TargetId;
            }
        }

        // 如果只有一个页面 Tab，直接使用它
        if (pages.Count == 1)
        {
            Debug.WriteLine($"[CDP] 仅 1 个 page，直接使用: {pages[0].Url}");
            return pages[0].TargetId;
        }

        Debug.WriteLine($"[CDP] ✗ 无匹配 URL 且有 {pages.Count} 个 page，返回 null");
        return null;
    }

    /// <summary>
    /// Attach 到指定 Target 并执行 JS 获取元素文本
    /// </summary>
    private async Task<string> EvaluateOnTargetAsync(DefaultProtocolClient client, Target.TargetIDType targetId, string selector)
    {
        // Attach 到目标 Tab，获取 sessionId
        var attachResult = await client.SendCommandAsync(Target.AttachToTarget(targetId, Flatten: true));
        var sessionId = attachResult.SessionId.Value;
        Debug.WriteLine($"[CDP] AttachToTarget 成功, sessionId={sessionId}");

        // 构建 JS 表达式：支持 CSS Selector 和 XPath
        var jsExpression = BuildElementTextJs(selector);
        Debug.WriteLine($"[CDP] 执行 JS: {jsExpression}");

        // 通过 session 执行 Runtime.evaluate
        var evalResult = await client.SendCommandAsync(
            Runtime.Evaluate(jsExpression, ReturnByValue: true),
            sessionId: sessionId);

        Debug.WriteLine($"[CDP] Evaluate 返回: type={evalResult.Result?.Type}, value={evalResult.Result?.Value}, subtype={evalResult.Result?.Subtype}");
        if (evalResult.ExceptionDetails != null)
            Debug.WriteLine($"[CDP] ✗ JS 异常: {evalResult.ExceptionDetails.Text} (line {evalResult.ExceptionDetails.LineNumber})");

        var value = evalResult.Result?.Value?.ToString();
        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }

    /// <summary>
    /// 构建获取元素文本的 JS 表达式（支持 CSS Selector 和 XPath）
    /// XPath 以 / 或 // 开头（如 /html/body/... 或 //div[@class=...]）
    /// </summary>
    private static string BuildElementTextJs(string selector)
    {
        var trimmed = selector.TrimStart();
        if (trimmed.StartsWith("//") || trimmed.StartsWith("/"))
        {
            return $"(function(){{ var r = document.evaluate(\"{EscapeJs(selector)}\", document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null); return r.singleNodeValue ? r.singleNodeValue.textContent.trim() : '--'; }})()";
        }
        return $"(function(){{ var el = document.querySelector(\"{EscapeJs(selector)}\"); return el ? el.textContent.trim() : '--'; }})()";
    }

    /// <summary>
    /// 获取网页元素的文本值
    /// </summary>
    /// <param name="browserExePath">浏览器可执行文件路径</param>
    /// <param name="profileDir">用户数据目录</param>
    /// <param name="instanceKey">实例标识 "{browserId}_{profileId}"</param>
    /// <param name="url">目标网址</param>
    /// <param name="selector">CSS Selector 或 XPath（以 // 开头）</param>
    public async Task<string> FetchElementValueAsync(string browserExePath, string profileDir, string instanceKey, string url, string selector)
    {
        var instance = await GetOrCreateInstanceAsync(browserExePath, profileDir, instanceKey);
        if (instance == null) return "--";

        try
        {
            // 获取或创建目标 URL 的 Tab
            var wsUrl = await GetOrCreateTabAsync(instance, url);
            if (string.IsNullOrEmpty(wsUrl)) return "--";

            // 通过 CDP WebSocket 执行 JS 获取元素文本
            return await EvaluateElementTextAsync(wsUrl, selector);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] FetchElementValue 失败: {ex.Message}");
            return "--";
        }
    }

    /// <summary>
    /// 获取或创建浏览器实例
    /// </summary>
    private async Task<CdpBrowserInstance?> GetOrCreateInstanceAsync(string browserExePath, string profileDir, string instanceKey)
    {
        // 【已禁用】CDP浏览器服务 - 2026-06-09 禁用，解决重复启动问题
        Debug.WriteLine("[CDP] 服务已禁用，跳过浏览器启动");
        return null;
        
        /*
        lock (_lock)
        {
            if (_instances.TryGetValue(instanceKey, out var existing) && !existing.BrowserProcess.HasExited)
                return existing;
        }

        // 分配端口
        int port = FindAvailablePort();

        // 如果未指定 profileDir，使用专属 Headless 目录，避免与已运行浏览器冲突
        if (string.IsNullOrEmpty(profileDir))
        {
            profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDesk", "HeadlessProfile");
            Directory.CreateDirectory(profileDir);
        }

        // 启动隐藏浏览器
        var args = $"--headless=new --remote-debugging-port={port} --no-first-run --no-default-browser-check --disable-extensions --user-data-dir=\"{profileDir}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browserExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(psi);
            if (process == null) return null;

            // 等待 CDP 就绪（最多 10 秒）
            var ready = await WaitForCdpReadyAsync(port, TimeSpan.FromSeconds(10));
            if (!ready)
            {
                try { process.Kill(); } catch { }
                return null;
            }

            var instance = new CdpBrowserInstance
            {
                BrowserProcess = process,
                DebugPort = port,
                Tabs = new Dictionary<string, string>()
            };

            lock (_lock)
            {
                _instances[instanceKey] = instance;
            }

            Debug.WriteLine($"[CDP] 浏览器实例已启动: {instanceKey}, port={port}");
            return instance;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] 启动浏览器失败: {ex.Message}");
            return null;
        }
        */
    }

    /// <summary>
    /// 等待 CDP 端口就绪：以 WS 连接尝试探测（兼容只监听 IPv6 的 headless 浏览器）。
    /// </summary>
    private async Task<bool> WaitForCdpReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var wsUri in await DiscoverWsUrisAsync(port))
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.ConnectAsync(wsUri, cts.Token);
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return true;
                }
                catch { }
            }
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>
    /// 获取或创建目标 URL 的 Tab，返回 WebSocket 调试 URL
    /// </summary>
    private async Task<string?> GetOrCreateTabAsync(CdpBrowserInstance instance, string url)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // 检查是否已有该 URL 的 Tab
        if (instance.Tabs.TryGetValue(url, out var cachedWsUrl))
        {
            return cachedWsUrl;
        }

        try
        {
            // 获取当前 Tab 列表
            var json = await httpClient.GetStringAsync($"http://localhost:{instance.DebugPort}/json");
            var tabs = JArray.Parse(json);

            // 查找已存在的 Tab
            foreach (var tab in tabs)
            {
                var tabUrl = tab["url"]?.ToString() ?? "";
                if (tabUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase) || url.StartsWith(tabUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var wsUrl = tab["webSocketDebuggerUrl"]?.ToString();
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        instance.Tabs[url] = wsUrl;
                        return wsUrl;
                    }
                }
            }

            // 创建新 Tab
            var createResponse = await httpClient.GetStringAsync($"http://localhost:{instance.DebugPort}/json/new?{Uri.EscapeDataString(url)}");
            var newTab = JObject.Parse(createResponse);
            var newWsUrl = newTab["webSocketDebuggerUrl"]?.ToString();

            if (!string.IsNullOrEmpty(newWsUrl))
            {
                instance.Tabs[url] = newWsUrl;
                // 等待页面加载
                await Task.Delay(3000);
                return newWsUrl;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] Tab 管理失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 通过 CDP WebSocket 执行 JS 获取元素文本
    /// </summary>
    private async Task<string> EvaluateElementTextAsync(string wsUrl, string selector)
    {
        using var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

        // 构建 JS 表达式：支持 CSS Selector 和 XPath
        string jsExpression;
        if (selector.TrimStart().StartsWith("//"))
        {
            // XPath
            jsExpression = $"(function(){{ var r = document.evaluate(\"{EscapeJs(selector)}\", document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null); return r.singleNodeValue ? r.singleNodeValue.textContent.trim() : '--'; }})()";
        }
        else
        {
            // CSS Selector
            jsExpression = $"(function(){{ var el = document.querySelector(\"{EscapeJs(selector)}\"); return el ? el.textContent.trim() : '--'; }})()";
        }

        // 发送 Runtime.evaluate 命令
        var command = new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression = jsExpression }
        };

        var commandJson = JsonConvert.SerializeObject(command);
        var sendBuffer = Encoding.UTF8.GetBytes(commandJson);
        await ws.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Text, true, cts.Token);

        // 接收响应
        var receiveBuffer = new byte[8192];
        var result = new StringBuilder();
        WebSocketReceiveResult receiveResult;
        do
        {
            receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
            result.Append(Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count));
        } while (!receiveResult.EndOfMessage);

        // 解析返回值
        var response = JObject.Parse(result.ToString());
        var value = response["result"]?["result"]?["value"]?.ToString();

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }

    /// <summary>
    /// 以可见模式启动浏览器（不使用 headless），用于元素选择器
    /// </summary>
    public async Task<(int port, Process? process)> LaunchVisibleBrowserAsync(string browserExePath, string profileDir, string url)
    {
        int port = FindAvailablePort();

        // 直接使用默认 profile（用户需先关闭浏览器）
        var args = $"--remote-debugging-port={port} --no-first-run --no-default-browser-check --disable-extensions --auto-open-devtools-for-tabs";
        if (!string.IsNullOrEmpty(profileDir))
            args += $" --user-data-dir=\"{profileDir}\"";
        args += $" \"{url}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browserExePath,
                Arguments = args,
                UseShellExecute = false
            };

            Debug.WriteLine($"[CDP] 启动可见浏览器: {browserExePath} {args}");
            var process = Process.Start(psi);
            if (process == null) return (port, null);

            var ready = await WaitForCdpReadyAsync(port, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                Debug.WriteLine($"[CDP] CDP 就绪检查超时, port={port}");
                try { process.Kill(); } catch { }
                return (port, null);
            }

            Debug.WriteLine($"[CDP] 可见浏览器已启动: port={port}, url={url}");
            return (port, process);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] 启动可见浏览器失败: {ex.Message}");
            return (port, null);
        }
    }

    /// <summary>
    /// 探测浏览器默认的 User Data 目录
    /// </summary>
    private static string DetectDefaultUserDataDir(string browserExePath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var browserName = Path.GetFileNameWithoutExtension(browserExePath).ToLower();

        if (browserName.Contains("chrome"))
            return Path.Combine(localAppData, "Google", "Chrome", "User Data");
        if (browserName.Contains("edge") || browserName.Contains("msedge"))
            return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        if (browserName.Contains("brave"))
            return Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data");

        return "";
    }

    /// <summary>
    /// 注入元素选择覆盖层，等待用户点击选中元素，返回 CSS Selector
    /// </summary>
    public async Task<string?> PickElementSelectorAsync(int port, string url)
    {
        // 等待页面加载
        await Task.Delay(3000);

        // 新版浏览器已移除 HTTP /json 端点，改用浏览器级 WS 连接 + Target.getTargets 获取页面 WS 地址
        string? wsUrl = null;

        var browserClient = await ConnectBrowserAsync(port);
        if (browserClient == null)
        {
            Debug.WriteLine($"[CDP] 获取 Tab 列表失败：无法连接浏览器 CDP (port={port})");
            return null;
        }

        try
        {
            // 探测实际可达的环回地址（headless Chrome 150 可能只监听 IPv6）
            string? reachableHost = null;
            using (var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                foreach (var h in new[] { "127.0.0.1", "[::1]" })
                {
                    try
                    {
                        var r = await probeHttp.GetAsync($"http://{h}:{port}/json/version");
                        if (r.IsSuccessStatusCode) { reachableHost = h; break; }
                    }
                    catch { }
                }
            }

            var targets = await browserClient.SendCommandAsync(ChromeProtocol.Domains.Target.GetTargets());
            foreach (var tab in targets.TargetInfos.Where(t => t.Type == "page"))
            {
                var tabUrl = tab.Url ?? "";
                if (tabUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase) || url.StartsWith(tabUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // 页面级 WS 端点直接按 targetId 构造（/json 端点已移除后的标准做法）
                    var host = reachableHost ?? "127.0.0.1";
                    wsUrl = $"ws://{host}:{port}/devtools/page/{tab.TargetId}";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] 获取 Tab 列表失败: {ex.Message}");
        }
        finally
        {
            browserClient.Dispose();
        }

        if (string.IsNullOrEmpty(wsUrl)) return null;

        using var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(65));

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            // 注入 ElementPickerScript
            var injectCommand = new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = ElementPickerScript }
            };

            var commandJson = JsonConvert.SerializeObject(injectCommand);
            var sendBuffer = Encoding.UTF8.GetBytes(commandJson);
            await ws.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Text, true, cts.Token);

            // 接收注入响应
            var receiveBuffer = new byte[8192];
            var result = new StringBuilder();
            WebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
                result.Append(Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count));
            } while (!receiveResult.EndOfMessage);

            // 轮询等待用户选择元素（每 500ms，最多 60 秒）
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, cts.Token);

                var pollCommand = new
                {
                    id = 2,
                    method = "Runtime.evaluate",
                    @params = new { expression = "window.__pickedSelector" }
                };

                var pollJson = JsonConvert.SerializeObject(pollCommand);
                var pollBuffer = Encoding.UTF8.GetBytes(pollJson);
                await ws.SendAsync(new ArraySegment<byte>(pollBuffer), WebSocketMessageType.Text, true, cts.Token);

                result.Clear();
                do
                {
                    receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
                    result.Append(Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count));
                } while (!receiveResult.EndOfMessage);

                var response = JObject.Parse(result.ToString());
                var value = response["result"]?["result"]?["value"]?.ToString();

                if (!string.IsNullOrEmpty(value))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return value;
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] PickElementSelector 失败: {ex.Message}");
        }

        return null;
    }

    private const string ElementPickerScript = @"(function() {
  if (window.__elementPickerActive) return;
  window.__elementPickerActive = true;
  window.__pickedSelector = '';

  var overlay = document.createElement('div');
  overlay.id = '__picker_overlay';
  overlay.style.cssText = 'position:fixed;pointer-events:none;border:2px solid #4A90D9;background:rgba(74,144,217,0.15);z-index:2147483647;transition:all 0.05s ease;display:none;';
  document.body.appendChild(overlay);

  var tip = document.createElement('div');
  tip.style.cssText = 'position:fixed;top:8px;left:50%;transform:translateX(-50%);background:#333;color:#fff;padding:6px 16px;border-radius:4px;font-size:13px;z-index:2147483647;';
  tip.textContent = '请点击要选择的元素';
  document.body.appendChild(tip);

  var currentEl = null;

  function onMouseOver(e) {
    currentEl = e.target;
    var rect = currentEl.getBoundingClientRect();
    overlay.style.display = 'block';
    overlay.style.top = rect.top + 'px';
    overlay.style.left = rect.left + 'px';
    overlay.style.width = rect.width + 'px';
    overlay.style.height = rect.height + 'px';
  }

  function generateSelector(el) {
    if (el.id) return '#' + CSS.escape(el.id);

    var path = [];
    while (el && el !== document.body && el !== document.documentElement) {
      var selector = el.tagName.toLowerCase();
      if (el.id) { path.unshift('#' + CSS.escape(el.id)); break; }
      if (el.className && typeof el.className === 'string') {
        var classes = el.className.trim().split(/\s+/).filter(function(c) { return c && !c.startsWith('__'); });
        if (classes.length > 0) {
          selector += '.' + classes.map(function(c) { return CSS.escape(c); }).join('.');
          var matches = document.querySelectorAll(selector);
          if (matches.length === 1) { path.unshift(selector); break; }
        }
      }
      var parent = el.parentElement;
      if (parent) {
        var siblings = Array.from(parent.children).filter(function(c) { return c.tagName === el.tagName; });
        if (siblings.length > 1) {
          var index = siblings.indexOf(el) + 1;
          selector += ':nth-of-type(' + index + ')';
        }
      }
      path.unshift(selector);
      el = parent;
    }
    return path.join(' > ');
  }

  function onClick(e) {
    e.preventDefault();
    e.stopPropagation();
    e.stopImmediatePropagation();

    if (currentEl) {
      window.__pickedSelector = generateSelector(currentEl);
    }

    document.removeEventListener('mouseover', onMouseOver, true);
    document.removeEventListener('click', onClick, true);
    overlay.remove();
    tip.textContent = '已选择: ' + window.__pickedSelector;
    setTimeout(function() { tip.remove(); }, 2000);
    window.__elementPickerActive = false;
  }

  document.addEventListener('mouseover', onMouseOver, true);
  document.addEventListener('click', onClick, true);
})();";

    /// <summary>
    /// 查找可用端口（9222-9322 范围）
    /// </summary>
    private int FindAvailablePort()
    {
        lock (_lock)
        {
            var port = _nextPort;
            _nextPort++;
            if (_nextPort > 9322) _nextPort = 9222;
            return port;
        }
    }

    /// <summary>
    /// JS 字符串转义
    /// </summary>
    private static string EscapeJs(string input)
    {
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    /// <summary>
    /// 关闭所有浏览器实例和缓存连接
    /// </summary>
    public void Dispose()
    {
        InvalidateCachedClient();

        lock (_lock)
        {
            foreach (var kvp in _instances)
            {
                try
                {
                    if (!kvp.Value.BrowserProcess.HasExited)
                    {
                        kvp.Value.BrowserProcess.Kill();
                        Debug.WriteLine($"[CDP] 已关闭浏览器实例: {kvp.Key}");
                    }
                }
                catch { }
            }
            _instances.Clear();
        }
    }
}

/// <summary>
/// CDP 浏览器实例信息
/// </summary>
internal class CdpBrowserInstance
{
    public Process BrowserProcess { get; set; } = null!;
    public int DebugPort { get; set; }
    public Dictionary<string, string> Tabs { get; set; } = new(); // url -> webSocketDebuggerUrl
}
