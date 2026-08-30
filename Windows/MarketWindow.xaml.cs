using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SideDesk.Windows;

/// <summary>
/// 通用市场窗口：内嵌 WebView2 加载前端市场页面（应用市场 / 网页市场 / 插件市场等）。
/// 自动为所有请求注入 Authorization: Bearer 头，同时通过 query 参数传递 token 供页面读取。
/// </summary>
public partial class MarketWindow : Window
{
    /// <summary>前端站点基础地址（统一在 Constants.URLs.ServerBaseUrl）</summary>
    private static readonly string FrontendBaseUrl = Constants.URLs.FrontendBaseUrl + "/#";

    private readonly string _marketPath;

    /// <summary>
    /// 创建市场窗口
    /// </summary>
    /// <param name="marketPath">市场页面路由路径（如 /app-market）</param>
    /// <param name="title">窗口标题</param>
    public MarketWindow(string marketPath, string title)
    {
        InitializeComponent();
        _marketPath = marketPath;
        Title = title;
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += MarketWindow_Loaded;
    }

    /// <summary>打开指定市场窗口（静态便捷方法）</summary>
    public static void Open(string marketPath, string title)
    {
        var win = new MarketWindow(marketPath, title);
        win.Show();
    }

    private async void MarketWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 注册 desktxp:// 自定义 scheme：未注册的 scheme 上 fetch 会被 Chromium 网络栈直接拒绝（ERR_UNKNOWN_URL_SCHEME），
            // 不会触发 WebResourceRequested；注册后才能拦截请求并伪造响应（插件市场本地桥）
            // origin 白名单从本页实际加载的 URL 反推（与 FrontendBaseUrl 同源派生），
            // 生产（https 域名）与本地联调（http://localhost:3007）自动匹配，不依赖独立维护的常量；
            // 格式要求 scheme://host[:port]，无尾斜杠（GetLeftPart(Authority) 恰好满足）
            var pageOrigin = new Uri(FrontendBaseUrl).GetLeftPart(UriPartial.Authority);
            var schemeReg = new CoreWebView2CustomSchemeRegistration("desktxp")
            {
                HasAuthorityComponent = true, // desktxp://sidedesk/... 含 host 段
                // 生产环境页面为 https，fetch 非安全 scheme 会被混合内容策略拦截；
                // 置 true 后两种环境均可达（安全 scheme 从 http 页面请求不受限）
                TreatAsSecure = true,
                AllowedOrigins = new System.Collections.Generic.List<string> { pageOrigin }
            };
            // SDK 1.0.4129.50：CustomSchemeRegistrations 只读且默认 null，必须经构造函数传入
            var options = new CoreWebView2EnvironmentOptions(
                null, null, null, false,
                new System.Collections.Generic.List<CoreWebView2CustomSchemeRegistration> { schemeReg });
            // 必须使用独立用户数据目录：登录窗 / AI 对话 / 数据模块等其他 WebView2 均以默认 Environment
            //（无自定义 scheme）先行启动共享浏览器进程，而浏览器进程启动后 Environment 选项即锁定，
            // 再复用默认目录时本 scheme 注册会被静默忽略（表现为页面正常加载但桥探测失败、显示"未连接客户端"）
            var marketUserDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDesk", "WebView2.Market");
            var env = await CoreWebView2Environment.CreateAsync(null, marketUserDataFolder, options);
            await webView.EnsureCoreWebView2Async(env);
            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // 为所有请求注入 Bearer 认证头；同时拦截 desktxp:// 自定义协议
            //（插件市场本地桥：查询已装 / 安装 / 拉源码，见 PluginMarketBridge）
            var app = (App)Application.Current;
            var token = app.AppSettings.AccountToken ?? "";
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, args) =>
            {
                if (args.Request.Uri.StartsWith("desktxp://", StringComparison.OrdinalIgnoreCase))
                {
                    // 数据模板市场走独立桥；其余（插件查询/安装/拉源码）走 PluginMarketBridge
                    var (status, json) = args.Request.Uri.Contains("/data-templates/", StringComparison.OrdinalIgnoreCase)
                        ? Services.DataTemplateMarketBridge.HandleScheme(args.Request.Uri)
                        : Services.Plugins.PluginMarketBridge.HandleScheme(args.Request.Uri);
                    var body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                    args.Response = core.Environment.CreateWebResourceResponse(
                        body, status, "OK", "application/json");
                    // 本页源为前端站点，与 scheme 跨源：放行任意源
                    args.Response.Headers.AppendHeader("Access-Control-Allow-Origin", "*");
                    return;
                }
                if (!string.IsNullOrEmpty(token))
                    args.Request.Headers.SetHeader("Authorization", "Bearer " + token);
            };

            // 拼接 token 参数，供前端页面直接读取（与插件编辑器同一模式）；
            // theme 参数传递宿主当前主题（dark/light），使市场页面按一致的主题风格渲染
            var url = $"{FrontendBaseUrl}{_marketPath}?token={Uri.EscapeDataString(token)}&theme={App.FrontendThemeParam}";
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarketWindow] WebView2 init failed: {ex.Message}");
            MessageBox.Show(Framework.Lang.TF("common.webview2InitializationFailed", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
