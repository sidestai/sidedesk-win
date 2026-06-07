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
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (urlBox != null)
            urlBox.IsEnabled = webviewRadio.IsChecked == true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new WindowConfig
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "新窗口" : nameBox.Text.Trim(),
            Type = webviewRadio.IsChecked == true ? WindowType.WebView : WindowType.Native,
            Url = webviewRadio.IsChecked == true ? urlBox.Text.Trim() : null,
            IsAppMode = false
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
