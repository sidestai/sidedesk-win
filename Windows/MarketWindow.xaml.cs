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
    /// <summary>前端站点基础地址</summary>
    private static readonly string FrontendBaseUrl =
#if DEBUG
        "http://localhost:3007/#";
#else
        "http://localhost:3007/#"; // TODO: 生产环境替换为实际部署地址
#endif

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
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // 为所有请求注入 Bearer 认证头
            var app = (App)Application.Current;
            var token = app.AppSettings.AccountToken ?? "";
            if (!string.IsNullOrEmpty(token))
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, args) =>
                {
                    args.Request.Headers.SetHeader("Authorization", "Bearer " + token);
                };
            }

            // 拼接 token 参数，供前端页面直接读取（与插件编辑器同一模式）
            var url = $"{FrontendBaseUrl}{_marketPath}?token={Uri.EscapeDataString(token)}";
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarketWindow] WebView2 初始化失败: {ex.Message}");
            MessageBox.Show($"WebView2 初始化失败: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
