using System;
using System.Windows.Controls;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace SideDesk.Controls
{
    /// <summary>
    /// WebView2 模式面板
    /// 负责管理 WebView2 的初始化和导航
    /// </summary>
    public partial class WebViewModePanel : UserControl
    {
        private bool _isInitialized = false;

        public WebViewModePanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 初始化 WebView2
        /// </summary>
        public async void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                await webView.EnsureCoreWebView2Async();
                _isInitialized = true;
                Debug.WriteLine("[WebViewModePanel] WebView2 初始化成功");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebViewModePanel] WebView2 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导航到指定 URL
        /// </summary>
        public void Navigate(string url)
        {
            if (_isInitialized && webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.Navigate(url);
            }
        }

        /// <summary>
        /// 重新加载当前页面
        /// </summary>
        public void Reload()
        {
            if (_isInitialized && webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.Reload();
            }
        }

        /// <summary>
        /// 获取 WebView2 核心实例
        /// </summary>
        public CoreWebView2? GetCoreWebView2()
        {
            return _isInitialized ? webView.CoreWebView2 : null;
        }
    }
}
