using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideDesk.Windows;

/// <summary>
/// 网页版 AI Chat 窗口：内嵌 WebView2 加载前端聊天页面。
/// LLM 调用经本机 HTTP 代理（127.0.0.1:HttpServerPort）转发，API Key 不出本机。
/// </summary>
public partial class AIChatWebWindow : Window
{
    /// <summary>前端聊天页基础地址（基址统一在 Constants.URLs.ServerBaseUrl）</summary>
    private static readonly string ChatBaseUrl = Constants.URLs.FrontendBaseUrl + "/#/ai-chat";

    public AIChatWebWindow()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
        Loaded += AIChatWebWindow_Loaded;
    }

    /// <summary>自定义标题栏按住拖动窗口（点在关闭按钮上时不触发）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.ClickCount == 2) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private async void AIChatWebWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // proxy 参数告知页面本机 HTTP 代理地址（端口可在设置中修改）
            // token 供页面在登录态下访问后端 API（未登录时为空，页面自动走本地能力）
            // theme 传递宿主当前主题（dark/light），使页面按一致的主题风格渲染
            var app = (App)Application.Current;
            var proxy = $"http://127.0.0.1:{Services.HttpServerService.Instance.CurrentPort}";
            var token = app.AppSettings.AccountToken ?? "";
            var url = $"{ChatBaseUrl}?proxy={Uri.EscapeDataString(proxy)}&token={Uri.EscapeDataString(token)}&theme={App.FrontendThemeParam}";
            webView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AIChatWeb] WebView2 init failed: {ex.Message}");
            MessageBox.Show(Framework.Lang.TF("common.webview2InitializationFailed", ex.Message), "AI Chat", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
