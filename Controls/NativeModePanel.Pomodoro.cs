using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SideDesk.Models;

namespace SideDesk.Controls
{
    /// <summary>
    /// 「番茄闹钟」组件：番茄工作法计时器，支持 番茄钟/短休息/长休息 三阶段循环，
    /// 可自定义各阶段时长、自动切换下一阶段、到时闪烁/弹窗提醒，并统计当日完成番茄数。
    /// </summary>
    public partial class NativeModePanel
    {
        // 番茄闹钟主题色（番茄红）
        private static readonly SolidColorBrush PomodoroRedBrush = new(Color.FromRgb(0xE8, 0x56, 0x4F));

        private UIElement BuildPomodoroWidget(PanelItem item)
        {
            item.Pomodoro ??= new PomodoroSettings();
            var p = item.Pomodoro;
            p.EnsureTodayCount();

            // ===== 红色卡片容器（自包含，不依赖模块背景色）=====
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = PomodoroRedBrush,
                Padding = new Thickness(8, 8, 8, 10),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var root = new StackPanel();
            card.Child = root;

            // ===== 顶部模式选择按钮（点击弹出浮动下拉框）=====
            var modeBtn = new Button
            {
                Content = GetPomodoroModeLabel(p.Mode) + " ▾",
                Height = 26,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(12, 3, 12, 3),
                Template = BuildPomodoroRectButtonTemplate(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            root.Children.Add(modeBtn);

            // ===== 圆形计时器区域（宽度自适应：Viewbox 随容器等比缩放，最大 100px）=====
            var timerContent = new Grid { Width = 100, Height = 100 };

            var ring = new Ellipse
            {
                Stroke = Brushes.White,
                StrokeThickness = 3,
                Fill = Brushes.Transparent
            };
            timerContent.Children.Add(ring);

            var timeDisplay = new TextBlock
            {
                Text = FormatPomodoroTime(p.GetRemaining()),
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            timerContent.Children.Add(timeDisplay);

            var timerBox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 100,
                MaxHeight = 100,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 10),
                Child = timerContent
            };
            root.Children.Add(timerBox);

            // ===== 控制按钮行：开始/暂停 =====
            var controlsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var startBtn = new Button
            {
                Content = p.IsRunning ? "暂停" : "开始",
                Height = 28,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(14, 4, 14, 4),
                Template = BuildPomodoroRectButtonTemplate()
            };

            controlsRow.Children.Add(startBtn);
            root.Children.Add(controlsRow);

            // ===== 状态行：今日完成番茄数 =====
            var statusText = new Emoji.Wpf.TextBlock
            {
                Text = BuildPomodoroStatus(p),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            root.Children.Add(statusText);

            // ===== 模式切换逻辑（下拉菜单项共用）=====
            Action<string> switchMode = (mode) =>
            {
                if (p.Mode == mode) return;

                // 计时中或有进度时需确认
                bool hasProgress = p.IsRunning || p.GetRemaining().TotalSeconds < p.GetModeTotalSeconds();
                if (hasProgress)
                {
                    var result = MessageBox.Show(
                        "切换模式将重置当前计时，是否继续？",
                        "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;
                }

                p.IsRunning = false;
                p.StartTime = null;
                p.Mode = mode;
                p.RemainingSeconds = p.GetModeTotalSeconds();
                _configManager?.Save();

                modeBtn.Content = GetPomodoroModeLabel(mode) + " ▾";
                timeDisplay.Text = FormatPomodoroTime(p.GetRemaining());
                startBtn.Content = "开始";
            };

            // ===== 浮动下拉框：番茄钟 / 短休息 / 长休息 + 重置 =====
            var modeMenu = new ContextMenu();
            foreach (var (mode, label) in new[] { ("Work", "番茄钟"), ("ShortBreak", "短休息"), ("LongBreak", "长休息") })
            {
                var mi = new MenuItem { Header = label };
                var capturedMode = mode;
                mi.Click += (s, e) => switchMode(capturedMode);
                modeMenu.Items.Add(mi);
            }

            // 重置：保持当前模式不变，仅将剩余时间重置为当前模式满时长
            Action resetTimer = () =>
            {
                p.IsRunning = false;
                p.StartTime = null;
                p.RemainingSeconds = p.GetModeTotalSeconds();
                _configManager?.Save();

                startBtn.Content = "开始";
                timeDisplay.Text = FormatPomodoroTime(p.GetRemaining());
            };

            modeMenu.Items.Add(new Separator());
            var resetMi = new MenuItem { Header = "重置" };
            resetMi.Click += (s, e) => resetTimer();
            modeMenu.Items.Add(resetMi);

            // 点击按钮在下方弹出下拉框
            modeBtn.Click += (s, e) =>
            {
                modeMenu.PlacementTarget = modeBtn;
                modeMenu.Placement = PlacementMode.Bottom;
                modeMenu.IsOpen = true;
            };

            // ===== 开始/暂停 =====
            startBtn.Click += (s, e) =>
            {
                if (!p.IsRunning)
                {
                    // 启动 / 继续
                    if (p.GetRemaining().TotalSeconds <= 0)
                        p.RemainingSeconds = p.GetModeTotalSeconds();
                    p.IsRunning = true;
                    p.StartTime = DateTime.Now;
                    _configManager?.Save();

                    StartPomodoroTick(p, timeDisplay, startBtn, statusText);
                    startBtn.Content = "暂停";
                }
                else
                {
                    // 暂停：结算剩余
                    if (p.StartTime.HasValue)
                        p.RemainingSeconds = Math.Max(0, p.RemainingSeconds - (DateTime.Now - p.StartTime.Value).TotalSeconds);
                    p.IsRunning = false;
                    p.StartTime = null;
                    _configManager?.Save();

                    startBtn.Content = "开始";
                    timeDisplay.Text = FormatPomodoroTime(p.GetRemaining());
                }
            };

            // 运行中：恢复计时器刷新
            if (p.IsRunning)
            {
                StartPomodoroTick(p, timeDisplay, startBtn, statusText);
            }

            // ===== 基于 ModuleControl 基础组件包装 =====
            var module = CreateModuleControl("🍅 番茄闹钟", item);
            var settingsMi = new MenuItem { Header = "番茄钟设置" };
            settingsMi.Click += (s, e) => ShowPomodoroSettingsDialog(item);
            module.AddCustomMenuItem(settingsMi);
            module.SetContent(card);
            return module;
        }

        /// <summary>番茄闹钟每秒刷新</summary>
        private void StartPomodoroTick(PomodoroSettings p, TextBlock timeDisplay, Button startBtn,
            Emoji.Wpf.TextBlock statusText)
        {
            var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            dt.Tick += (s, e) =>
            {
                if (!p.IsRunning || p.StartTime == null)
                {
                    dt.Stop();
                    return;
                }

                var remaining = p.GetRemaining();
                timeDisplay.Text = FormatPomodoroTime(remaining);

                if (remaining.TotalSeconds <= 0)
                {
                    // 本阶段结束
                    dt.Stop();
                    OnPomodoroPhaseComplete(p, timeDisplay, startBtn, statusText);
                }
            };
            dt.Start();

            // 控件卸载时停止，避免定时器泄漏
            timeDisplay.Unloaded += (s, e) => dt.Stop();
        }

        /// <summary>阶段结束处理：统计、提醒、切换到下一阶段</summary>
        private void OnPomodoroPhaseComplete(PomodoroSettings p, TextBlock timeDisplay, Button startBtn,
            Emoji.Wpf.TextBlock statusText)
        {
            bool wasWork = p.Mode == "Work";

            if (wasWork)
            {
                p.EnsureTodayCount();
                p.CompletedCount++;
            }

            // 决定下一阶段
            string nextMode;
            if (wasWork)
                nextMode = (p.CompletedCount % Math.Max(1, p.LongBreakInterval) == 0) ? "LongBreak" : "ShortBreak";
            else
                nextMode = "Work";

            p.IsRunning = false;
            p.StartTime = null;
            p.Mode = nextMode;
            p.RemainingSeconds = p.GetModeTotalSeconds();
            _configManager?.Save();

            // 提醒
            if (p.RemindFlash)
                FlashMainWindow();
            if (p.RemindPopup)
                ShowPomodoroPopup(wasWork, p);

            if (p.AutoStartNext)
            {
                // 自动开始下一阶段（重绘时自动恢复计时）
                p.IsRunning = true;
                p.StartTime = DateTime.Now;
            }

            // 刷新页签样式需要重绘整个模块（页签引用不在此作用域）；
            // 若 AutoStartNext，重绘时 BuildPomodoroWidget 会检测 IsRunning 自动恢复计时
            RenderPanels();
        }

        // ==================== 对话框 ====================

        /// <summary>番茄钟设置对话框：各阶段时长 + 行为选项</summary>
        private void ShowPomodoroSettingsDialog(PanelItem panelItem)
        {
            panelItem.Pomodoro ??= new PomodoroSettings();
            var p = panelItem.Pomodoro;

            var dialog = new Window
            {
                Title = "番茄钟设置",
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

            // 各阶段时长
            root.Children.Add(MakeLabel("番茄钟时长（分钟）"));
            var workBox = new TextBox { Text = p.WorkMinutes.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(workBox);

            root.Children.Add(MakeLabel("短休息时长（分钟）"));
            var shortBox = new TextBox { Text = p.ShortBreakMinutes.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(shortBox);

            root.Children.Add(MakeLabel("长休息时长（分钟）"));
            var longBox = new TextBox { Text = p.LongBreakMinutes.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(longBox);

            root.Children.Add(MakeLabel("每完成几个番茄后长休息"));
            var intervalBox = new TextBox { Text = p.LongBreakInterval.ToString(), Margin = new Thickness(0, 0, 0, 12), Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(intervalBox);

            // 行为选项
            root.Children.Add(MakeLabel("行为"));
            var autoChk = new CheckBox { Content = "阶段结束后自动开始下一阶段", IsChecked = p.AutoStartNext, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(autoChk);
            var flashChk = new CheckBox { Content = "结束时闪烁窗口", IsChecked = p.RemindFlash, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(flashChk);
            var popupChk = new CheckBox { Content = "结束时弹出提示窗口", IsChecked = p.RemindPopup, Foreground = (Brush)FindResource("BrushTextMain"), Margin = new Thickness(0, 0, 0, 16) };
            root.Children.Add(popupChk);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                if (!int.TryParse(workBox.Text, out int work) || work < 1) work = 25;
                if (!int.TryParse(shortBox.Text, out int shortBrk) || shortBrk < 1) shortBrk = 5;
                if (!int.TryParse(longBox.Text, out int longBrk) || longBrk < 1) longBrk = 15;
                if (!int.TryParse(intervalBox.Text, out int interval) || interval < 1) interval = 4;

                bool durationChanged = p.WorkMinutes != work || p.ShortBreakMinutes != shortBrk || p.LongBreakMinutes != longBrk;
                p.WorkMinutes = work;
                p.ShortBreakMinutes = shortBrk;
                p.LongBreakMinutes = longBrk;
                p.LongBreakInterval = interval;
                p.AutoStartNext = autoChk.IsChecked ?? false;
                p.RemindFlash = flashChk.IsChecked ?? true;
                p.RemindPopup = popupChk.IsChecked ?? true;

                if (durationChanged && !p.IsRunning)
                {
                    // 时长变更且未在运行：重置当前阶段剩余时间
                    p.RemainingSeconds = p.GetModeTotalSeconds();
                }

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

        /// <summary>阶段结束弹窗提醒</summary>
        private void ShowPomodoroPopup(bool wasWork, PomodoroSettings p)
        {
            var popup = new Window
            {
                Title = wasWork ? "番茄完成" : "休息结束",
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
                Text = wasWork ? "🍅 番茄完成！" : "☕ 休息结束！",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = wasWork
                    ? $"今日已完成 {p.CompletedCount} 个番茄，开始休息吧"
                    : "精力恢复，开始新的番茄钟吧",
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var okBtn = new Button
            {
                Content = "知道了",
                Width = 80,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            okBtn.Click += (s, e) => popup.Close();
            root.Children.Add(okBtn);

            popup.Content = root;
            popup.Show();
            popup.Activate();
        }

        // ==================== 工具方法 ====================

        /// <summary>模式显示名称</summary>
        private static string GetPomodoroModeLabel(string mode) => mode switch
        {
            "ShortBreak" => "短休息",
            "LongBreak" => "长休息",
            _ => "番茄钟"
        };

        /// <summary>矩形按钮模板：白色圆角背景（开始/暂停按钮），宽度随内容自适应（绑定 Padding）</summary>
        private static ControlTemplate BuildPomodoroRectButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            presenter.SetValue(TextBlock.FontSizeProperty, new TemplateBindingExtension(Control.FontSizeProperty));
            presenter.SetValue(TextBlock.FontWeightProperty, new TemplateBindingExtension(Control.FontWeightProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        /// <summary>格式化番茄钟时间：MM:SS（分钟可超过 59，如 60:00）</summary>
        private static string FormatPomodoroTime(TimeSpan ts)
        {
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }

        /// <summary>状态行文字：今日完成数 + 当前阶段提示</summary>
        private static string BuildPomodoroStatus(PomodoroSettings p)
        {
            var modeLabel = p.Mode switch
            {
                "ShortBreak" => "短休息中",
                "LongBreak" => "长休息中",
                _ => "专注中"
            };
            string status = p.IsRunning ? $"🍅 × {p.CompletedCount}  |  {modeLabel}" : $"🍅 × {p.CompletedCount}";
            return status;
        }
    }
}
