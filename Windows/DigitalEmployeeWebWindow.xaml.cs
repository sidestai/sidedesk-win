using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SideDesk.Models;

namespace SideDesk.Windows;

/// <summary>
/// 数字员工后台窗口：内嵌 WebView2 加载前端数字员工管理页面。
/// 自动为所有请求注入 Authorization: Bearer 头，同时通过 query 参数传递 token 供页面读取。
/// </summary>
public partial class DigitalEmployeeWebWindow : Window
{
    /// <summary>前端站点基础地址（统一在 Constants.URLs.ServerBaseUrl）</summary>
    private static readonly string FrontendBaseUrl = Constants.URLs.ServerBaseUrl + "/#";

    private readonly DigitalEmployee? _employee;

    /// <summary>
    /// 创建数字员工后台窗口
    /// </summary>
    /// <param name="employee">要查看的员工（可为 null，null 时显示全部员工）</param>
    public DigitalEmployeeWebWindow(DigitalEmployee? employee = null)
    {
        InitializeComponent();
        _employee = employee;
        Title = employee != null ? $"数字员工后台 - {employee.Name}" : "数字员工后台";
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += DigitalEmployeeWebWindow_Loaded;
    }

    private async void DigitalEmployeeWebWindow_Loaded(object sender, RoutedEventArgs e)
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

            // 拼接参数：token 供前端认证，name 供页面定位到指定员工
            var url = $"{FrontendBaseUrl}/digital-employee?token={Uri.EscapeDataString(token)}";
            if (_employee != null && !string.IsNullOrWhiteSpace(_employee.Name))
                url += $"&name={Uri.EscapeDataString(_employee.Name)}";
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DigitalEmployeeWebWindow] WebView2 初始化失败: {ex.Message}");
            MessageBox.Show($"WebView2 初始化失败: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
