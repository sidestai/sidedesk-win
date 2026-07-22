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
        private string? _pendingUrl = null;

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

                // 初始化完成后，执行待处理的导航
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    var pendingUrl = _pendingUrl;
                    _pendingUrl = null;
                    Debug.WriteLine($"[WebViewModePanel] 执行待处理导航: {pendingUrl}");
                    webView.CoreWebView2.Navigate(pendingUrl);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebViewModePanel] WebView2 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导航到指定 URL（如果当前已是该URL则跳过）
        /// </summary>
        public void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();

            if (_isInitialized && webView.CoreWebView2 != null)
            {
                // 如果当前已是该URL，不重复加载
                var current = webView.Source?.ToString()?.TrimEnd('/');
                var target = url.TrimEnd('/');

                // about:blank 或 null 不应阻止导航
                if (!string.IsNullOrEmpty(current)
                    && !current.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[WebViewModePanel] URL相同，跳过Navigate: {url}");
                    return;
                }

                try
                {
                    webView.CoreWebView2.Navigate(url);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebViewModePanel] Navigate 失败（URL无效）: {url} - {ex.Message}");
                }
            }
            else
            {
                // WebView2 尚未初始化完成，保存待处理URL
                _pendingUrl = url;
                Debug.WriteLine($"[WebViewModePanel] WebView2未就绪，URL已暂存: {url}");
            }
        }

        /// <summary>
        /// 获取当前已加载的 URL
        /// </summary>
        public string? GetCurrentUrl()
        {
            return webView?.Source?.ToString();
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
