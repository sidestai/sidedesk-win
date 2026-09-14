using System.Windows;

namespace SideDesk;

/// <summary>
/// 文件区项「编辑别名」专用对话框：输入别名，并只读展示原始文件名与原始路径（可自行拖选复制）。
/// 与通用的 <see cref="RenameDialog"/> 分开，避免为单一场景给共用对话框加可选参数与条件显示分支。
/// </summary>
public partial class EditAliasDialog : Window
{
    /// <summary>用户输入的别名（已 Trim）；空字符串表示清除别名、恢复显示原始文件名</summary>
    public string Alias { get; private set; } = "";

    /// <param name="originalName">原始文件名（只读展示，供对照与复制）</param>
    /// <param name="originalPath">原始路径（只读展示）；为空时整行隐藏（如外部网络文件项）</param>
    /// <param name="currentAlias">当前别名；为 null/空表示尚未设置，输入框预填原始文件名</param>
    public EditAliasDialog(string originalName, string? originalPath, string? currentAlias)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        originalNameBox.Text = originalName;
        if (string.IsNullOrEmpty(originalPath))
        {
            originalPathLabel.Visibility = Visibility.Collapsed;
            originalPathHolder.Visibility = Visibility.Collapsed;
        }
        else
        {
            originalPathBox.Text = originalPath;
        }
        aliasBox.Text = string.IsNullOrEmpty(currentAlias) ? originalName : currentAlias;
        aliasBox.SelectAll();
        aliasBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Alias = aliasBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
