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
    public static void Show(Window? owner, string message, string title = null)
    {
        ShowCore(owner, message, title ?? Framework.Lang.T("common.notice"), withCancel: false);
    }

    /// <summary>显示"确定 / 取消"确认框，返回是否点击了"确定"</summary>
    public static bool Confirm(Window? owner, string message, string title = null)
    {
        return ShowCore(owner, message, title ?? Framework.Lang.T("common.confirm"), withCancel: true);
    }

    /// <summary>
    /// 显示分享结果窗口：短码与链接各带「复制」按钮，可单独复制。
    /// </summary>
    public static void ShowShareResult(Window? owner, string shareCode, string shareLink, string title = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
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

        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 提示文字
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 短码行
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 链接行
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 关闭按钮

        // 标题栏
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
            Content = "\u2715",
            Width = 26, Height = 26, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)GetResource(owner, "GhostButtonStyle"),
            Cursor = Cursors.Hand
        };
        closeBtn.Click += (s, e) => dialog.Close();
        titleBar.Children.Add(closeBtn);
        Grid.SetRow(titleBar, 0);

        // 提示文字
        var hint = new TextBlock
        {
            Text = Framework.Lang.T("messageBox.shareCreatedHint"),
            FontSize = 12,
            Foreground = (Brush)GetResource(owner, "BrushTextSub"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 12)
        };
        Grid.SetRow(hint, 1);

        // 辅助方法：构建「标签 + 只读文本框 + 复制按钮」行
        Grid MakeCopyRow(string label, string value, int rowIndex)
        {
            var row = new Grid { Margin = new Thickness(16, 0, 16, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)GetResource(owner, "BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(lbl, 0);

            var tb = new TextBox
            {
                Text = value,
                FontSize = 12,
                IsReadOnly = true,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)GetResource(owner, "BrushBorder"),
                Background = (Brush)GetResource(owner, "BrushInputBG"),
                Foreground = (Brush)GetResource(owner, "BrushTextMain"),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };
            Grid.SetColumn(tb, 1);

            var btn = new Button
            {
                Content = Framework.Lang.T("common.copy"),
                Width = 52,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)GetResource(owner, "StandardButtonStyle"),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) =>
            {
                Clipboard.SetText(value);
                btn.Content = Framework.Lang.T("settings.copiedCheck");
            };
            Grid.SetColumn(btn, 2);

            row.Children.Add(lbl);
            row.Children.Add(tb);
            row.Children.Add(btn);
            Grid.SetRow(row, rowIndex);
            return row;
        }

        var codeRow = MakeCopyRow(Framework.Lang.T("messageBox.shortCode"), shareCode, 2);
        var linkRow = MakeCopyRow(Framework.Lang.T("messageBox.link"), shareLink, 3);

        // 关闭按钮
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 16)
        };
        var okBtn = new Button
        {
            Content = Framework.Lang.T("common.close"),
            Width = 80,
            Style = (Style)GetResource(owner, "PrimaryButtonStyle"),
            Cursor = Cursors.Hand,
            IsDefault = true,
            IsCancel = true
        };
        okBtn.Click += (s, e) => dialog.Close();
        btnPanel.Children.Add(okBtn);
        Grid.SetRow(btnPanel, 4);

        outerGrid.Children.Add(titleBar);
        outerGrid.Children.Add(hint);
        outerGrid.Children.Add(codeRow);
        outerGrid.Children.Add(linkRow);
        outerGrid.Children.Add(btnPanel);
        root.Child = outerGrid;
        dialog.Content = root;
        dialog.ShowDialog();
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
            // GhostButtonStyle 默认 Padding=16,10 会把 26px 按钮的内容区压没，必须置 0
            Padding = new Thickness(0),
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
            Content = Framework.Lang.T("common.ok"),
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
                Content = Framework.Lang.T("common.cancel"),
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
