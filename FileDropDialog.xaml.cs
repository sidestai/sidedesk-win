using System.Windows;
using System.Windows.Controls;

namespace SideDesk;

/// <summary>
/// 当文件被拖入窗体，但没有可处理该拖放的目标（如文件区）时弹出的选择窗口。
/// 目前窗口内容为空，仅列出拖入的文件路径，后续可扩展具体处理选项。
/// </summary>
public partial class FileDropDialog : Window
{
    public FileDropDialog(string[] files)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

        if (files != null)
        {
            foreach (var file in files)
            {
                filesPanel.Children.Add(new TextBlock
                {
                    Text = file,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                });
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
