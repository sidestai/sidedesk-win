using System;
using System.Windows;

namespace SideDesk.Windows;

/// <summary>
/// 通用网页窗口：内嵌 WebView2 加载任意 URL（独立窗口，供通知 open_webview 动作等场景调用）。
/// </summary>
public partial class WebViewWindow : Window
{
    private readonly string _url;

    /// <summary>
    /// 创建通用网页窗口
    /// </summary>
    /// <param name="url">要加载的网址</param>
    /// <param name="title">窗口标题（默认"网页"）</param>
    public WebViewWindow(string url, string title = null)
    {
        InitializeComponent();
        _url = url;
        Title = title ?? Framework.Lang.T("common.web");
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += WebViewWindow_Loaded;
    }

    private async void WebViewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebViewWindow] WebView2 初始化失败: {ex.Message}");
            MessageBox.Show(Framework.Lang.TF("common.webview2InitializationFailed", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
