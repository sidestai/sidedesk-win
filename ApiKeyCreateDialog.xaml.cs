using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SideDesk;

/// <summary>
/// 添加 API 密钥对话框：名称（备注）+ 归属（个人/某个团队）。
/// 绑定团队后，该 Key 调接口时默认作用于团队范围。
/// </summary>
public partial class ApiKeyCreateDialog : Window
{
    /// <summary>用户输入的密钥名称（已 Trim）</summary>
    public string KeyName { get; private set; } = "";

    /// <summary>用户选择的归属团队 ID（encrypt2 密文字符串，null=个人；由后端 parseCorpId 反解）</summary>
    public string? CorpId { get; private set; }

    /// <param name="teams">当前用户所在团队列表（归属选项）</param>
    public ApiKeyCreateDialog(List<Services.Team> teams)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

        scopeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("common.personal"), Tag = null });
        foreach (var t in teams)
        {
            // Team.Id 是 encrypt2 密文，原样放入 Tag，创建时传给后端反解，不能 TryParse 成 int
            scopeCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
        }
        scopeCombo.SelectedIndex = 0;
        nameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        KeyName = nameBox.Text.Trim();
        CorpId = (scopeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
