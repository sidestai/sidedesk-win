using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SideDesk;

/// <summary>
/// 主题化消息框，替代系统 MessageBox，与应用深色/浅色主题统一。
/// 无边框、圆角、自定义标题栏（可拖动/关闭）。
/// </summary>
public static class ThemedMessageBox
{
    /// <summary>显示仅含"确定"按钮的消息框</summary>
    public static void Show(Window? owner, string message, string title = "提示")
    {
        ShowCore(owner, message, title, withCancel: false);
    }

    /// <summary>显示"确定 / 取消"确认框，返回是否点击了"确定"</summary>
    public static bool Confirm(Window? owner, string message, string title = "确认")
    {
        return ShowCore(owner, message, title, withCancel: true);
    }

    private static bool ShowCore(Window? owner, string message, string title, bool withCancel)
    {
        var confirmed = false;

        var dialog = new Window
        {
            Title = title,
            Width = 340,
            MinHeight = 150,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false
        };

        var root = new Border
        {
            Background = (Brush)GetResource(owner, "BrushDialogBG"),
            BorderBrush = (Brush)GetResource(owner, "BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题栏
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 内容
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

        // ===== 自定义标题栏 =====
        var titleBar = new Grid { Background = Brushes.Transparent };
        titleBar.MouseLeftButtonDown += (s, e) =>
        {
            if (e.OriginalSource is Button) return;
            if (e.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };

        titleBar.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)GetResource(owner, "BrushTextMain"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 12, 0, 12)
        });

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)GetResource(owner, "GhostButtonStyle"),
            Cursor = Cursors.Hand
        };
        closeBtn.Click += (s, e) => dialog.Close();
        titleBar.Children.Add(closeBtn);
        Grid.SetRow(titleBar, 0);

        // ===== 内容 =====
        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = (Brush)GetResource(owner, "BrushTextMain"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 20)
        };
        Grid.SetRow(messageBlock, 1);

        // ===== 按钮 =====
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        var okBtn = new Button
        {
            Content = "确定",
            Width = 80,
            Style = (Style)GetResource(owner, "PrimaryButtonStyle"), // 高度自适应，避免固定高度裁掉文字下部
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        okBtn.Click += (s, e) => { confirmed = true; dialog.Close(); };

        if (withCancel)
        {
            var cancelBtn = new Button
            {
                Content = "取消",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)GetResource(owner, "StandardButtonStyle"), // 高度自适应，避免固定高度裁掉文字下部
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            cancelBtn.Click += (s, e) => { confirmed = false; dialog.Close(); };
            // 取消在左、确定在右
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
        }
        else
        {
            btnPanel.Children.Add(okBtn);
        }

        Grid.SetRow(btnPanel, 2);

        grid.Children.Add(titleBar);
        grid.Children.Add(messageBlock);
        grid.Children.Add(btnPanel);
        root.Child = grid;
        dialog.Content = root;

        dialog.ShowDialog();
        return confirmed;
    }

    private static object GetResource(Window? owner, string key)
    {
        return owner?.TryFindResource(key) ?? Application.Current.TryFindResource(key);
    }
}
