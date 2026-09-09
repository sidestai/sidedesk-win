using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services.Plugins;

/// <summary>
/// HTML 组件模式插件的唯一 WebView2 沙箱封装（模块页与设置页共用）。
///
/// 沙箱模型（对齐 Chrome extension 的受控运行环境）：
/// - 每个插件一个专属虚拟主机：https://sd-plugin-&lt;id&gt;.local/，只读映射到插件目录；
/// - 文档响应由宿主经 WebResourceRequested 下发并注入 CSP（不信任插件自带 meta）；
/// - 导航白名单：仅允许本虚拟主机，外链一律取消并转系统默认浏览器；
/// - 禁 DevTools / 状态栏 / 默认右键菜单，window.open 转系统浏览器；
/// - 注入 window.sideDesk 桥（Promise 化），消息信封版本化 {v:1,...}；
/// - 红线：桥绝不透传 AccountToken 或任何凭据。
/// </summary>
public class HtmlPluginHost : UserControl, IDisposable
{
    private readonly PluginConfig _config;
    private readonly string _pluginDirectory;
    private readonly string _htmlFile;
    private readonly string _virtualHost;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// 创建 HTML 插件宿主控件
    /// </summary>
    /// <param name="config">插件配置（plugin.json）</param>
    /// <param name="htmlFile">要装载的 HTML 文件（相对于插件目录，如 module.html / settings.html）</param>
    /// <param name="pluginDirectory">插件根目录绝对路径</param>
    public HtmlPluginHost(PluginConfig config, string htmlFile, string pluginDirectory)
    {
        _config = config;
        _htmlFile = htmlFile;
        _pluginDirectory = pluginDirectory;
        _virtualHost = BuildVirtualHostName(config.Id);

        _webView = new Microsoft.Web.WebView2.Wpf.WebView2();
        Content = _webView;

        Loaded += HtmlPluginHost_Loaded;
        App.ThemeChanged += OnAppThemeChanged;
    }

    /// <summary>插件专属虚拟主机名（id 中非法字符替换为连字符，保证可作为主机名）</summary>
    private static string BuildVirtualHostName(string pluginId)
    {
        var sanitized = new string(pluginId
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrEmpty(sanitized)) sanitized = "plugin";
        return $"sd-plugin-{sanitized}.local";
    }

    private async void HtmlPluginHost_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复附加（置顶切换/局部刷新）时不重复初始化
        if (_initialized || _disposed) return;
        try
        {
            // 共享默认 WebView2 Environment（多插件多实例共用运行时，降低内存开销）
            await _webView.EnsureCoreWebView2Async(await SideDesk.Services.WebView2Shared.GetAsync());
            if (_disposed) return;
            var core = _webView.CoreWebView2;

            // 沙箱化设置：禁 DevTools / 状态栏 / 默认右键菜单
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;

            // 虚拟主机 → 插件目录只读映射（静态资源经此解析）
            core.SetVirtualHostNameToFolderMapping(
                _virtualHost, _pluginDirectory, CoreWebView2HostResourceAccessKind.Allow);

            // 文档请求由宿主下发并注入 CSP 响应头（不信任插件自带 meta）
            core.WebResourceRequested += Core_WebResourceRequested;
            core.AddWebResourceRequestedFilter($"https://{_virtualHost}/*", CoreWebView2WebResourceContext.Document);

            // 导航白名单：非本虚拟主机的导航一律取消，外链转系统浏览器
            core.NavigationStarting += Core_NavigationStarting;

            // window.open / target=_blank 不放行，外链转系统浏览器
            core.NewWindowRequested += Core_NewWindowRequested;

            // 注入 window.sideDesk 桥（每个文档创建时执行）
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

            core.WebMessageReceived += Core_WebMessageReceived;

            // 导航到模块/设置页（主题参数沿用 App.FrontendThemeParam 约定）
            core.Navigate($"https://{_virtualHost}/{_htmlFile}?theme={App.FrontendThemeParam}");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlPluginHost] WebView2 init failed ({_config.Id}): {ex.Message}");
            Content = new TextBlock
            {
                Text = Framework.Lang.TF("common.webview2InitializationFailed", ex.Message),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12)
            };
        }
    }

    // ==================== 沙箱：CSP 注入 / 导航拦截 ====================

    /// <summary>宿主自服务插件文档并注入 CSP 响应头（脚本仅允许同源与内联，外链脚本被阻断）</summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-src 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'";

    private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.Equals(_virtualHost, StringComparison.OrdinalIgnoreCase)) return;

            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (string.IsNullOrEmpty(relativePath)) relativePath = _htmlFile;

            var core = _webView.CoreWebView2;
            string headers;
            System.IO.Stream body;
            int status;
            string reason;

            var fullPath = Path.GetFullPath(Path.Combine(_pluginDirectory, relativePath));
            var dirFull = Path.GetFullPath(_pluginDirectory);
            // 路径越界防护：拒绝跳出插件目录的请求
            if (!fullPath.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                status = 404; reason = "Not Found";
                body = new MemoryStream();
                headers = "Content-Type: text/plain; charset=utf-8";
            }
            else
            {
                status = 200; reason = "OK";
                body = new MemoryStream(File.ReadAllBytes(fullPath));
                headers = "Content-Type: text/html; charset=utf-8\r\n" +
                          "Content-Security-Policy: " + ContentSecurityPolicy;
            }
            e.Response = core.Environment.CreateWebResourceResponse(body, status, reason, headers);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlPluginHost] serve document failed: {ex.Message}");
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Uri);
            if (uri.Host.Equals(_virtualHost, StringComparison.OrdinalIgnoreCase)) return; // 本插件域放行

            e.Cancel = true;
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                OpenInSystemBrowser(e.Uri); // 外链转系统默认浏览器
        }
        catch { /* 非标准 URL（about:blank 等）不影响主流程 */ }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // 禁 window.open：外链转系统浏览器
        if (!string.IsNullOrEmpty(e.Uri)
            && (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            OpenInSystemBrowser(e.Uri);
    }

    /// <summary>用系统默认浏览器打开链接（参照 NotificationActionService.OpenUrl）</summary>
    private static void OpenInSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlPluginHost] open url failed: {ex.Message}");
        }
    }

    // ==================== 消息桥 ====================

    /// <summary>
    /// window.sideDesk 桥脚本（Promise 化）：
    /// call(method, params) → Promise；onEvent(cb) 订阅宿主事件（如 themeChanged）。
    /// </summary>
    private const string BridgeScript = @"
(function () {
  if (window.sideDesk) return;
  var pending = {};
  var seq = 0;
  var eventHandlers = [];
  window.chrome.webview.addEventListener('message', function (e) {
    var msg = e.data || {};
    if (msg.v !== 1) return;
    if (msg.event) {
      for (var i = 0; i < eventHandlers.length; i++) {
        try { eventHandlers[i](msg.data, msg.event); } catch (_) { }
      }
      return;
    }
    if (msg.id != null && pending[msg.id]) {
      var p = pending[msg.id];
      delete pending[msg.id];
      if (msg.ok) p.resolve(msg.data);
      else p.reject(msg.error || { code: 'unknown', message: 'unknown error' });
    }
  });
  function call(method, params) {
    return new Promise(function (resolve, reject) {
      var id = ++seq;
      pending[id] = { resolve: resolve, reject: reject };
      window.chrome.webview.postMessage({ v: 1, id: id, method: method, params: params == null ? null : params });
    });
  }
  window.sideDesk = {
    apiVersion: 1,
    call: call,
    getConfig: function () { return call('getConfig'); },
    setConfig: function (data) { return call('setConfig', data); },
    openUrl: function (url) { return call('openUrl', { url: url }); },
    onEvent: function (cb) {
      eventHandlers.push(cb);
      return function () {
        var i = eventHandlers.indexOf(cb);
        if (i >= 0) eventHandlers.splice(i, 1);
      };
    }
  };
})();";

    /// <summary>
    /// 消息信封分发：{v:1, id, method, params} → getConfig / setConfig / openUrl；
    /// 未注册 method 一律结构化错误响应。桥绝不透传 AccountToken / 任何凭据。
    /// </summary>
    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JObject.Parse(e.WebMessageAsJson);
            if (msg.Value<int?>("v") != 1) return;

            var id = msg["id"];
            var method = msg.Value<string?>("method") ?? "";
            var parameters = msg["params"];

            switch (method)
            {
                case "getConfig":
                    Respond(id, true, HtmlPluginDataStore.Load(_config.Id), null);
                    break;

                case "setConfig":
                    if (parameters is not JObject obj)
                    {
                        Respond(id, false, null, new JObject { ["code"] = "invalidParams", ["message"] = "setConfig 参数必须是 JSON 对象" });
                        break;
                    }
                    HtmlPluginDataStore.Save(_config.Id, obj);
                    Respond(id, true, null, null);
                    break;

                case "openUrl":
                {
                    var url = (parameters as JObject)?.Value<string?>("url") ?? "";
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(id, false, null, new JObject { ["code"] = "invalidParams", ["message"] = "仅支持 http/https 链接" });
                        break;
                    }
                    OpenInSystemBrowser(url);
                    Respond(id, true, null, null);
                    break;
                }

                default:
                    // 未注册 method：结构化拒绝
                    Respond(id, false, null, new JObject { ["code"] = "unknownMethod", ["message"] = $"未知方法: {method}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlPluginHost] web message error ({_config.Id}): {ex.Message}");
        }
    }

    /// <summary>按信封回包：成功 {v:1,id,ok:true,data} / 失败 {v:1,id,ok:false,error:{code,message}}</summary>
    private void Respond(JToken? id, bool ok, JToken? data, JObject? error)
    {
        if (_disposed || _webView.CoreWebView2 == null) return;
        var response = new JObject
        {
            ["v"] = 1,
            ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
            ["ok"] = ok
        };
        if (ok) response["data"] = data ?? JValue.CreateNull();
        else response["error"] = error;
        try { _webView.CoreWebView2.PostWebMessageAsJson(response.ToString(Formatting.None)); }
        catch (Exception ex) { Debug.WriteLine($"[HtmlPluginHost] post response failed: {ex.Message}"); }
    }

    // ==================== 主题广播 ====================

    private void OnAppThemeChanged()
    {
        if (_disposed) return;
        Action broadcast = () =>
        {
            if (_disposed || !_initialized || _webView.CoreWebView2 == null) return;
            var evt = new JObject
            {
                ["v"] = 1,
                ["event"] = "themeChanged",
                ["data"] = new JObject { ["theme"] = App.FrontendThemeParam }
            };
            try { _webView.CoreWebView2.PostWebMessageAsJson(evt.ToString(Formatting.None)); }
            catch (Exception ex) { Debug.WriteLine($"[HtmlPluginHost] broadcast theme failed: {ex.Message}"); }
        };
        if (Dispatcher.CheckAccess()) broadcast();
        else Dispatcher.BeginInvoke(broadcast);
    }

    // ==================== 释放 ====================

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        App.ThemeChanged -= OnAppThemeChanged;
        try
        {
            var core = _webView.CoreWebView2;
            if (core != null)
            {
                core.ClearVirtualHostNameToFolderMapping(_virtualHost);
                core.WebResourceRequested -= Core_WebResourceRequested;
                core.NavigationStarting -= Core_NavigationStarting;
                core.NewWindowRequested -= Core_NewWindowRequested;
                core.WebMessageReceived -= Core_WebMessageReceived;
            }
        }
        catch { /* WebView2 可能已随窗口销毁 */ }
        try { _webView.Dispose(); }
        catch { /* ignore */ }
    }
}

/// <summary>
/// HTML 插件私有存储：每个插件一个命名空间文件
/// %LocalAppData%\SideDesk\plugin-data\&lt;pluginId&gt;.json（读写一律经宿主桥，插件不可直接触及其他插件数据）
/// </summary>
internal static class HtmlPluginDataStore
{
    private static readonly object _lock = new();

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDesk", "plugin-data");

    private static string GetFilePath(string pluginId)
    {
        // 文件名清洗，避免插件 id 携带路径字符
        var safe = new string(pluginId.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "plugin";
        return Path.Combine(DataDirectory, safe + ".json");
    }

    /// <summary>读取插件私有配置（不存在返回空对象）</summary>
    public static JObject Load(string pluginId)
    {
        lock (_lock)
        {
            try
            {
                var path = GetFilePath(pluginId);
                if (!File.Exists(path)) return new JObject();
                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HtmlPluginDataStore] load failed ({pluginId}): {ex.Message}");
                return new JObject();
            }
        }
    }

    /// <summary>整体写入插件私有配置（覆盖式）</summary>
    public static void Save(string pluginId, JObject data)
    {
        lock (_lock)
        {
            var path = GetFilePath(pluginId);
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(path, data.ToString(Formatting.Indented));
        }
    }
}
