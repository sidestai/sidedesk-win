using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SideDesk;

/// <summary>
/// 选择窗口对话框 - 用于从已打开的窗口列表中选择
/// </summary>
public partial class SelectWindowDialog : Window
{
    /// <summary>
    /// 选中的窗口信息列表
    /// </summary>
    public List<(IntPtr Hwnd, string Title, string? ExePath, string? ProcessName, string? ClassName)> SelectedWindows { get; private set; } = new();

    public SelectWindowDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
    }

    /// <summary>
    /// 填充窗口候选列表
    /// </summary>
    public void PopulateCandidates(IEnumerable<(IntPtr Hwnd, string Title, string? ExePath, string? ProcessName, string? ClassName)> candidates)
    {
        windowListBox.Items.Clear();
        foreach (var c in candidates)
        {
            string display = string.IsNullOrEmpty(c.ProcessName) ? c.Title : $"[{c.ProcessName}] {c.Title}";
            var lbItem = new ListBoxItem { Tag = c };

            // 图标 + 文字（标题占满剩余宽度，超长时省略号截断，悬停看全名）
            var dock = new DockPanel { LastChildFill = true };
            var iconSrc = GetExeIconSafe(c.ExePath);
            if (iconSrc != null)
            {
                var img = new Image
                {
                    Source = iconSrc,
                    Width = 16, Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                DockPanel.SetDock(img, Dock.Left);
                dock.Children.Add(img);
            }
            dock.Children.Add(new TextBlock
            {
                Text = display,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = display
            });
            lbItem.Content = dock;

            windowListBox.Items.Add(lbItem);
        }
    }

    /// <summary>从 exe 路径提取应用图标（本地文件 IO，不跨进程，不会卡）。</summary>
    private static BitmapSource? GetExeIconSafe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
            {
                if (icon == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (src.CanFreeze) src.Freeze();
                return src;
            }
        }
        catch { }
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var selected = new List<(IntPtr Hwnd, string Title, string? ExePath, string? ProcessName, string? ClassName)>();
        foreach (ListBoxItem item in windowListBox.SelectedItems)
        {
            if (item.Tag is ValueTuple<IntPtr, string, string?, string?, string?> info)
            {
                selected.Add(info);
            }
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("请选择至少一个窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedWindows = selected;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
