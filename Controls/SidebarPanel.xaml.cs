using System;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

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
            webViewPanel.Navigate(url);
        }
    }
}
