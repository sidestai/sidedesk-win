using System.Windows;

namespace SideDesk;

public partial class RenameDialog : Window
{
    /// <summary>用户输入的新名称（已 Trim；可能为空字符串，表示恢复默认标题）</summary>
    public string NewName { get; private set; } = "";

    public RenameDialog(string title, string currentName)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Title = title;
        nameBox.Text = currentName;
        nameBox.SelectAll();
        nameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewName = nameBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
