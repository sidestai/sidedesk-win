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
            var schemeReg = new CoreWebView2CustomSchemeRegistration("desktxp")
            {
                HasAuthorityComponent = true, // desktxp://sidedesk/... 含 host 段
                TreatAsSecure = false,
                AllowedOrigins = new System.Collections.Generic.List<string> { Constants.URLs.ServerBaseOrigin }
            };
            // SDK 1.0.4129.50：CustomSchemeRegistrations 只读且默认 null，必须经构造函数传入
            var options = new CoreWebView2EnvironmentOptions(
                null, null, null, false,
                new System.Collections.Generic.List<CoreWebView2CustomSchemeRegistration> { schemeReg });
            var env = await CoreWebView2Environment.CreateAsync(null, null, options);
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
                    var (status, json) = Services.Plugins.PluginMarketBridge.HandleScheme(args.Request.Uri);
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

            // 拼接 token 参数，供前端页面直接读取（与插件编辑器同一模式）
            var url = $"{FrontendBaseUrl}{_marketPath}?token={Uri.EscapeDataString(token)}";
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarketWindow] WebView2 初始化失败: {ex.Message}");
            MessageBox.Show(Framework.Lang.TF("WebView2 初始化失败: {0}", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
