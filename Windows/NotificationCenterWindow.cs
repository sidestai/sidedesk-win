using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk.Windows
{
    /// <summary>
    /// 通知中心窗口：展示所有历史通知（含 silent 静默通知），支持已读标记、清空、点击执行动作。
    /// 纯代码构建 UI（无边框、圆角、主题化），与 ThemedMessageBox 风格统一。
    /// </summary>
    public class NotificationCenterWindow : Window
    {
        private readonly StackPanel _listPanel = new();

        /// <summary>展开态的通知 ID 集合</summary>
        private readonly HashSet<string> _expandedIds = new();

        public NotificationCenterWindow()
        {
            Title = Framework.Lang.T("common.notificationCenter");
            Width = 380;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            // 不居顶：普通 Z 序窗口，允许被其他应用窗口自然覆盖

            var root = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ===== 标题栏 =====
            var titleBar = new Grid { Background = Brushes.Transparent };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button) return;
                if (e.LeftButton == MouseButtonState.Pressed) DragMove();
            };

            var titleText = new TextBlock
            {
                Text = Framework.Lang.T("notification.notificationCenter"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 12, 0, 12)
            };

            var btnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            var clearBtn = new Button
            {
                Content = Framework.Lang.T("common.clear"),
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                Style = (Style)FindResource("GhostButtonStyle"),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("notification.clearAllNotifications")
            };
            clearBtn.Click += (s, e) =>
            {
                NotificationService.Instance.ClearHistory();
                // 服务端同清，避免下次同步把已清条目拉回来
                Services.NotificationSyncService.Instance.ClearServer();
                RefreshList();
            };

            var closeBtn = new Button
            {
                Width = 26,
                Height = 26,
                // GhostButtonStyle 默认 Padding=16,10 会把 26px 按钮的内容区压没，必须置 0
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("GhostButtonStyle"),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("common.close"),
                // 用矢量 Path 画 ✕，不依赖字体字形渲染（✕ 字符在部分字体下不可见）
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                    Stroke = (Brush)FindResource("BrushTextMain"),
                    StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = 8,
                    Height = 8
                }
            };
            closeBtn.Click += (s, e) => Close();

            btnStack.Children.Add(clearBtn);
            btnStack.Children.Add(closeBtn);

            titleBar.Children.Add(titleText);
            titleBar.Children.Add(btnStack);
            Grid.SetRow(titleBar, 0);

            // ===== 列表区 =====
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(8, 0, 8, 8)
            };
            scroll.Content = _listPanel;
            Grid.SetRow(scroll, 1);

            grid.Children.Add(titleBar);
            grid.Children.Add(scroll);
            root.Child = grid;
            Content = root;

            // 打开时不批量标已读：未读状态以服务端逐条记录为准，列表行显示红点，
            // 点击某行才标已读（行内红点消失；全部已读后外层菜单红点随之消除）
            Loaded += (s, e) => RefreshList();

            // 订阅历史变更（新通知进入时实时刷新）
            NotificationService.Instance.HistoryChanged += OnHistoryChanged;
            Closed += (s, e) => NotificationService.Instance.HistoryChanged -= OnHistoryChanged;
        }

        private void OnHistoryChanged()
        {
            Dispatcher.Invoke(RefreshList);
        }

        /// <summary>刷新通知列表</summary>
        private void RefreshList()
        {
            _listPanel.Children.Clear();

            var history = NotificationService.Instance.History;
            if (history.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.T("notification.noNotifications"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
                return;
            }

            // 复制列表避免迭代中修改
            var items = history.ToArray();
            foreach (var n in items)
            {
                _listPanel.Children.Add(BuildHistoryItem(n));
            }
        }

        /// <summary>构建单条历史通知 UI</summary>
        private UIElement BuildHistoryItem(NotificationPayload n)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            bool isExpanded = _expandedIds.Contains(n.Id);

            // 标题行：未读圆点 + 标题 + 展开箭头 + 时间（超一个月的陈旧未读不显示红点）。
            // 用 Grid 列约束（* 列）让标题拿到有限宽：收起时 TextTrimming 省略号才生效，
            // 且能以「单行期望宽 vs 列实际宽」判定标题是否溢出可展开；
            // 横向 StackPanel 给子元素无限宽，长标题只会被硬切且无法测量溢出
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (n.ShowUnread)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = (Brush)FindResource("BrushAccent"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(dot, 0);
                titleRow.Children.Add(dot);
            }

            // 标题：展开时换行显示全文（点击看全内容的主体），收起时单行省略号
            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = n.Title,
                FontSize = 11.5,
                FontWeight = n.ShowUnread ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = isExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = isExpanded ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 1);
            titleRow.Children.Add(titleText);

            // 展开/收起指示箭头：有正文/按钮时初始即显示；标题溢出由 RefreshExpandable 布局后复核显隐
            var hasBodyOrButtons = !string.IsNullOrEmpty(n.Body) || (n.Buttons != null && n.Buttons.Count > 0);
            var arrow = new TextBlock
            {
                Text = isExpanded ? "\u25BC" : "\u25B6", // ▼ / ▶
                FontSize = 8,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = hasBodyOrButtons ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetColumn(arrow, 2);
            titleRow.Children.Add(arrow);

            var timeText = new TextBlock
            {
                Text = n.Timestamp.ToString("MM-dd HH:mm"),
                FontSize = 9,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(timeText, 3);
            titleRow.Children.Add(timeText);

            stack.Children.Add(titleRow);

            // 可展开 = 有正文/按钮，或标题单行溢出（信息类通知整段文案在 Title、Body 为空，
            // 不看标题溢出则这类条目永远点不开、看不全）。溢出在布局后按「单行期望宽 vs 标题列实际宽」
            // 比较，SizeChanged 时重算（窗口可拉伸，宽度变化会改变标题是否放得下）；
            // 闭包标志供箭头显隐与点击handler共用，展开态下标题换行但列宽不变，判定与展开状态无关
            bool expandable = hasBodyOrButtons;
            void RefreshExpandable()
            {
                // 尚未布局（ActualWidth=0）或未挂到呈现源：保持初值，避免误判全亮箭头
                if (titleText.ActualWidth <= 0 || PresentationSource.FromVisual(titleText) == null) return;
                var formatted = new FormattedText(
                    n.Title ?? "",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(titleText.FontFamily, titleText.FontStyle, titleText.FontWeight, titleText.FontStretch),
                    titleText.FontSize,
                    titleText.Foreground,
                    VisualTreeHelper.GetDpi(titleText).PixelsPerDip);
                expandable = hasBodyOrButtons || formatted.Width > titleText.ActualWidth + 1;
                arrow.Visibility = expandable ? Visibility.Visible : Visibility.Collapsed;
            }
            titleText.SizeChanged += (s, e) => RefreshExpandable();

            // 正文（展开时显示全文，收起时限制高度）
            if (!string.IsNullOrEmpty(n.Body))
            {
                stack.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = n.Body,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = isExpanded ? double.PositiveInfinity : 32,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(n.ShowUnread ? 12 : 0, 2, 0, 0)
                });
            }

            // 操作按钮行（展开时才显示）
            if (isExpanded && n.Buttons != null && n.Buttons.Count > 0)
            {
                var btnRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(n.ShowUnread ? 12 : 0, 6, 0, 0)
                };

                foreach (var btn in n.Buttons)
                {
                    var actionBtn = new Button
                    {
                        Content = btn.Label ?? btn.Type,
                        FontSize = 10.5,
                        Padding = new Thickness(10, 4, 10, 4),
                        Style = (Style)FindResource("GhostButtonStyle"),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    actionBtn.Click += async (s, e) =>
                    {
                        e.Handled = true;
                        NotificationSyncService.Instance.MarkRead(n);
                        await NotificationActionService.ExecuteAsync(btn, n);
                    };
                    btnRow.Children.Add(actionBtn);
                }

                stack.Children.Add(btnRow);
            }

            var item = new Border
            {
                Child = stack,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            // 悬停高亮
            item.MouseEnter += (s, e) => item.Background = (Brush)FindResource("BrushInputBG");
            item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;

            // 点击主体：切换展开/收起；已读标记 + 执行 ClickMain 动作
            item.MouseLeftButtonUp += async (s, e) =>
            {
                // 按钮点击已通过 e.Handled 阻止冒泡，此处只处理条目空白区域
                if (e.Handled) return;

                if (expandable)
                {
                    // 切换展开状态
                    if (isExpanded)
                        _expandedIds.Remove(n.Id);
                    else
                        _expandedIds.Add(n.Id);
                    RefreshList();
                }

                NotificationSyncService.Instance.MarkRead(n);
                if (n.ClickMain != null)
                    await NotificationActionService.ExecuteAsync(n.ClickMain, n);
            };

            return item;
        }
    }
}
