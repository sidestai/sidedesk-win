using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk;

public partial class AddWindowDialog : Window
{
    public WindowConfig? Result { get; private set; }

    /// <summary>前端站点基础地址（统一在 Constants.URLs.ServerBaseUrl）</summary>
    private static readonly string FrontendBaseUrl = Constants.URLs.ServerBaseUrl + "/#";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public AddWindowDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        _ = LoadOwnerOptionsAsync();
    }

    /// <summary>加载归属选项：个人 + 团队列表</summary>
    private async System.Threading.Tasks.Task LoadOwnerOptionsAsync()
    {
        ownerCombo.Items.Clear();
        ownerCombo.Items.Add(new ComboBoxItem { Content = "个人", Tag = null });
        ownerCombo.SelectedIndex = 0;

        var app = (App)Application.Current;
        if (string.IsNullOrEmpty(app.AppSettings.AccountToken)) return; // 未登录只有个人

        var teams = await Services.TeamService.Instance.ListTeamsAsync();
        if (teams == null || ownerCombo == null) return;

        foreach (var team in teams)
            ownerCombo.Items.Add(new ComboBoxItem { Content = team.Name, Tag = team.Id });
    }

    private async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        bool isWebView = webviewRadio.IsChecked == true;

        // 归属：个人 / 团队
        string? corpId = (ownerCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        bool isTeam = !string.IsNullOrEmpty(corpId);
        string name = string.IsNullOrWhiteSpace(nameBox.Text) ? "新窗口" : nameBox.Text.Trim();

        string? url = null;
        if (isWebView)
        {
            // 网页模式：不再手输网址，由服务端创建 Web Bar 后拿返回的 ID 拼成固定 URL
            var app = (App)Application.Current;
            if (string.IsNullOrEmpty(app.AppSettings.AccountToken))
            {
                MessageBox.Show("网页模式窗口需要先登录", "无法添加",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? serverId;
            IsEnabled = false; // 防重复点击
            try
            {
                serverId = await CreateWebBarAsync(name, app.AppSettings.AccountToken!, corpId);
            }
            finally
            {
                IsEnabled = true;
            }

            if (string.IsNullOrEmpty(serverId))
            {
                MessageBox.Show("创建网页窗口失败，请稍后重试", "无法添加",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            url = $"{FrontendBaseUrl}/web-bar?bar={Uri.EscapeDataString(serverId)}";
        }

        Result = new WindowConfig
        {
            Name = name,
            Type = isWebView ? WindowType.WebView : WindowType.Native,
            Url = url,
            IsAppMode = false,
            OwnerType = isTeam ? "team" : "personal",
            CorpId = corpId
        };
        DialogResult = true;
        Close();
    }

    /// <summary>POST /api/sidebar/web-bar — 创建 Web Bar（不带网址，可选团队归属），返回服务端加密 ID；失败返回 null</summary>
    private static async Task<string?> CreateWebBarAsync(string title, string token, string? corpId)
    {
        try
        {
            var body = new JObject { ["title"] = title };
            if (!string.IsNullOrEmpty(corpId)) body["corpId"] = corpId; // 团队加密串透传，后端解析并校验成员身份

            using var req = new HttpRequestMessage(HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/web-bar");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Debug.WriteLine($"[AddWindow] 创建 Web Bar {(int)resp.StatusCode}: {json}");

            var root = JObject.Parse(json);
            if (root["code"]?.Value<int>() != 200) return null;
            return root["data"]?["serverId"]?.Value<string>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AddWindow] 创建 Web Bar 失败: {ex.Message}");
            return null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
