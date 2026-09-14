using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SideDesk.Models;

namespace SideDesk.Controls
{
    /// <summary>
    /// 「倒计时器」卡片：支持添加多个倒计时（时/分输入 + 名称 + 到时提醒方式），
    /// 标题右侧按钮启动/暂停，到时支持闪烁、弹窗、休眠、关机。
    /// </summary>
    public partial class NativeModePanel
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        private UIElement BuildCountdownWidget(PanelItem item)
        {
            item.CountdownItems ??= new List<CountdownItem>();

            var container = new StackPanel();

            foreach (var cd in item.CountdownItems)
            {
                container.Children.Add(CreateCountdownCard(cd, item));
            }

            // 空列表提示：点击直接弹出添加倒计时对话框
            if (item.CountdownItems.Count == 0)
                container.Children.Add(BuildEmptyAddTip(Framework.Lang.T("nativePanel.noCountdownsClickTo"), () => ShowAddCountdownDialog(item)));

            // 基于 ModuleControl 基础组件包装
            var module = CreateModuleControl(Framework.Lang.T("nativePanel.countdown"), item);
            var addMi = new MenuItem { Header = Framework.Lang.T("nativePanel.addCountdown") };
            addMi.Click += (s, e) => ShowAddCountdownDialog(item);
            module.AddCustomMenuItem(addMi);
            module.SetContent(container);
            return module;
        }

        private Border CreateCountdownCard(CountdownItem cd, PanelItem panelItem)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(3),
                Background = (Brush)FindResource("BrushBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };

            // 右键菜单
            var cardMenu = new ContextMenu();
            var editMi = new MenuItem { Header = Framework.Lang.T("nativePanel.editCountdown") };
            editMi.Click += (s, e) => ShowCountdownEditDialog(cd, panelItem);
            cardMenu.Items.Add(editMi);
            var resetMi = new MenuItem { Header = Framework.Lang.T("common.reset") };
            resetMi.Click += (s, e) =>
            {
                cd.IsRunning = false;
                cd.StartTime = null;
                cd.RemainingSeconds = cd.TotalSeconds;
                cd.IsFinished = false;
                _configManager?.Save();
                RenderPanels();
            };
            cardMenu.Items.Add(resetMi);
            var deleteMi = new MenuItem { Header = Framework.Lang.T("nativePanel.deleteCountdown") };
            deleteMi.Click += (s, e) => DeleteCountdown(cd, panelItem);
            cardMenu.Items.Add(deleteMi);
            cardMenu.Items.Add(new Separator());
            cardMenu.Items.Add(BuildSettingsMenuItem(panelItem, Framework.Lang.T("nativePanel.countdown")));
            var cardHideMi = new MenuItem { Header = Framework.Lang.T("common.hideModuleFrame"), IsCheckable = true, IsChecked = panelItem.HideFrame };
            cardHideMi.Click += (s, e) => { panelItem.HideFrame = cardHideMi.IsChecked; _configManager?.Save(); RenderPanels(); };
            cardMenu.Items.Add(cardHideMi);
            cardMenu.Items.Add(BuildPinMenuItem(panelItem));
            var cardDelModuleMi = new MenuItem { Header = Framework.Lang.T("nativePanel.deleteModule") };
            cardDelModuleMi.Click += (s, e) => RemovePanel(panelItem);
            cardMenu.Items.Add(cardDelModuleMi);
            card.ContextMenu = cardMenu;

            var mainStack = new StackPanel();

            // 剩余时间显示
            var timeDisplay = new Emoji.Wpf.TextBlock
            {
                Text = FormatCountdown(cd.GetRemaining()),
                FontSize = FS(24),
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = cd.IsRunning ? (Brush)FindResource("BrushAccent")
                    : cd.IsFinished ? Brushes.OrangeRed
                    : (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };
            var timeBox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = timeDisplay
            };

            // 标题行：名称 + 启动/暂停圆钮 + 编辑按钮
            var headerPanel = new DockPanel();

            var nameBlock = new Emoji.Wpf.TextBlock
            {
                Text = cd.Name,
                FontWeight = FontWeights.Bold,
                FontSize = FS(13),
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            };

            // 启动/暂停圆钮（标题右侧）
            var toggleBtn = new Button
            {
                Content = cd.IsRunning ? BuildPauseIcon() : BuildPlayIcon(),
                Width = 22,
                Height = 22,
                FontSize = FS(10),
                Background = cd.IsRunning ? Brushes.DarkOrange : new SolidColorBrush(Color.FromRgb(46, 160, 67)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Template = BuildCircleButtonTemplate(),
                ToolTip = cd.IsRunning ? Framework.Lang.T("common.pause") : Framework.Lang.T("common.start")
            };
            DockPanel.SetDock(toggleBtn, Dock.Right);

            // 运行中：启动 DispatcherTimer 更新显示
            if (cd.IsRunning && !cd.IsFinished)
            {
                StartCountdownTick(cd, timeDisplay, panelItem, toggleBtn, card);
            }

            toggleBtn.Click += (s, e) =>
            {
                if (cd.IsFinished)
                {
                    // 已到期：点击重新开始（重置后启动）
                    cd.RemainingSeconds = cd.TotalSeconds;
                    cd.IsFinished = false;
                }

                if (!cd.IsRunning)
                {
                    // 启动 / 继续
                    if (cd.RemainingSeconds <= 0) cd.RemainingSeconds = cd.TotalSeconds;
                    cd.IsRunning = true;
                    cd.StartTime = DateTime.Now;
                    _configManager?.Save();
                    StartCountdownTick(cd, timeDisplay, panelItem, toggleBtn, card);
                    toggleBtn.Content = BuildPauseIcon();
                    toggleBtn.Background = Brushes.DarkOrange;
                    toggleBtn.ToolTip = Framework.Lang.T("common.pause");
                    timeDisplay.Foreground = (Brush)FindResource("BrushAccent");
                }
                else
                {
                    // 暂停：结算剩余
                    if (cd.StartTime.HasValue)
                        cd.RemainingSeconds = Math.Max(0, cd.RemainingSeconds - (DateTime.Now - cd.StartTime.Value).TotalSeconds);
                    cd.IsRunning = false;
                    cd.StartTime = null;
                    _configManager?.Save();
                    toggleBtn.Content = BuildPlayIcon();
                    toggleBtn.Background = new SolidColorBrush(Color.FromRgb(46, 160, 67));
                    toggleBtn.ToolTip = Framework.Lang.T("common.start");
                    timeDisplay.Text = FormatCountdown(cd.GetRemaining());
                    timeDisplay.Foreground = (Brush)FindResource("BrushTextMain");
                }
            };

            // 编辑按钮（编辑模式可见）
            var editBtn = new Button
            {
                Content = "✏",
                Width = 24,
                Height = 24,
                FontSize = FS(12),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed
            };
            DockPanel.SetDock(editBtn, Dock.Right);
            editBtn.Click += (s, e) => ShowCountdownEditDialog(cd, panelItem);

            headerPanel.Children.Add(editBtn);
            headerPanel.Children.Add(toggleBtn);
            headerPanel.Children.Add(nameBlock);

            mainStack.Children.Add(headerPanel);
            mainStack.Children.Add(timeBox);

            // 状态行：总时长 + 提醒方式摘要
            var summary = BuildCountdownSummary(cd);
            var infoLine = new Emoji.Wpf.TextBlock
            {
                Text = summary,
                FontSize = FS(11),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            mainStack.Children.Add(infoLine);

            card.Child = mainStack;
            return card;
        }

        /// <summary>倒计时每秒刷新</summary>
        private void StartCountdownTick(CountdownItem cd, TextBlock timeDisplay, PanelItem panelItem, Button toggleBtn, Border card)
        {
            var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            dt.Tick += (s, e) =>
            {
                if (!cd.IsRunning || cd.StartTime == null)
                {
                    dt.Stop();
                    return;
                }

                var remaining = cd.GetRemaining();
                timeDisplay.Text = FormatCountdown(remaining);

                if (remaining.TotalSeconds <= 0)
                {
                    // 到期
                    dt.Stop();
                    cd.IsRunning = false;
                    cd.StartTime = null;
                    cd.RemainingSeconds = 0;
                    cd.IsFinished = true;
                    _configManager?.Save();

                    timeDisplay.Text = "00:00:00";
                    timeDisplay.Foreground = Brushes.OrangeRed;
                    toggleBtn.Content = BuildPlayIcon();
                    toggleBtn.Background = new SolidColorBrush(Color.FromRgb(46, 160, 67));
                    toggleBtn.ToolTip = Framework.Lang.T("nativePanel.restart");

                    ExecuteCountdownReminder(cd);
                }
            };
            dt.Start();

            // 控件卸载时停止，避免定时器泄漏
            card.Unloaded += (s, e) => dt.Stop();
        }

        /// <summary>执行到时提醒：闪烁 / 弹窗 / 休眠 / 关机</summary>
        private void ExecuteCountdownReminder(CountdownItem cd)
        {
            if (cd.RemindFlash)
                FlashMainWindow();

            if (cd.RemindPopup)
                ShowCountdownPopup(cd);

            switch (cd.RemindAction)
            {
                case "Hibernate":
                    SetSuspendState(true, false, false);
                    break;
                case "Shutdown":
                    // 30 秒后关机，给用户取消的机会
                    try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 30") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
                    break;
            }
        }

        /// <summary>闪烁主窗口（透明度交替闪烁 6 次）</summary>
        private void FlashMainWindow()
        {
            var win = _mainWindow ?? Window.GetWindow(this);
            if (win == null) return;

            int count = 0;
            var flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            flashTimer.Tick += (s, e) =>
            {
                count++;
                win.Opacity = count % 2 == 1 ? 0.35 : 1.0;
                if (count >= 12)
                {
                    win.Opacity = 1.0;
                    flashTimer.Stop();
                }
            };
            flashTimer.Start();
        }

        /// <summary>弹出倒计时到期提示窗口</summary>
        private void ShowCountdownPopup(CountdownItem cd)
        {
            var owner = _mainWindow ?? Window.GetWindow(this);
            var popup = new Window
            {
                Title = Framework.Lang.T("nativePanel.countdownEnded"),
                Width = 300,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = true,
                Topmost = true,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            popup.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(popup);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("nativePanel.timeSUp"),
                FontSize = FS(18),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.TF("nativePanel.countdownHasEnded", cd.Name),
                FontSize = FS(13),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 不设固定宽高：PrimaryButtonStyle 自带 16,8 内边距，
            // 强制 Height=28 会把文字裁掉，改为由内容自适应
            var okBtn = new Button
            {
                Content = Framework.Lang.T("common.gotIt"),
                MinWidth = 80,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            okBtn.Click += (s, e) => popup.Close();
            root.Children.Add(okBtn);

            popup.Content = root;
            popup.Show();
            popup.Activate();
        }

        // ==================== 添加 / 编辑对话框 ====================

        /// <summary>添加倒计时对话框：名称 + 时/分 + 提醒方式</summary>
        private void ShowAddCountdownDialog(PanelItem panelItem)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("nativePanel.addCountdown"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = Window.GetWindow(this)
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(16) };
            TextBlock MakeLabel(string t) => new Emoji.Wpf.TextBlock
            {
                Text = t,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 4)
            };

            // 名称
            root.Children.Add(MakeLabel(Framework.Lang.T("common.name")));
            var nameBox = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(nameBox);

            // 时长：小时 + 分钟
            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.countdownDuration")));
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var hourBox = new TextBox { Text = "0", Width = 50, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            timeRow.Children.Add(hourBox);
            timeRow.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("common.hoursUnit"), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("BrushTextMain") });
            var minBox = new TextBox { Text = "5", Width = 50, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            timeRow.Children.Add(minBox);
            timeRow.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("common.minutesUnit"), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("BrushTextMain") });
            root.Children.Add(timeRow);

            // 提醒方式
            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.remindWhenDue")));
            var flashChk = new CheckBox { Content = Framework.Lang.T("common.flashWindow"), IsChecked = true, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(flashChk);
            var popupChk = new CheckBox { Content = Framework.Lang.T("common.popupAlertWindow"), IsChecked = true, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(popupChk);

            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.actionWhenDue")));
            var actionCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            actionCombo.Items.Add(Framework.Lang.T("common.none"));
            actionCombo.Items.Add(Framework.Lang.T("nativePanel.hibernate"));
            actionCombo.Items.Add(Framework.Lang.T("nativePanel.shutDownIn30"));
            actionCombo.SelectedIndex = 0;
            root.Children.Add(actionCombo);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("common.ok"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("common.cancel"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                if (!int.TryParse(hourBox.Text, out int hours) || hours < 0) hours = 0;
                if (!int.TryParse(minBox.Text, out int minutes) || minutes < 0) minutes = 0;
                int totalSeconds = hours * 3600 + minutes * 60;
                if (totalSeconds <= 0)
                {
                    MessageBox.Show(Framework.Lang.T("nativePanel.durationMustBePositive"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var cd = new CountdownItem
                {
                    Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("nativePanel.newCountdown") : nameBox.Text.Trim(),
                    TotalSeconds = totalSeconds,
                    RemainingSeconds = totalSeconds,
                    RemindFlash = flashChk.IsChecked ?? true,
                    RemindPopup = popupChk.IsChecked ?? true,
                    RemindAction = actionCombo.SelectedIndex switch { 1 => "Hibernate", 2 => "Shutdown", _ => "None" }
                };
                panelItem.CountdownItems ??= new List<CountdownItem>();
                panelItem.CountdownItems.Add(cd);
                _configManager?.Save();
                RenderPanels();
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        /// <summary>编辑倒计时对话框</summary>
        private void ShowCountdownEditDialog(CountdownItem cd, PanelItem panelItem)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("nativePanel.editCountdown"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                Owner = Window.GetWindow(this)
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(16) };
            TextBlock MakeLabel(string t) => new Emoji.Wpf.TextBlock
            {
                Text = t,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 4)
            };

            // 名称
            root.Children.Add(MakeLabel(Framework.Lang.T("common.name")));
            var nameBox = new TextBox { Text = cd.Name, Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(nameBox);

            // 时长
            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.countdownDuration")));
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var hourBox = new TextBox { Text = (cd.TotalSeconds / 3600).ToString(), Width = 50, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            timeRow.Children.Add(hourBox);
            timeRow.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("common.hoursUnit"), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("BrushTextMain") });
            var minBox = new TextBox { Text = ((cd.TotalSeconds % 3600) / 60).ToString(), Width = 50, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            timeRow.Children.Add(minBox);
            timeRow.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("common.minutesUnit"), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("BrushTextMain") });
            root.Children.Add(timeRow);

            // 提醒方式
            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.remindWhenDue")));
            var flashChk = new CheckBox { Content = Framework.Lang.T("common.flashWindow"), IsChecked = cd.RemindFlash, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(flashChk);
            var popupChk = new CheckBox { Content = Framework.Lang.T("common.popupAlertWindow"), IsChecked = cd.RemindPopup, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(popupChk);

            root.Children.Add(MakeLabel(Framework.Lang.T("nativePanel.actionWhenDue")));
            var actionCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            actionCombo.Items.Add(Framework.Lang.T("common.none"));
            actionCombo.Items.Add(Framework.Lang.T("nativePanel.hibernate"));
            actionCombo.Items.Add(Framework.Lang.T("nativePanel.shutDownIn30"));
            actionCombo.SelectedIndex = cd.RemindAction switch { "Hibernate" => 1, "Shutdown" => 2, _ => 0 };
            root.Children.Add(actionCombo);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("common.ok"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("common.cancel"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                if (!int.TryParse(hourBox.Text, out int hours) || hours < 0) hours = 0;
                if (!int.TryParse(minBox.Text, out int minutes) || minutes < 0) minutes = 0;
                int totalSeconds = hours * 3600 + minutes * 60;
                if (totalSeconds <= 0)
                {
                    MessageBox.Show(Framework.Lang.T("nativePanel.durationMustBePositive"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool durationChanged = totalSeconds != cd.TotalSeconds;
                cd.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("nativePanel.newCountdown") : nameBox.Text.Trim();
                cd.TotalSeconds = totalSeconds;
                if (durationChanged)
                {
                    // 时长变更：重置进度
                    cd.RemainingSeconds = totalSeconds;
                    cd.IsRunning = false;
                    cd.StartTime = null;
                    cd.IsFinished = false;
                }
                cd.RemindFlash = flashChk.IsChecked ?? true;
                cd.RemindPopup = popupChk.IsChecked ?? true;
                cd.RemindAction = actionCombo.SelectedIndex switch { 1 => "Hibernate", 2 => "Shutdown", _ => "None" };
                _configManager?.Save();
                RenderPanels();
                dialog.DialogResult = true;
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        // ==================== 工具方法 ====================

        /// <summary>删除倒计时项；卡片为空时询问是否删除整个卡片</summary>
        private void DeleteCountdown(CountdownItem cd, PanelItem panelItem)
        {
            if (panelItem.CountdownItems == null) return;
            if (!panelItem.CountdownItems.Remove(cd)) return;
            _configManager?.Save();

            if (panelItem.CountdownItems.Count == 0)
            {
                var result = MessageBox.Show(
                    Framework.Lang.T("nativePanel.deleteEmptyCountdownModuleConfirm"),
                    Framework.Lang.T("common.confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes && _currentWindowConfig != null)
                {
                    _currentWindowConfig.Panels.Remove(panelItem);
                    _configManager?.Save();
                }
            }
            RenderPanels();
        }

        /// <summary>格式化倒计时显示：始终 HH:MM:SS</summary>
        private static string FormatCountdown(TimeSpan ts)
        {
            int totalHours = (int)ts.TotalHours;
            return $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        /// <summary>摘要行：总时长 + 提醒方式</summary>
        private static string BuildCountdownSummary(CountdownItem cd)
        {
            var parts = new List<string>();
            var total = TimeSpan.FromSeconds(cd.TotalSeconds);
            parts.Add(Framework.Lang.TF("nativePanel.hMinInTotal", (int)total.TotalHours, total.Minutes));

            var reminds = new List<string>();
            if (cd.RemindFlash) reminds.Add(Framework.Lang.T("common.flash"));
            if (cd.RemindPopup) reminds.Add(Framework.Lang.T("common.popup"));
            if (cd.RemindAction == "Hibernate") reminds.Add(Framework.Lang.T("nativePanel.hibernate"));
            else if (cd.RemindAction == "Shutdown") reminds.Add(Framework.Lang.T("nativePanel.shutDown"));
            if (reminds.Count > 0)
                parts.Add(Framework.Lang.T("nativePanel.at") + string.Join("/", reminds));

            if (cd.IsFinished) parts.Add(Framework.Lang.T("nativePanel.overdue"));
            else if (!cd.IsRunning && cd.GetRemaining().TotalSeconds < cd.TotalSeconds) parts.Add(Framework.Lang.T("nativePanel.paused"));

            return string.Join("  |  ", parts);
        }

        /// <summary>暂停图标：白色双竖线</summary>
        private static UIElement BuildPauseIcon()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(new System.Windows.Shapes.Rectangle { Width = 3, Height = 9, Fill = Brushes.White });
            panel.Children.Add(new System.Windows.Shapes.Rectangle { Width = 3, Height = 9, Fill = Brushes.White, Margin = new Thickness(2, 0, 0, 0) });
            return panel;
        }
    }
}
