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

        public NotificationCenterWindow()
        {
            Title = Framework.Lang.T("通知中心");
            Width = 380;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            Topmost = true;

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
                Text = Framework.Lang.T("🔔 通知中心"),
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
                Content = Framework.Lang.T("清空"),
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                Style = (Style)FindResource("GhostButtonStyle"),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("清空全部通知")
            };
            clearBtn.Click += (s, e) =>
            {
                NotificationService.Instance.ClearHistory();
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
                ToolTip = Framework.Lang.T("关闭"),
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

            // 打开时标记全部已读
            Loaded += (s, e) =>
            {
                NotificationService.Instance.MarkAllRead();
                RefreshList();
            };

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
                    Text = Framework.Lang.T("暂无通知"),
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

            // 标题行：未读圆点 + 标题 + 时间
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };

            if (!n.IsRead)
            {
                titleRow.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = (Brush)FindResource("BrushAccent"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }

            titleRow.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = n.Title,
                FontSize = 11.5,
                FontWeight = n.IsRead ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            titleRow.Children.Add(new TextBlock
            {
                Text = n.Timestamp.ToString("MM-dd HH:mm"),
                FontSize = 9,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });

            stack.Children.Add(titleRow);

            // 正文
            if (!string.IsNullOrEmpty(n.Body))
            {
                stack.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = n.Body,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 32,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(n.IsRead ? 0 : 12, 2, 0, 0)
                });
            }

            var item = new Border
            {
                Child = stack,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = n.ClickMain != null ? Cursors.Hand : Cursors.Arrow
            };

            // 悬停高亮
            item.MouseEnter += (s, e) => item.Background = (Brush)FindResource("BrushInputBG");
            item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;

            // 点击执行 clickMain
            item.MouseLeftButtonUp += async (s, e) =>
            {
                if (n.ClickMain != null)
                    await NotificationActionService.ExecuteAsync(n.ClickMain, n);
            };

            return item;
        }
    }
}
