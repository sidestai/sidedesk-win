using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

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
            var lbItem = new ListBoxItem 
            { 
                Content = display, 
                Tag = c 
            };
            windowListBox.Items.Add(lbItem);
        }
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
