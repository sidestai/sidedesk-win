using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SideDesk.Windows;

namespace SideDesk.Controls
{
    /// <summary>
    /// 窗口使用时长统计面板 - 右侧浮出式窗口
    /// </summary>
    public class WindowUsagePanel
    {
        private Window _dialog = null;
        private Canvas _pieCanvas = null;
        private StackPanel _summaryStack = null;
        private StackPanel _recentStack = null;
        private TextBlock _totalTimeText = null;
        private Border _enableBanner = null;
        private Window _owner = null;
        private HashSet<string> _expandedItems = new HashSet<string>(); // 记录展开的进程名

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        public bool IsOpen => _dialog != null && _dialog.IsLoaded;

        public void Show(Window owner, double left, double top, double width, double height)
        {
            if (IsOpen)
            {
                UpdateEnableBanner();
                _dialog.Activate();
                return;
            }

            _owner = owner;

            _dialog = new Window
            {
                Title = Framework.Lang.T("common.usageStatistics"),
                Width = 420,
                Height = height,
                Left = left,  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                Top = top,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.CanResize,
                Owner = owner
            };
            _dialog.Loaded += (s, e) => HideFromTaskbar(_dialog);
            _dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(_dialog);
            // 背景走主题资源绑定，切换主题时自动刷新
            _dialog.SetResourceReference(Window.BackgroundProperty, "BrushDialogBG");

            BuildUI();
            UpdateEnableBanner();
            LoadData();

            _dialog.Show();
        }

        private void BuildUI()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 未开启提示条
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: 饼状图
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: Content

            // 顶部提示条：设置中未开启使用统计时显眼提示，支持一键开启
            _enableBanner = new Border
            {
                Margin = new Thickness(12, 10, 12, 0),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Visibility = Visibility.Collapsed
            };
            _enableBanner.SetResourceReference(Border.BackgroundProperty, "BrushInputBG");
            _enableBanner.SetResourceReference(Border.BorderBrushProperty, "BrushAccent");
            var bannerPanel = new StackPanel();
            var bannerText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("window.usageStatisticsDisabledWarning"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            bannerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            bannerPanel.Children.Add(bannerText);
            var enableBtn = new Button
            {
                Content = Framework.Lang.T("window.clickToEnable"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            enableBtn.Click += (s, e) => EnableUsageStats();
            bannerPanel.Children.Add(enableBtn);
            _enableBanner.Child = bannerPanel;
            Grid.SetRow(_enableBanner, 0);
            grid.Children.Add(_enableBanner);

            // 饼状图区域（最上方）
            var pieBorder = new Border
            {
                Height = 180,
                Margin = new Thickness(12, 8, 12, 8),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            pieBorder.SetResourceReference(Border.BackgroundProperty, "BrushInputBG");
            pieBorder.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");
            _pieCanvas = new Canvas { Margin = new Thickness(8) };
            pieBorder.Child = _pieCanvas;
            Grid.SetRow(pieBorder, 1);
            grid.Children.Add(pieBorder);

            // Header: 标题 + 刷新按钮
            var headerPanel = new DockPanel { Margin = new Thickness(12, 6, 12, 6) };

            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("window.todayUsage"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            DockPanel.SetDock(titleText, Dock.Left);
            headerPanel.Children.Add(titleText);

            var refreshBtn = new Button
            {
                Content = Framework.Lang.T("common.refresh"),
                Width = 50,
                Height = 26,
                Margin = new Thickness(0)
            };
            refreshBtn.Click += (s, e) => LoadData();
            DockPanel.SetDock(refreshBtn, Dock.Right);
            headerPanel.Children.Add(refreshBtn);

            _totalTimeText = new Emoji.Wpf.TextBlock
            {
                Text = "",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            _totalTimeText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextSec");
            headerPanel.Children.Add(_totalTimeText);

            Grid.SetRow(headerPanel, 2);
            grid.Children.Add(headerPanel);

            // Content: 上下两个区域 (使用时长排行 + 最近记录)
            var contentScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12, 0, 12, 12)
            };

            var contentPanel = new StackPanel();

            // 使用时长排行卡片
            var summaryCard = BuildSectionCard(Framework.Lang.T("screenTime.appUsageTime"));
            _summaryStack = new StackPanel();
            ((StackPanel)summaryCard.Child).Children.Add(_summaryStack);
            contentPanel.Children.Add(summaryCard);

            // 最近窗口记录卡片
            var recentCard = BuildSectionCard(Framework.Lang.T("screenTime.recentWindowRecords"));
            _recentStack = new StackPanel();
            ((StackPanel)recentCard.Child).Children.Add(_recentStack);
            contentPanel.Children.Add(recentCard);

            contentScroll.Content = contentPanel;
            Grid.SetRow(contentScroll, 3);
            grid.Children.Add(contentScroll);

            _dialog.Content = grid;
        }

        /// <summary>区块卡片：主题绑定的圆角卡片 + 左侧 accent 竖条标题，返回卡片的 Child 为 StackPanel，调用方往里追加内容</summary>
        private static Border BuildSectionCard(string headerText)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            card.SetResourceReference(Border.BackgroundProperty, "BrushInputBG");
            card.SetResourceReference(Border.BorderBrushProperty, "BrushBorder");

            var inner = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var bar = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            bar.SetResourceReference(Border.BackgroundProperty, "BrushAccent");
            header.Children.Add(bar);
            var tb = new Emoji.Wpf.TextBlock
            {
                Text = headerText,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            header.Children.Add(tb);
            inner.Children.Add(header);

            card.Child = inner;
            return card;
        }

        /// <summary>根据设置开关状态切换顶部提示条显示</summary>
        private void UpdateEnableBanner()
        {
            if (_enableBanner == null) return;
            _enableBanner.Visibility = Framework.AppSettings.Instance.ShowUsageStats
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>一键开启使用统计：写设置、启动后台采集服务，然后隐藏提示条并刷新数据</summary>
        private void EnableUsageStats()
        {
            Framework.AppSettings.Instance.ShowUsageStats = true;
            Framework.AppSettings.Instance.Save();
            try
            {
                Services.WindowUsageService.Instance.ApplyEnabled(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UsagePanel] window usage collection toggle error: {ex.Message}");
            }
            UpdateEnableBanner();
            LoadData();
        }

        private void LoadData(bool keepExpanded = false)
        {
            if (_summaryStack == null || _recentStack == null) return;

            // 如果需要保持展开状态，先记录当前展开的进程名
            if (keepExpanded)
            {
                _expandedItems.Clear();
                foreach (UIElement child in _summaryStack.Children)
                {
                    if (child is DockPanel dockPanel)
                    {
                        // 找 Tag 里存储的进程名（展开项有 Tag）
                        if (dockPanel.Tag is string processName && !string.IsNullOrEmpty(processName))
                        {
                            // 找展开箭头按钮，检查是否展开
                            foreach (UIElement elem in GetVisualChildren(dockPanel))
                            {
                                if (elem is Button btn && btn.Content.ToString() == "▼")
                                {
                                    _expandedItems.Add(processName);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 总活跃时长（排除"忽略"类别）
            var summaryForTotal = WindowUsageStats.GetAppUsageSummary();
            var filteredTotal = summaryForTotal
                .Where(x => x.Category != "忽略")
                .Sum(x => x.TotalDuration.TotalSeconds);
            _totalTimeText.Text = Framework.Lang.TF("window.totalTime", WindowUsageStats.FormatDuration(TimeSpan.FromSeconds(filteredTotal)));

            // 饼状图
            DrawPieChart();

            // 使用时长排行
            _summaryStack.Children.Clear();
            var summary = WindowUsageStats.GetAppUsageSummary();
            foreach (var item in summary)
            {
                var row = CreateSummaryRow(item, _expandedItems.Contains(item.ProcessName));
                _summaryStack.Children.Add(row);
            }

            if (summary.Count == 0)
            {
                var emptySummary = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("common.noData"),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                emptySummary.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMuted");
                _summaryStack.Children.Add(emptySummary);
            }

            // 最近记录
            _recentStack.Children.Clear();
            var recent = WindowUsageStats.GetRecentRecords(30);
            foreach (var record in recent)
            {
                var row = CreateRecentRow(record);
                _recentStack.Children.Add(row);
            }

            if (recent.Count == 0)
            {
                var emptyRecent = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("window.noRecordsYet"),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                emptyRecent.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMuted");
                _recentStack.Children.Add(emptyRecent);
            }
        }

        /// <summary>递归获取可视化子元素</summary>
        private IEnumerable<UIElement> GetVisualChildren(DependencyObject parent)
        {
            var stack = new System.Collections.Generic.Stack<DependencyObject>();
            stack.Push(parent);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is UIElement ui) yield return ui;

                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }

        /// <summary>绘制分类占比饼状图</summary>
        private void DrawPieChart()
        {
            if (_pieCanvas == null) return;
            _pieCanvas.Children.Clear();

            var summary = WindowUsageStats.GetAppUsageSummary();
            if (summary.Count == 0) return;

            // 按分类汇总（排除"忽略"类别）
            var categoryTotals = new Dictionary<string, double>();
            double uncategorizedTotal = 0;
            double grandTotal = 0;

            foreach (var item in summary)
            {
                double seconds = item.TotalDuration.TotalSeconds;

                // 有子项的进程：主行不参与分类统计，只用子项的分类
                bool hasSub = item.SubItems != null && item.SubItems.Count > 0;
                if (hasSub)
                {
                    // 主行时长分配到子项分类中
                    foreach (var sub in item.SubItems)
                    {
                        double subSeconds = sub.TotalDuration.TotalSeconds;
                        if (string.IsNullOrEmpty(sub.Category))
                            uncategorizedTotal += subSeconds;
                        else if (sub.Category == "忽略")
                            continue;
                        else
                        {
                            if (!categoryTotals.ContainsKey(sub.Category))
                                categoryTotals[sub.Category] = 0;
                            categoryTotals[sub.Category] += subSeconds;
                            grandTotal += subSeconds;
                        }
                    }
                    continue;
                }

                // 无子项的进程：按主行分类统计
                if (string.IsNullOrEmpty(item.Category))
                {
                    uncategorizedTotal += seconds;
                    grandTotal += seconds;
                }
                else if (item.Category == "忽略")
                {
                    continue; // 忽略类别不计入统计（从分母中排除）
                }
                else
                {
                    if (!categoryTotals.ContainsKey(item.Category))
                        categoryTotals[item.Category] = 0;
                    categoryTotals[item.Category] += seconds;
                    grandTotal += seconds;
                }
            }

            if (grandTotal <= 0) return;

            if (uncategorizedTotal > 0)
                categoryTotals["未分类"] = uncategorizedTotal;

            // 分类颜色映射
            var categoryColors = new Dictionary<string, Color>
            {
                { "教育", Color.FromRgb(100, 180, 255) },
                { "学习", Color.FromRgb(120, 220, 160) },
                { "工作", Color.FromRgb(255, 180, 80) },
                { "娱乐", Color.FromRgb(255, 100, 120) },
                { "生活", Color.FromRgb(180, 140, 255) },
                { "交友", Color.FromRgb(255, 140, 200) },
                { "未分类", Color.FromRgb(130, 130, 130) }
            };

            double canvasWidth = _pieCanvas.ActualWidth > 0 ? _pieCanvas.ActualWidth : 380;
            double canvasHeight = _pieCanvas.ActualHeight > 0 ? _pieCanvas.ActualHeight : 160;
            double pieRadius = Math.Min(canvasWidth, canvasHeight) / 2 - 40;
            double centerX = pieRadius + 10;
            double centerY = canvasHeight / 2;

            // 绘制饼图扇形
            double currentAngle = 0;
            foreach (var kv in categoryTotals)
            {
                double ratio = kv.Value / grandTotal;
                if (ratio < 0.01) continue; // 太小不绘制

                double startAngle = currentAngle;
                double sweepAngle = ratio * 360;
                currentAngle += sweepAngle;

                var color = categoryColors.ContainsKey(kv.Key) ? categoryColors[kv.Key] : Color.FromRgb(130, 130, 130);
                var slice = CreatePieSlice(centerX, centerY, pieRadius, startAngle, sweepAngle, color);
                _pieCanvas.Children.Add(slice);
            }

            // 绘制图例（右侧）
            double legendX = centerX + pieRadius + 15;
            double legendY = 10;
            double lineHeight = 20;

            foreach (var kv in categoryTotals.OrderByDescending(x => x.Value))
            {
                if (legendY + lineHeight > canvasHeight) break;

                double ratio = kv.Value / grandTotal;
                string durationStr = WindowUsageStats.FormatDuration(TimeSpan.FromSeconds(kv.Value));
                string label = $"{kv.Key} {ratio:P1} ({durationStr})";
                var color = categoryColors.ContainsKey(kv.Key) ? categoryColors[kv.Key] : Color.FromRgb(130, 130, 130);

                // 颜色方块
                var rect = new Rectangle
                {
                    Width = 14,
                    Height = 14,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY + 3);
                _pieCanvas.Children.Add(rect);

                // 文字
                var text = new Emoji.Wpf.TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
                Canvas.SetLeft(text, legendX + 20);
                Canvas.SetTop(text, legendY);
                _pieCanvas.Children.Add(text);

                legendY += lineHeight;
            }
        }

        /// <summary>创建饼图扇形 Path</summary>
        private System.Windows.Shapes.Path CreatePieSlice(double cx, double cy, double radius, double startAngle, double sweepAngle, Color color)
        {
            var path = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(color),
                StrokeThickness = 2
            };
            path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "BrushBorder");

            double startRad = (startAngle - 90) * Math.PI / 180;
            double endRad = (startAngle + sweepAngle - 90) * Math.PI / 180;

            double x1 = cx + radius * Math.Cos(startRad);
            double y1 = cy + radius * Math.Sin(startRad);
            double x2 = cx + radius * Math.Cos(endRad);
            double y2 = cy + radius * Math.Sin(endRad);

            var geometry = new PathGeometry();
            var figure = new PathFigure
            {
                StartPoint = new Point(cx, cy),
                IsClosed = true
            };
            figure.Segments.Add(new LineSegment(new Point(x1, y1), true));
            figure.Segments.Add(new ArcSegment(
                new Point(x2, y2),
                new Size(radius, radius),
                0,
                sweepAngle > 180,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(new Point(cx, cy), true));
            geometry.Figures.Add(figure);
            path.Data = geometry;

            return path;
        }

        private UIElement CreateSummaryRow(AppUsageItem item, bool initiallyExpanded = false)
        {
            var outer = new StackPanel();

            // 主行
            var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };

            bool hasSub = item.SubItems != null && item.SubItems.Count > 0;
            bool isBrowser = IsBrowserProcess(item.ProcessName);
            bool isWechat = IsWechatProcess(item.ProcessName);
            // 有子项时：主行不显示时长+分类按钮（只展示进程名和展开箭头）
            bool showMainActions = !hasSub;

            // 右侧：时长（始终显示，方便对比汇总）
            var durationText = new Emoji.Wpf.TextBlock
            {
                Text = WindowUsageStats.FormatDuration(item.TotalDuration),
                FontSize = 12,
                Width = 70,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            durationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushAccent");
            DockPanel.SetDock(durationText, Dock.Right);
            panel.Children.Add(durationText);

            // 中间：分类标签（有子项时不显示主分类，由子项各自标记）
            if (showMainActions)
            {
                var catBtn = new Button
                {
                    Width = 48,
                    Height = 22,
                    FontSize = 11,
                    Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                UpdateCategoryButton(catBtn, item.Category);
                catBtn.Click += (s, e) => ShowCategoryPicker(item.ProcessName, catBtn);
                DockPanel.SetDock(catBtn, Dock.Right);
                panel.Children.Add(catBtn);
            }

            // 展开箭头 + 进程名（仅对有子项的显示）
            if (hasSub)
            {
                var expandBtn = new Button
                {
                    Content = initiallyExpanded ? "▼" : "▶",
                    Width = 22,
                    Height = 22,
                    FontSize = 10,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                DockPanel.SetDock(expandBtn, Dock.Left);
                panel.Children.Add(expandBtn);

                // 进程名（有子项时加粗显示）
                var nameTextSub = new Emoji.Wpf.TextBlock
                {
                    Text = item.ProcessName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                nameTextSub.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
                panel.Children.Add(nameTextSub);

                // 展开区域
                var subPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0), Visibility = initiallyExpanded ? Visibility.Visible : Visibility.Collapsed };

                expandBtn.Click += (s, e) =>
                {
                    bool isExpanded = subPanel.Visibility == Visibility.Visible;
                    subPanel.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
                    expandBtn.Content = isExpanded ? "▶" : "▼";
                    // 更新展开状态记录
                    if (isExpanded)
                        _expandedItems.Remove(item.ProcessName);
                    else
                        _expandedItems.Add(item.ProcessName);
                };

                // 子项
                foreach (var sub in item.SubItems)
                {
                    var subRow = CreateSubRow(item.ProcessName, sub, isBrowser, isWechat);
                    subPanel.Children.Add(subRow);
                }

                outer.Children.Add(panel);
                outer.Children.Add(subPanel);
                return outer;
            }

            // 左侧：进程名
            var nameText = new Emoji.Wpf.TextBlock
            {
                Text = item.ProcessName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            panel.Children.Add(nameText);

            outer.Children.Add(panel);
            return outer;
        }

        /// <summary>创建子项行（域名/标题）</summary>
        private UIElement CreateSubRow(string parentProcess, AppUsageSubItem sub, bool isBrowser, bool isWechat)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            // 右侧：时长
            var durText = new Emoji.Wpf.TextBlock
            {
                Text = WindowUsageStats.FormatDuration(sub.TotalDuration),
                FontSize = 11,
                Width = 70,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            durText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushAccent");
            DockPanel.SetDock(durText, Dock.Right);
            panel.Children.Add(durText);

            // 分类按钮
            var catBtn = new Button
            {
                Width = 42,
                Height = 20,
                FontSize = 10,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            UpdateCategoryButton(catBtn, sub.Category);
            // 浏览器子项用域名作 key，微信子项用 "weixin::标题" 作 key
            string subKey = isBrowser ? sub.Name : (parentProcess + "::" + sub.Name);
            catBtn.Click += (s, e) => ShowCategoryPicker(subKey, catBtn);
            DockPanel.SetDock(catBtn, Dock.Right);
            panel.Children.Add(catBtn);

            // 子项名称（域名/标题）
            var nameText = new Emoji.Wpf.TextBlock
            {
                Text = sub.Name,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            panel.Children.Add(nameText);

            return panel;
        }

        private static bool IsBrowserProcess(string processName)
        {
            string lower = processName.ToLowerInvariant();
            return lower == "msedge" || lower == "chrome" || lower == "firefox" || lower == "iexplore" || lower == "msedge.exe" || lower == "chrome.exe";
        }

        private static bool IsWechatProcess(string processName)
        {
            string lower = processName.ToLowerInvariant();
            return lower == "weixin" || lower == "wechat";
        }

        /// <summary>更新分类按钮显示：有分类则显示分类文字+浅色背景，无分类则显示+号</summary>
        private void UpdateCategoryButton(Button btn, string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                btn.Content = "+";
                btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BrushSecondaryBtnBG");
                btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "BrushTextMuted");
            }
            else
            {
                btn.Content = category;
                btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BrushAccent");
                btn.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        /// <summary>弹出分类选择器（Popup），用户点击分类后保存并重载数据</summary>
        private void ShowCategoryPicker(string processName, Button targetBtn)
        {
            var popup = new Popup
            {
                PlacementTarget = targetBtn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false
            };

            var pickerStack = new StackPanel();
            pickerStack.SetResourceReference(StackPanel.BackgroundProperty, "BrushDialogBG");
            foreach (var cat in ProgramCategoryManager.AvailableCategories)
            {
                var catBtn = new Button
                {
                    Content = cat,
                    Width = 100,
                    Height = 28,
                    Margin = new Thickness(4, 2, 4, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                catBtn.Click += (s, e) =>
                {
                    ProgramCategoryManager.SetCategory(processName, cat);
                    popup.IsOpen = false;
                    // 刷新数据（保持展开状态）
                    LoadData(keepExpanded: true);
                };
                pickerStack.Children.Add(catBtn);
            }

            // 添加"清除分类"选项
            var clearBtn = new Button
            {
                Content = Framework.Lang.T("window.clearCategory"),
                Width = 100,
                Height = 28,
                Margin = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            clearBtn.Click += (s, e) =>
            {
                ProgramCategoryManager.SetCategory(processName, null);
                popup.IsOpen = false;
                LoadData(keepExpanded: true);
            };
            pickerStack.Children.Add(clearBtn);

            popup.Child = pickerStack;
            popup.IsOpen = true;
        }

        private UIElement CreateRecentRow(WindowRecord record)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };

            // 第一行：时间 + 进程名 + 时长
            var topRow = new DockPanel();

            var timeText = new Emoji.Wpf.TextBlock
            {
                Text = record.Timestamp.ToString("HH:mm:ss"),
                FontSize = 11,
                Width = 60,
                VerticalAlignment = VerticalAlignment.Center
            };
            timeText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMuted");
            DockPanel.SetDock(timeText, Dock.Left);
            topRow.Children.Add(timeText);

            var durText = new Emoji.Wpf.TextBlock
            {
                Text = WindowUsageStats.FormatDuration(record.Duration),
                FontSize = 11,
                Width = 60,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            durText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushAccent");
            DockPanel.SetDock(durText, Dock.Right);
            topRow.Children.Add(durText);

            var procText = new Emoji.Wpf.TextBlock
            {
                Text = record.ProcessName,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            procText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushTextMain");
            topRow.Children.Add(procText);

            panel.Children.Add(topRow);

            // 第二行：如果有URL显示URL，否则显示标题
            string secondLine = !string.IsNullOrEmpty(record.Url) ? record.Url : record.Title;
            if (!string.IsNullOrEmpty(secondLine))
            {
                var titleText = new Emoji.Wpf.TextBlock
                {
                    Text = secondLine,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(60, 0, 60, 0)
                };
                titleText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                    !string.IsNullOrEmpty(record.Url) ? "BrushTextSec" : "BrushTextMuted");
                panel.Children.Add(titleText);
            }

            return panel;
        }

        public void SyncPosition(double left, double top, double width, double height)
        {
            if (IsOpen)
            {
                _dialog.Left = left;  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                _dialog.Top = top;
                _dialog.Height = height;
            }
        }

        public void Activate()
        {
            if (IsOpen)
                _dialog.Activate();
        }

        public void Close()
        {
            if (IsOpen)
            {
                try { _dialog.Close(); } catch { }
            }
            _dialog = null;
        }

        private void HideFromTaskbar(Window window)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var taskbarList = (ITaskbarList)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090")));
                taskbarList.HrInit();
                taskbarList.DeleteTab(hwnd);
                Marshal.ReleaseComObject(taskbarList);
            }
            catch { }
        }
    }
}
