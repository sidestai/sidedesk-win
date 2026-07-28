using System.Windows;
using System.Windows.Controls;
using SideDesk.Models;

namespace SideDesk;

public partial class AddWindowDialog : Window
{
    public WindowConfig? Result { get; private set; }

    public AddWindowDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        // 初始同步一次：此时控件已构造，不依赖 XAML 默认可见性
        UpdateUrlPanel();
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

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateUrlPanel();
    }

    // 网页模式才显示网址输入行
    private void UpdateUrlPanel()
    {
        if (urlPanel != null)
            urlPanel.Visibility = webviewRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        bool isWebView = webviewRadio.IsChecked == true;
        string url = urlBox.Text.Trim();

        // 网页模式下校验网址：低于 10 个字符视为无效，提示后不关闭
        if (isWebView && url.Length < 10)
        {
            MessageBox.Show("请输入有效的网址（至少 10 个字符）", "无法添加",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 归属：个人 / 团队
        string? corpId = (ownerCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        bool isTeam = !string.IsNullOrEmpty(corpId);

        Result = new WindowConfig
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "新窗口" : nameBox.Text.Trim(),
            Type = isWebView ? WindowType.WebView : WindowType.Native,
            Url = isWebView ? url : null,
            IsAppMode = false,
            OwnerType = isTeam ? "team" : "personal",
            CorpId = corpId
        };
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
