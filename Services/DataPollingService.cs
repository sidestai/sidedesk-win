using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ChromeProtocol.Domains;
using ChromeProtocol.Runtime.Messaging.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// 数据看板连接状态
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// 浏览器 CDP 连接（按 browserId+profileId 分组）
/// </summary>
public class BrowserConnection
{
    public string Key { get; set; } = "";                // "{browserId}_{profileId}" 或 "default"
    public DefaultProtocolClient? Client { get; set; }
    public Process? LaunchedProcess { get; set; }        // 自启动的隐藏浏览器进程（null=连接的用户浏览器）
    public int Port { get; set; }
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
    public bool IsSelfLaunched => LaunchedProcess != null;
}

/// <summary>
/// 数据轮询服务 - 中央数据源，统一管理所有数据看板项的定时抓取。
/// 面板组件只负责展示，不发起请求。
/// </summary>
public class DataPollingService : IDisposable
{
    private static readonly Lazy<DataPollingService> _instance = new(() => new DataPollingService());
    public static DataPollingService Instance => _instance.Value;

    /// <summary>数据项更新事件 (itemId, newValue)</summary>
    public event Action<string, string>? DataUpdated;

    /// <summary>连接状态变化事件 (connectionKey, newState)</summary>
    public event Action<string, ConnectionState>? ConnectionStateChanged;

    // 所有数据项（面板项 + 后台项的引用）
    private readonly List<DataDashboardItem> _items = new();
    private readonly object _itemsLock = new();

    // 每个数据项的定时器
    private readonly Dictionary<string, DispatcherTimer> _timers = new();

    // CDP 连接池: key = "{browserId}_{profileId}" 或 "default"
    private readonly Dictionary<string, BrowserConnection> _connections = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private int _nextAutoPort = 9300;

    // 全局建连计数器（用于诊断：每建立一条 CDP 连接就 +1，浏览器每收到一条新连接都会弹一次提示）
    private static int _totalConnections = 0;

    private DataPollingService() { }

    /// <summary>
    /// 从 WindowConfigs 同步数据项（面板项 + 后台项），重建定时器
    /// </summary>
    public void SyncFromConfigs(WindowConfigManager configs)
    {
        lock (_itemsLock)
        {
            _items.Clear();

            // 面板数据项
            foreach (var win in configs.Windows)
            {
                foreach (var panel in win.Panels)
                {
                    if (panel.DashboardItems == null || panel.DashboardItems.Count == 0) continue;
                    _items.AddRange(panel.DashboardItems);
                }
            }

            // 后台数据项
            _items.AddRange(configs.BackgroundDashboardItems);
        }

        RebuildTimers();
        Debug.WriteLine($"[DataPolling] SyncFromConfigs: 共 {_items.Count} 个数据项");
    }

    /// <summary>获取当前所有数据项（只读副本引用）</summary>
    public List<DataDashboardItem> GetAllItems()
    {
        lock (_itemsLock) return new List<DataDashboardItem>(_items);
    }

    /// <summary>获取指定数据项的连接状态</summary>
    public ConnectionState GetItemState(DataDashboardItem item)
    {
        var key = GetConnectionKey(item);
        lock (_connections)
        {
            return _connections.TryGetValue(key, out var conn) ? conn.State : ConnectionState.Disconnected;
        }
    }

    /// <summary>获取指定数据项的连接 key</summary>
    public string GetConnectionKey(DataDashboardItem item)
    {
        if (!string.IsNullOrEmpty(item.BrowserId))
            return $"{item.BrowserId}_{item.ProfileId ?? ""}";
        return "default";
    }

    /// <summary>获取指定连接 key 的 BrowserConnection</summary>
    public BrowserConnection? GetConnection(string key)
    {
        lock (_connections)
        {
            return _connections.TryGetValue(key, out var conn) ? conn : null;
        }
    }

    /// <summary>
    /// 断开指定连接。如果是自启动的隐藏浏览器则关闭进程。
    /// </summary>
    public void DisconnectConnection(string key)
    {
        BrowserConnection? conn;
        lock (_connections)
        {
            _connections.TryGetValue(key, out conn);
            _connections.Remove(key);
        }

        if (conn == null) return;

        try { conn.Client?.Dispose(); } catch { }

        if (conn.LaunchedProcess != null)
        {
            try
            {
                if (!conn.LaunchedProcess.HasExited)
                    conn.LaunchedProcess.Kill();
            }
            catch { }
        }

        SetConnectionState(key, ConnectionState.Disconnected);
        Debug.WriteLine($"[DataPolling] 已断开连接: {key}");
    }

    /// <summary>立即刷新指定数据项</summary>
    public async Task RefreshItemAsync(DataDashboardItem item)
    {
        await PollItemAsync(item);
    }

    /// <summary>立即刷新所有数据项</summary>
    public async Task RefreshAllAsync()
    {
        var items = GetAllItems();
        foreach (var item in items)
            await PollItemAsync(item);
    }

    /// <summary>重建所有定时器</summary>
    private void RebuildTimers()
    {
        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.Invoke(() =>
        {
            // 清除旧定时器
            foreach (var timer in _timers.Values)
                timer.Stop();
            _timers.Clear();

            var items = GetAllItems();
            foreach (var item in items)
            {
                var interval = Math.Max(item.RefreshIntervalSeconds, 10);
                var capturedItem = item;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(interval) };
                timer.Tick += async (s, e) =>
                {
                    try { await PollItemAsync(capturedItem); }
                    catch (Exception ex) { Debug.WriteLine($"[DataPolling] 定时轮询失败 {capturedItem.Label}: {ex.Message}"); }
                };
                timer.Start();
                _timers[item.Id] = timer;
            }
        });
    }

    /// <summary>
    /// 轮询单个数据项：获取连接 → 查找 Tab → 执行 JS → 更新值
    /// </summary>
    private async Task PollItemAsync(DataDashboardItem item)
    {
        var appSettings = ((App)Application.Current).AppSettings;
        var key = GetConnectionKey(item);

        Debug.WriteLine($"[DataPolling] === 轮询 \"{item.Label}\" === key={key}, url={item.TargetUrl}");

        try
        {
            // 1. 获取或建立 CDP 连接
            var conn = await GetOrCreateConnectionAsync(item, appSettings);
            if (conn?.Client == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ 无法建立连接: {key}");
                UpdateItemValue(item, "--");
                return;
            }

            // 2. 查找匹配 URL 的 Tab
            var targetId = await FindTabByUrlAsync(conn.Client, item.TargetUrl);

            // 3. 找不到 Tab → 尝试自动打开
            if (targetId == null)
            {
                if (appSettings.EnableAutoLaunchHiddenBrowser && conn.IsSelfLaunched)
                {
                    // 自启动的浏览器：导航到目标 URL
                    targetId = await NavigateToUrlAsync(conn.Client, item.TargetUrl);
                }
                else if (appSettings.EnableAutoLaunchHiddenBrowser && !conn.IsSelfLaunched)
                {
                    // 用户浏览器：尝试新建 Tab
                    targetId = await CreateTabAsync(conn.Client, item.TargetUrl);
                }
            }

            if (targetId == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ 未找到匹配 Tab: {item.TargetUrl}");
                UpdateItemValue(item, "--");
                return;
            }

            // 4. Attach + Evaluate
            var value = await EvaluateOnTargetAsync(conn.Client, targetId, item.Selector);
            UpdateItemValue(item, value);
            Debug.WriteLine($"[DataPolling] ✓ \"{item.Label}\" = \"{value}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ 轮询异常 \"{item.Label}\": {ex.GetType().Name}: {ex.Message}");
            // 连接可能已断开，清除以便下次重连
            InvalidateConnection(key);
            UpdateItemValue(item, "--");
        }
    }

    /// <summary>更新数据项的值并触发事件</summary>
    private void UpdateItemValue(DataDashboardItem item, string value)
    {
        item.Value = value;
        DataUpdated?.Invoke(item.Id, value);
    }

    /// <summary>
    /// 获取或建立浏览器 CDP 连接（连接池复用）
    /// </summary>
    private async Task<BrowserConnection?> GetOrCreateConnectionAsync(DataDashboardItem item, Settings appSettings)
    {
        var key = GetConnectionKey(item);

        // 快速检查：已有连接且存活
        lock (_connections)
        {
            if (_connections.TryGetValue(key, out var existing) && existing.Client != null)
                return existing;
        }

        await _connectLock.WaitAsync();
        try
        {
            // 双重检查
            lock (_connections)
            {
                if (_connections.TryGetValue(key, out var existing) && existing.Client != null)
                    return existing;
            }

            SetConnectionState(key, ConnectionState.Connecting);

            // 解析浏览器配置
            var (exePath, userDataDir, port) = ResolveBrowserConfig(item, appSettings);

            // 尝试连接已运行的浏览器（经全局守卫：同端口已有连接尝试进行中则复用，避免重复弹窗）
            var client = await CdpConnectGuard.ConnectAsync(port, () => ConnectBrowserAsync(port));
            if (client != null)
            {
                var conn = new BrowserConnection { Key = key, Client = client, Port = port, State = ConnectionState.Connected };
                lock (_connections) { _connections[key] = conn; }
                SetConnectionState(key, ConnectionState.Connected);
                Debug.WriteLine($"[DataPolling] ✓ 已连接浏览器: {key}, port={port}");
                return conn;
            }

            // 连接失败 → 自动启动隐藏浏览器
            if (!appSettings.EnableAutoLaunchHiddenBrowser || string.IsNullOrEmpty(exePath))
            {
                SetConnectionState(key, ConnectionState.Error);
                return null;
            }

            var launched = await LaunchHiddenBrowserAsync(key, exePath, userDataDir, port, item.TargetUrl, appSettings);
            if (launched != null)
            {
                SetConnectionState(key, ConnectionState.Connected);
                return launched;
            }

            SetConnectionState(key, ConnectionState.Error);
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 解析数据项对应的浏览器配置（exe路径、userData目录、CDP端口）
    /// </summary>
    private (string exePath, string userDataDir, int port) ResolveBrowserConfig(DataDashboardItem item, Settings appSettings)
    {
        // 默认浏览器（未指定 BrowserId）
        if (string.IsNullOrEmpty(item.BrowserId))
        {
            var defaultExe = FindDefaultBrowserExe();
            return (defaultExe, "", appSettings.CdpDebugPort);
        }

        // 指定了浏览器：从 ProgramRegistry 查找
        var registry = ((App)Application.Current).ProgramRegistry;
        var browser = registry.Browsers.FirstOrDefault(b => b.Id == item.BrowserId);
        if (browser == null)
            return (FindDefaultBrowserExe(), "", appSettings.CdpDebugPort);

        var exePath = browser.ExePath;
        var userDataDir = "";

        // 解析 Profile（userData 目录）
        if (!string.IsNullOrEmpty(item.ProfileId))
        {
            var profile = browser.Profiles.FirstOrDefault(p => p.Id == item.ProfileId);
            if (profile != null)
                userDataDir = profile.Directory;
        }

        // 为该浏览器分配独立端口（从 9300 起）
        var port = AllocatePort(item.BrowserId, item.ProfileId);
        return (exePath, userDataDir, port);
    }

    // 端口分配记录: key = browserId_profileId
    private readonly Dictionary<string, int> _allocatedPorts = new();

    /// <summary>为指定浏览器+Profile 分配固定端口</summary>
    private int AllocatePort(string browserId, string? profileId)
    {
        var key = $"{browserId}_{profileId ?? ""}";
        lock (_allocatedPorts)
        {
            if (_allocatedPorts.TryGetValue(key, out int existing))
                return existing;
            var port = _nextAutoPort++;
            if (_nextAutoPort > 9399) _nextAutoPort = 9300;
            _allocatedPorts[key] = port;
            return port;
        }
    }

    /// <summary>查找默认浏览器 exe 路径</summary>
    private static string FindDefaultBrowserExe()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary>
    /// 启动隐藏浏览器（headless 或 hidden 窗口模式）
    /// </summary>
    private async Task<BrowserConnection?> LaunchHiddenBrowserAsync(string key, string exePath, string userDataDir, int port, string url, Settings appSettings)
    {
        // 如果未指定 userData 目录，使用专属目录避免与用户浏览器冲突
        if (string.IsNullOrEmpty(userDataDir))
        {
            userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDesk", "HeadlessProfile", key.Replace("_", "-"));
            Directory.CreateDirectory(userDataDir);
        }

        var mode = appSettings.HiddenBrowserMode;
        string args;
        if (mode == "headless")
        {
            args = $"--headless=new --remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check --disable-extensions \"{url}\"";
        }
        else
        {
            args = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check \"{url}\"";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = mode == "headless",
                WindowStyle = mode == "headless" ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Minimized
            };

            Debug.WriteLine($"[DataPolling] 启动隐藏浏览器: {exePath} {args}");
            var process = Process.Start(psi);
            if (process == null) return null;

            // 等待 CDP 就绪并直接建立正式连接（全程只建立 1 条连接，
            // 避免“就绪探测 + 正式连接”重复建连触发多次浏览器弹窗）
            var client = await ConnectWhenReadyAsync(port, TimeSpan.FromSeconds(15));
            if (client == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ CDP 就绪超时, port={port}");
                try { process.Kill(); } catch { }
                return null;
            }

            var conn = new BrowserConnection
            {
                Key = key,
                Client = client,
                Port = port,
                LaunchedProcess = process,
                State = ConnectionState.Connected
            };
            lock (_connections) { _connections[key] = conn; }
            Debug.WriteLine($"[DataPolling] ✓ 隐藏浏览器已启动: {key}, port={port}, mode={mode}");
            return conn;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ 启动隐藏浏览器失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 等待 CDP 端口就绪，并直接返回成功连接的客户端。
    /// 探测成功的那条连接直接作为正式连接复用，全程只建立 1 条连接，
    /// 避免重复建连触发多次浏览器“允许远程调试”弹窗。
    /// </summary>
    private static async Task<DefaultProtocolClient?> ConnectWhenReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var client = new DefaultProtocolClient(new Uri($"ws://127.0.0.1:{port}/devtools/browser"), NullLogger.Instance);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(cts.Token);
                var n = Interlocked.Increment(ref _totalConnections);
                Debug.WriteLine($"[DataPolling] ★ 建立 CDP 连接 #{n}（隐藏浏览器就绪）: port={port}");
                return client; // 连接成功，直接复用，不 Dispose
            }
            catch
            {
                client.Dispose();
            }
            await Task.Delay(500);
        }
        return null;
    }

    /// <summary>通过 WebSocket CDP 连接到浏览器</summary>
    private static async Task<DefaultProtocolClient?> ConnectBrowserAsync(int port)
    {
        var wsUri = new Uri($"ws://127.0.0.1:{port}/devtools/browser");
        var client = new DefaultProtocolClient(wsUri, NullLogger.Instance);
        try
        {
            // 超时适当放宽：Edge “允许远程调试”弹窗需用户手动确认，太短会在确认前超时重试、叠加弹窗
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(cts.Token);
            var n = Interlocked.Increment(ref _totalConnections);
            Debug.WriteLine($"[DataPolling] ★ 建立 CDP 连接 #{n}（连接已运行浏览器）: {wsUri}");
            return client;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] 连接 {wsUri} 失败: {ex.Message}");
            client.Dispose();
            return null;
        }
    }

    /// <summary>查找匹配 URL 的 page Tab</summary>
    private static async Task<Target.TargetIDType?> FindTabByUrlAsync(DefaultProtocolClient client, string url)
    {
        var result = await client.SendCommandAsync(Target.GetTargets());
        var pages = result.TargetInfos.Where(t => t.Type == "page").ToList();

        foreach (var tab in pages)
        {
            var tabUrl = tab.Url ?? "";
            if (tabUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase) || url.StartsWith(tabUrl, StringComparison.OrdinalIgnoreCase))
                return tab.TargetId;
        }

        if (pages.Count == 1) return pages[0].TargetId;
        return null;
    }

    /// <summary>在已连接的浏览器中新建 Tab 并导航到 URL</summary>
    private static async Task<Target.TargetIDType?> CreateTabAsync(DefaultProtocolClient client, string url)
    {
        try
        {
            var result = await client.SendCommandAsync(Target.CreateTarget(url));
            Debug.WriteLine($"[DataPolling] 新建 Tab: {url} -> {result.TargetId}");
            await Task.Delay(2000); // 等待页面加载
            return result.TargetId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ 新建 Tab 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>导航已有 Tab 到 URL（用于自启动浏览器）</summary>
    private static async Task<Target.TargetIDType?> NavigateToUrlAsync(DefaultProtocolClient client, string url)
    {
        try
        {
            // 获取第一个 page target
            var result = await client.SendCommandAsync(Target.GetTargets());
            var page = result.TargetInfos.FirstOrDefault(t => t.Type == "page");
            if (page == null) return await CreateTabAsync(client, url);

            var attachResult = await client.SendCommandAsync(Target.AttachToTarget(page.TargetId, Flatten: true));
            var sessionId = attachResult.SessionId.Value;
            await client.SendCommandAsync(Page.Navigate(url), sessionId: sessionId);
            await Task.Delay(3000); // 等待页面加载
            return page.TargetId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ 导航失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>Attach 到 Target 并执行 JS 获取元素文本</summary>
    private static async Task<string> EvaluateOnTargetAsync(DefaultProtocolClient client, Target.TargetIDType targetId, string selector)
    {
        var attachResult = await client.SendCommandAsync(Target.AttachToTarget(targetId, Flatten: true));
        var sessionId = attachResult.SessionId.Value;

        var jsExpression = BuildElementTextJs(selector);
        var evalResult = await client.SendCommandAsync(
            Runtime.Evaluate(jsExpression, ReturnByValue: true),
            sessionId: sessionId);

        if (evalResult.ExceptionDetails != null)
            Debug.WriteLine($"[DataPolling] ✗ JS 异常: {evalResult.ExceptionDetails.Text}");

        var value = evalResult.Result?.Value?.ToString();
        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }

    /// <summary>构建获取元素文本的 JS 表达式（支持 CSS Selector 和 XPath）</summary>
    private static string BuildElementTextJs(string selector)
    {
        var trimmed = selector.TrimStart();
        var escaped = selector.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        if (trimmed.StartsWith("/"))
        {
            return $"(function(){{ var r = document.evaluate(\"{escaped}\", document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null); return r.singleNodeValue ? r.singleNodeValue.textContent.trim() : '--'; }})()";
        }
        return $"(function(){{ var el = document.querySelector(\"{escaped}\"); return el ? el.textContent.trim() : '--'; }})()";
    }

    /// <summary>使指定连接失效</summary>
    private void InvalidateConnection(string key)
    {
        BrowserConnection? conn;
        lock (_connections)
        {
            _connections.TryGetValue(key, out conn);
            _connections.Remove(key);
        }
        if (conn == null) return;

        try { conn.Client?.Dispose(); } catch { }
        // 注意：不清除 LaunchedProcess，浏览器可能还在运行，下次可以重连
        SetConnectionState(key, ConnectionState.Disconnected);
    }

    /// <summary>设置连接状态并触发事件</summary>
    private void SetConnectionState(string key, ConnectionState state)
    {
        lock (_connections)
        {
            if (_connections.TryGetValue(key, out var conn))
                conn.State = state;
        }
        ConnectionStateChanged?.Invoke(key, state);
    }

    /// <summary>释放所有资源（关闭自启动的浏览器进程）</summary>
    public void Dispose()
    {
        foreach (var timer in _timers.Values)
            timer.Stop();
        _timers.Clear();

        lock (_connections)
        {
            foreach (var conn in _connections.Values)
            {
                try { conn.Client?.Dispose(); } catch { }
                if (conn.LaunchedProcess != null)
                {
                    try { if (!conn.LaunchedProcess.HasExited) conn.LaunchedProcess.Kill(); } catch { }
                }
            }
            _connections.Clear();
        }
    }
}
