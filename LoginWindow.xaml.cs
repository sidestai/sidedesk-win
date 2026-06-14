using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SideDesk
{
    public partial class LoginWindow : Window
    {
        private const string LoginUrl = "https://v4.api.yzllq.com/api/client/login";

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
        }

        private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 初始化WebView2
                await webView.EnsureCoreWebView2Async(null);
                
                // 导航到登录页面
                webView.CoreWebView2.Navigate(LoginUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载登录页面失败：{ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
