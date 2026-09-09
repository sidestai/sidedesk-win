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
    public static void ShowShareResult(Window? owner, string shareCode, string shareLink, string? title = null)
    {
        // Window.Title 不接受 null（否则抛“'' 不是属性 Title 的有效值”）；兜底为“创建分享”，自定义标题栏复用同一文案
        if (string.IsNullOrEmpty(title)) title = Framework.Lang.T("settings.createShare");
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
                // 用 MinWidth 取代写死的 Width：StandardButtonStyle 的 Padding=16,8 会吃掉固定宽度，
                // 导致「复制」尤其点击后的「已复制 ✓」（及英/俄/日等更长文案）被裁切显示不全；
                // 改为自适应内容宽度（所在列已是 Auto）并收窄内边距让小按钮更紧凑，MinWidth 保底。
                MinWidth = 56,
                Padding = new Thickness(12, 6, 12, 6),
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

    /// <summary>
    /// 统一的“创建分享”对话框：选择分享模式（复制/引用）与导入方权限，确认后 POST /api/sidebar/share/create，
    /// 成功则弹出分享结果窗（短码 + 链接）。itemServerId 为 null → 分享整个小屏（载荷仅含 barServerId）；非 null → 分享单个模块（附带 itemServerId）。
    /// 供设置页「小屏窗口」行分享、窗口结构树节点分享、侧边栏模块分享统一复用，避免多处重复实现。
    /// </summary>
    public static void ShowCreateShareDialog(Window? owner, string barServerId, string? itemServerId, string? title = null)
    {
        var resolvedTitle = string.IsNullOrEmpty(title) ? Framework.Lang.T("settings.createShare") : title;
        var dialog = new Window
        {
            Title = resolvedTitle,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)GetResource(owner, "BrushDialogBG"),
            Foreground = (Brush)GetResource(owner, "BrushTextMain")
        };
        dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

        var radioStyle = (Style)GetResource(owner, "DarkRadioButtonStyle");
        var checkStyle = (Style)GetResource(owner, "DarkCheckBoxStyle");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("settings.chooseASharingMode"),
            FontSize = 12,
            Foreground = (Brush)GetResource(owner, "BrushTextSec"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var copyRadio = new RadioButton { Content = Framework.Lang.T("settings.shareModeCopyHint"), Style = radioStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        var refRadio = new RadioButton { Content = Framework.Lang.T("settings.referenceLiveDataHint"), Style = radioStyle, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(copyRadio);
        panel.Children.Add(refRadio);

        var permLabel = new TextBlock
        {
            Text = Framework.Lang.T("settings.importerPermissions"),
            FontSize = 12,
            Foreground = (Brush)GetResource(owner, "BrushTextSec"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(permLabel);
        var cbEdit = new CheckBox { Content = Framework.Lang.T("common.allowEditing"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        var cbDelete = new CheckBox { Content = Framework.Lang.T("common.allowDeletion"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        var cbReShare = new CheckBox { Content = Framework.Lang.T("settings.allowReSharing"), Style = checkStyle, IsChecked = false, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(cbEdit);
        panel.Children.Add(cbDelete);
        panel.Children.Add(cbReShare);

        var confirmBtn = new Button
        {
            Content = Framework.Lang.T("settings.createShareLink"),
            Style = (Style)GetResource(owner, "PrimaryButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand
        };
        panel.Children.Add(confirmBtn);
        dialog.Content = panel;

        // 分享模式与权限联动：复制模式权限固定全允许且隐藏权限区；引用模式显示权限区、逐项可配、默认全不选
        void ApplyShareMode(bool isCopy)
        {
            var vis = isCopy ? Visibility.Collapsed : Visibility.Visible;
            permLabel.Visibility = cbEdit.Visibility = cbDelete.Visibility = cbReShare.Visibility = vis;
            cbEdit.IsChecked = cbDelete.IsChecked = cbReShare.IsChecked = isCopy;
        }
        copyRadio.Checked += (_, _) => ApplyShareMode(true);
        refRadio.Checked += (_, _) => ApplyShareMode(false);
        ApplyShareMode(true);

        confirmBtn.Click += async (_, _) =>
        {
            string mode = copyRadio.IsChecked == true ? "copy" : "reference";
            confirmBtn.IsEnabled = false;
            try
            {
                // 窗口级分享（itemServerId==null）：创建前先做一次全量同步，确保本地结构与行数据都已上传服务端。
                // 否则接收方 copy 到的会是“服务端空壳窗口”——组件仅在本地、从未同步，导入后窗口为空（组件“丢失”）。
                if (itemServerId == null)
                {
                    confirmBtn.Content = Framework.Lang.T("cloudDrive.syncing");
                    if (!await Services.CloudSyncService.Instance.SyncNowAsync(silent: true))
                    {
                        MessageBox.Show(Framework.Lang.T("cloudDrive.syncFailed"), Framework.Lang.T("settings.createShareFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    confirmBtn.Content = Framework.Lang.T("settings.createShareLink");
                }

                var app = (App)Application.Current;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bool allowEdit = cbEdit.IsChecked == true, allowDelete = cbDelete.IsChecked == true, allowReShare = cbReShare.IsChecked == true;
                // itemServerId 为 null → 分享整个小屏（载荷不含 itemServerId 字段，与原窗口级分享一致）
                object payloadObj = itemServerId != null
                    ? new { barServerId, itemServerId, shareMode = mode, allowEdit, allowDelete, allowReShare }
                    : new { barServerId, shareMode = mode, allowEdit, allowDelete, allowReShare };
                var payload = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
                using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/create") { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                using var resp = await Services.HttpLog.SendAsync(http, req);
                string json = await resp.Content.ReadAsStringAsync();
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                if (root["code"]?.ToObject<int>() == 200)
                {
                    string code = root["data"]?["shareCode"]?.ToString() ?? "";
                    dialog.Close();
                    ShowShareResult(owner, code, $"{Constants.URLs.FrontendBaseUrl}/s/{code}");
                }
                else
                {
                    MessageBox.Show(root["msg"]?.ToString() ?? Framework.Lang.T("settings.createShareFailed"), Framework.Lang.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("settings.createShareFailedDetail", ex.Message), Framework.Lang.T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                confirmBtn.IsEnabled = true;
            }
        };

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
