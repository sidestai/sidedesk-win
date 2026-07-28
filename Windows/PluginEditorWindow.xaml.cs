using System.Windows;

namespace SideDesk.Windows;

/// <summary>
/// 插件编辑器窗口：内嵌 WebView2 加载前端插件编辑器页面
/// 通过 WebSocket (127.0.0.1:19191) 与宿主通信完成本地编译
/// </summary>
public partial class PluginEditorWindow : Window
{
    /// <summary>前端编辑器页面基础地址</summary>
    private static readonly string EditorBaseUrl =
#if DEBUG
        "http://localhost:3007/#/plugin-editor";
#else
        "http://localhost:3007/#/plugin-editor"; // TODO: 生产环境替换为实际部署地址
#endif

    public PluginEditorWindow()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += PluginEditorWindow_Loaded;
    }

    private async void PluginEditorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // 拼接 token 参数，供前端页面用于后端 API 认证
            var app = (App)Application.Current;
            var token = app.AppSettings.AccountToken ?? "";
            var url = $"{EditorBaseUrl}?token={Uri.EscapeDataString(token)}";
            webView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginEditor] WebView2 初始化失败: {ex.Message}");
            MessageBox.Show($"WebView2 初始化失败: {ex.Message}", "插件编辑器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
