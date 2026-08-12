using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SideDesk.Models;
using SideDesk.Windows;

namespace SideDesk.Controls
{
    /// <summary>
    /// 「屏幕时长」组件：基于 WindowUsageStats 采集数据，支持 列表 / 饼图 / 分类 / 窗口 四种展示风格，
    /// 风格持久化到 PanelItem.ScreenTimeStyle。
    /// </summary>
    public partial class NativeModePanel
    {
        /// <summary>风格定义：显示名 → 存储键</summary>
        private static readonly (string Label, string Key)[] ScreenTimeStyles =
        {
            (Framework.Lang.T("列表"), "List"),
            (Framework.Lang.T("饼图"), "Pie"),
            (Framework.Lang.T("分类"), "Category"),
            (Framework.Lang.T("窗口"), "Window")
        };

        /// <summary>窄宽紧凑模式：组件宽度低于 100px 时为 true，时长省略秒数</summary>
        private bool _screenTimeCompact;

        private UIElement BuildScreenTimeWidget(PanelItem item)
        {
            string style = string.IsNullOrEmpty(item.ScreenTimeStyle) ? "List" : item.ScreenTimeStyle;

            var container = new StackPanel();

            // ===== 内容区（按风格渲染） =====
            UIElement content;
            switch (style)
            {
                case "Pie": content = BuildScreenTimePie(); break;
                case "Category": content = BuildScreenTimeCategory(); break;
                case "Window": content = BuildScreenTimeWindow(); break;
                default: content = BuildScreenTimeList(); break;
            }
            container.Children.Add(content);

            // ===== 右上角下拉菜单：展示方式切换 =====
            var module = CreateModuleControl(Framework.Lang.T("⏱ 屏幕时长"), item);
            var styleMenu = new MenuItem { Header = Framework.Lang.T("展示方式") };
            foreach (var (label, key) in ScreenTimeStyles)
            {
                string capturedKey = key;
                var mi = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = key == style
                };
                mi.Click += (s, e) =>
                {
                    item.ScreenTimeStyle = capturedKey;
                    _configManager?.Save();
                    RenderPanels();
                };
                styleMenu.Items.Add(mi);
            }
            module.AddCustomMenuItem(styleMenu);

            module.SetContent(container);

            // 窄宽自适应：宽度低于 100px 时切换紧凑模式（隐藏秒数）并重新渲染
            module.SizeChanged += (s, e) =>
            {
                double w = e.NewSize.Width;
                if (w <= 0) return;
                bool compact = w < 100;
                if (compact != _screenTimeCompact)
                {
                    _screenTimeCompact = compact;
                    RenderPanels();
                }
            };

            return module;
        }

        // ===================== 列表：按应用排行 =====================
        private UIElement BuildScreenTimeList()
        {
            var stack = new StackPanel();
            var summary = WindowUsageStats.GetAppUsageSummary();
            if (summary.Count == 0) return ScreenTimeEmpty();

            double max = summary.Max(x => x.TotalDuration.TotalSeconds);
            foreach (var app in summary.Take(15))
            {
                double sec = app.TotalDuration.TotalSeconds;

                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
                var dur = new Emoji.Wpf.TextBlock
                {
                    Text = WindowUsageStats.FormatDuration(app.TotalDuration, !_screenTimeCompact),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dur, Dock.Right);
                row.Children.Add(dur);

                var name = new Emoji.Wpf.TextBlock
                {
                    Text = app.ProcessName,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                row.Children.Add(name);
                stack.Children.Add(row);

                stack.Children.Add(ScreenTimeBar(max > 0 ? sec / max : 0, Color.FromRgb(100, 180, 255)));
            }
            return stack;
        }

        // ===================== 饼图：按分类占比 =====================
        private UIElement BuildScreenTimePie()
        {
            var wrapper = new StackPanel();
            var agg = ScreenTimeAggregateByCategory();
            if (agg.Count == 0) return ScreenTimeEmpty();

            double total = agg.Values.Sum();
            if (total <= 0) return ScreenTimeEmpty();
            var colors = ScreenTimeCategoryColors();
            var ordered = agg.OrderByDescending(x => x.Value).ToList();

            var canvas = new Canvas { Height = 130, HorizontalAlignment = HorizontalAlignment.Stretch };
            canvas.Loaded += (s, e) => DrawScreenTimePie(canvas, ordered, total, colors);
            canvas.SizeChanged += (s, e) => DrawScreenTimePie(canvas, ordered, total, colors);
            wrapper.Children.Add(canvas);

            // 图例
            var legend = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var kv in ordered)
            {
                double ratio = kv.Value / total;
                var color = colors.ContainsKey(kv.Key) ? colors[kv.Key] : Color.FromRgb(130, 130, 130);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"{kv.Key}  {ratio:P0}  {WindowUsageStats.FormatDuration(TimeSpan.FromSeconds(kv.Value), !_screenTimeCompact)}",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                legend.Children.Add(row);
            }
            wrapper.Children.Add(legend);
            return wrapper;
        }

        private void DrawScreenTimePie(Canvas canvas, List<KeyValuePair<string, double>> data, double total, Dictionary<string, Color> colors)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 220;
            double h = canvas.Height;
            double radius = Math.Min(w, h) / 2 - 6;
            if (radius < 20) radius = 20;
            double cx = w / 2;
            double cy = h / 2;

            double angle = 0;
            foreach (var kv in data)
            {
                double ratio = kv.Value / total;
                if (ratio < 0.01) continue;
                double sweep = ratio * 360;
                var color = colors.ContainsKey(kv.Key) ? colors[kv.Key] : Color.FromRgb(130, 130, 130);
                canvas.Children.Add(ScreenTimePieSlice(cx, cy, radius, angle, sweep, color));
                angle += sweep;
            }
        }

        // ===================== 分类：按分类聚合 =====================
        private UIElement BuildScreenTimeCategory()
        {
            var stack = new StackPanel();
            var agg = ScreenTimeAggregateByCategory();
            if (agg.Count == 0) return ScreenTimeEmpty();

            double total = agg.Values.Sum();
            var colors = ScreenTimeCategoryColors();
            foreach (var kv in agg.OrderByDescending(x => x.Value))
            {
                double ratio = total > 0 ? kv.Value / total : 0;
                var color = colors.ContainsKey(kv.Key) ? colors[kv.Key] : Color.FromRgb(130, 130, 130);

                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
                var dur = new Emoji.Wpf.TextBlock
                {
                    Text = WindowUsageStats.FormatDuration(TimeSpan.FromSeconds(kv.Value), !_screenTimeCompact),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dur, Dock.Right);
                row.Children.Add(dur);

                var dot = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);

                var name = new Emoji.Wpf.TextBlock
                {
                    Text = $"{kv.Key}  {ratio:P0}",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(name);
                stack.Children.Add(row);

                stack.Children.Add(ScreenTimeBar(ratio, color));
            }
            return stack;
        }

        // ===================== 窗口：最近窗口记录 =====================
        private UIElement BuildScreenTimeWindow()
        {
            var stack = new StackPanel();
            var recent = WindowUsageStats.GetRecentRecords(20);
            if (recent.Count == 0) return ScreenTimeEmpty();

            foreach (var rec in recent)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

                var time = new Emoji.Wpf.TextBlock
                {
                    Text = rec.Timestamp.ToString("HH:mm"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Width = 38,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(time, Dock.Left);
                row.Children.Add(time);

                var dur = new Emoji.Wpf.TextBlock
                {
                    Text = WindowUsageStats.FormatDuration(rec.Duration, !_screenTimeCompact),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255)),
                    Width = 50,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dur, Dock.Right);
                row.Children.Add(dur);

                string sub = !string.IsNullOrEmpty(rec.Url) ? rec.Url : rec.Title;
                var mid = new StackPanel();
                mid.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = rec.ProcessName,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (!string.IsNullOrEmpty(sub))
                {
                    mid.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = sub,
                        FontSize = 10,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }
                row.Children.Add(mid);
                stack.Children.Add(row);
            }
            return stack;
        }

        // ===================== 辅助方法 =====================

        /// <summary>按分类聚合各应用使用时长（秒）。分类取自 ProgramCategoryManager，排除"忽略"，未设置归入"未分类"。</summary>
        private Dictionary<string, double> ScreenTimeAggregateByCategory()
        {
            var result = new Dictionary<string, double>();
            var summary = WindowUsageStats.GetAppUsageSummary();
            double uncategorized = 0;
            foreach (var app in summary)
            {
                double sec = app.TotalDuration.TotalSeconds;
                string cat = ProgramCategoryManager.GetCategory(app.ProcessName);
                if (cat == "忽略") continue;
                if (string.IsNullOrEmpty(cat)) { uncategorized += sec; continue; }
                if (!result.ContainsKey(cat)) result[cat] = 0;
                result[cat] += sec;
            }
            if (uncategorized > 0) result["未分类"] = uncategorized;
            return result;
        }

        /// <summary>分类配色（与 WindowUsagePanel 保持一致）</summary>
        private static Dictionary<string, Color> ScreenTimeCategoryColors()
        {
            return new Dictionary<string, Color>
            {
                { "教育", Color.FromRgb(100, 180, 255) },
                { "学习", Color.FromRgb(120, 220, 160) },
                { "工作", Color.FromRgb(255, 180, 80) },
                { "娱乐", Color.FromRgb(255, 100, 120) },
                { "生活", Color.FromRgb(180, 140, 255) },
                { "交友", Color.FromRgb(255, 140, 200) },
                { "未分类", Color.FromRgb(130, 130, 130) }
            };
        }

        /// <summary>比例条（star 列宽实现，自适应宽度）</summary>
        private UIElement ScreenTimeBar(double ratio, Color color)
        {
            ratio = Math.Max(0, Math.Min(1, ratio));
            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource("BrushInputBG"),
                ClipToBounds = true
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(ratio, 0.0001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Min(ratio, 0.9999), GridUnitType.Star) });
            var fill = new Border { Background = new SolidColorBrush(color) };
            Grid.SetColumn(fill, 0);
            grid.Children.Add(fill);
            track.Child = grid;
            return track;
        }

        /// <summary>饼图扇形（含整圆退化处理）</summary>
        private UIElement ScreenTimePieSlice(double cx, double cy, double r, double startAngle, double sweepAngle, Color color)
        {
            var brush = new SolidColorBrush(color);
            var stroke = (Brush)FindResource("BrushBorder");

            if (sweepAngle >= 359.9)
            {
                var el = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush, Stroke = stroke, StrokeThickness = 1 };
                Canvas.SetLeft(el, cx - r);
                Canvas.SetTop(el, cy - r);
                return el;
            }

            var path = new System.Windows.Shapes.Path { Fill = brush, Stroke = stroke, StrokeThickness = 1 };
            double startRad = (startAngle - 90) * Math.PI / 180;
            double endRad = (startAngle + sweepAngle - 90) * Math.PI / 180;
            double x1 = cx + r * Math.Cos(startRad);
            double y1 = cy + r * Math.Sin(startRad);
            double x2 = cx + r * Math.Cos(endRad);
            double y2 = cy + r * Math.Sin(endRad);

            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(x1, y1), true));
            fig.Segments.Add(new ArcSegment(new Point(x2, y2), new Size(r, r), 0, sweepAngle > 180, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(cx, cy), true));
            geo.Figures.Add(fig);
            path.Data = geo;
            return path;
        }

        /// <summary>空数据占位</summary>
        private UIElement ScreenTimeEmpty()
        {
            return new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("暂无数据，开启「使用时长统计」后开始记录"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8)
            };
        }
    }
}
