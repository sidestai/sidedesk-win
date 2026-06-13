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

    private CdpBrowserService() { }

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
    /// 等待 CDP 端口就绪
    /// </summary>
    private async Task<bool> WaitForCdpReadyAsync(int port, TimeSpan timeout)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync($"http://localhost:{port}/json/version");
                if (response.IsSuccessStatusCode) return true;
            }
            catch { }
            await Task.Delay(500);
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

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string? wsUrl = null;

        try
        {
            var json = await httpClient.GetStringAsync($"http://localhost:{port}/json");
            var tabs = JArray.Parse(json);

            foreach (var tab in tabs)
            {
                var tabUrl = tab["url"]?.ToString() ?? "";
                if (tabUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase) || url.StartsWith(tabUrl, StringComparison.OrdinalIgnoreCase))
                {
                    wsUrl = tab["webSocketDebuggerUrl"]?.ToString();
                    if (!string.IsNullOrEmpty(wsUrl)) break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CDP] 获取 Tab 列表失败: {ex.Message}");
            return null;
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
    /// 关闭所有浏览器实例
    /// </summary>
    public void Dispose()
    {
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
