using System.Windows;

namespace SideDesk;

public partial class EditWindowDialog : Window
{
    public string NewName { get; private set; } = "";

    public EditWindowDialog(string currentName)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        nameBox.Text = currentName;
        nameBox.SelectAll();
        nameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewName = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("窗口") : nameBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
