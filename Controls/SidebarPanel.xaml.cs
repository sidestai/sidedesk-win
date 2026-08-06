using System;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using SideDesk.Models;

namespace SideDesk.Controls
{
    /// <summary>
    /// 显示模式枚举
    /// </summary>
    public enum DisplayMode
    {
        WebView,    // WebView2 模式
        Native      // 本地 UI 模式
    }

    /// <summary>
    /// SidebarPanel.xaml 的交互逻辑
    /// 支持在 WebView2 和本地 UI 之间切换
    /// 作为容器管理 WebViewModePanel 和 NativeModePanel
    /// </summary>
    public partial class SidebarPanel : UserControl
    {
        private MainWindow? _mainWindow;
        private DisplayMode _currentMode = DisplayMode.WebView;
        private string? _currentWebViewWindowId;

        public DisplayMode CurrentMode => _currentMode;

        public SidebarPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 设置主窗口引用
        /// </summary>
        public void SetMainWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            
            // 传递给子面板
            nativePanel.SetMainWindow(mainWindow);
        }

        /// <summary>
        /// 初始化 WebView2
        /// </summary>
        public void InitializeWebView()
        {
            webViewPanel.Initialize();
            Debug.WriteLine("[SidebarPanel] WebView2 面板已初始化");
        }

        /// <summary>
        /// 初始化本地 UI 内容
        /// </summary>
        public void InitializeNativeContent()
        {
            nativePanel.Initialize();
            Debug.WriteLine("[SidebarPanel] 本地 UI 面板已初始化");
        }

        /// <summary>
        /// 切换到 WebView2 模式
        /// </summary>
        public void SwitchToWebViewMode()
        {
            _currentMode = DisplayMode.WebView;
            webViewPanel.Visibility = Visibility.Visible;
            nativePanel.Visibility = Visibility.Collapsed;
            
            // 确保 WebView2 已初始化
            InitializeWebView();
            
            Debug.WriteLine("[SidebarPanel] 切换到 WebView2 模式");
        }

        /// <summary>
        /// 切换到本地 UI 模式
        /// </summary>
        public void SwitchToNativeMode()
        {
            _currentMode = DisplayMode.Native;
            webViewPanel.Visibility = Visibility.Collapsed;
            nativePanel.Visibility = Visibility.Visible;
            
            // 初始化本地 UI 内容
            InitializeNativeContent();
            
            Debug.WriteLine("[SidebarPanel] 切换到本地 UI 模式");
        }

        /// <summary>
        /// 加载指定的 URL
        /// </summary>
        public void NavigateToUrl(string url)
        {
            webViewPanel.Navigate(AppendTokenForFrontend(url));
        }

        /// <summary>
        /// 根据窗口配置切换显示内容
        /// </summary>
        public void SwitchToWindow(WindowConfig? config, WindowConfigManager? manager = null)
        {
            if (config == null) return;

            if (config.Type == WindowType.WebView)
            {
                SwitchToWebViewMode();
                // 只有切换到不同的 WebView 窗口时才尝试 Navigate
                // 切换回同一个窗口时保留已有状态，不重新加载
                if (_currentWebViewWindowId != config.Id)
                {
                    if (!string.IsNullOrEmpty(config.Url))
                    {
                        webViewPanel.Navigate(AppendTokenForFrontend(config.Url));
                    }
                    _currentWebViewWindowId = config.Id;
                }
            }
            else
            {
                SwitchToNativeMode();
                if (manager != null)
                {
                    nativePanel.LoadWindow(config, manager);
                }
            }
        }

        /// <summary>
        /// 为指向本地前端站点的 URL 追加 token 参数（内嵌页从 query 读取 token 完成认证）；
        /// 其它 URL 原样返回。URL 本身不存 token，避免持久化泄露。
        /// </summary>
        private static string AppendTokenForFrontend(string url)
        {
            string prefix = Constants.URLs.ServerBaseUrl + "/";
            if (string.IsNullOrEmpty(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return url;

            var app = (App)Application.Current;
            string token = app.AppSettings.AccountToken ?? "";
            if (string.IsNullOrEmpty(token)) return url;

            string sep = url.Contains('?') ? "&" : "?";
            return $"{url}{sep}token={Uri.EscapeDataString(token)}";
        }

        /// <summary>重新渲染原生模式面板（设置变更后调用）。</summary>
        public void RefreshNativePanels() => nativePanel.RefreshLayout();

        /// <summary>好友资料变更通知（WS 推送）：转发给原生面板更新好友缓存。</summary>
        public void UpdateFriendProfile(string userId, string? nickName, string? avatar)
            => nativePanel.UpdateFriendProfile(userId, nickName, avatar);

        /// <summary>好友上/下线通知（WS 推送）：转发给原生面板更新好友在线状态。</summary>
        public void UpdateFriendOnlineStatus(string userId, bool online)
            => nativePanel.UpdateFriendOnlineStatus(userId, online);

        /// <summary>填充"工具"菜单（窗口右上角菜单与底部右键菜单共用同一份工具列表）。</summary>
        public void PopulateToolsMenu(System.Windows.Controls.MenuItem menu) => nativePanel.PopulateToolsMenu(menu);
        
        /// <summary>填充"添加模块"菜单（窗口右上角菜单使用，与空白处右键菜单内容一致）。</summary>
        public void PopulateAddModuleMenu(System.Windows.Controls.MenuItem menu) => nativePanel.PopulateAddModuleMenu(menu);

        /// <summary>清空内容区域，显示空白（所有小屏均不可见时调用）。</summary>
        public void ClearContent()
        {
            webViewPanel.Visibility = Visibility.Collapsed;
            nativePanel.Visibility = Visibility.Collapsed;
        }
    }
}
