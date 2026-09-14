using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;

namespace SideDesk.Controls
{
    /// <summary>
    /// 「打卡」组件：多个打卡按钮，每点一次当日计数 +1（按钮右上角徽标显示数字），
    /// 并以打卡内容文字为类型名镜像写入「每日活跃」当日数值（作用域跟随当前窗口）。
    /// 弹出窗提供常用打卡预设一键添加 + 自定义（文字 / 图标 / 颜色）。
    /// 数据结构与云同步遵循行级存储（row_type = check_in），与信号灯等组件一致。
    /// </summary>
    public partial class NativeModePanel
    {
        /// <summary>常用打卡预设：(emoji 图标, 文字 i18n 键)。弹出窗一键添加。</summary>
        private static readonly (string Icon, string TextKey)[] CheckInPresets =
        {
            ("💧", "nativePanel.presetDrinkWater"),
            ("🧍", "nativePanel.presetStandUp"),
            ("👀", "nativePanel.presetEyeRest"),
            ("💊", "nativePanel.presetMedicine"),
            ("🌬", "nativePanel.presetBreath"),
            ("🚶", "nativePanel.presetWalk"),
        };

        /// <summary>自定义打卡时的备选配色（按已有数量循环取用）。</summary>
        private static readonly string[] CheckInPalette =
        {
            "#3498DB", "#2ECC71", "#E67E22", "#9B59B6", "#1ABC9C", "#E74C3C", "#F39C12", "#16A085"
        };

        /// <summary>编辑弹窗里提供的快捷 emoji（免去手动输入图标的麻烦）。</summary>
        private static readonly string[] CheckInEmojiChoices =
        {
            "💧", "☕", "🍎", "💊", "🏃", "🚶", "🧘", "👀",
            "🌬", "💪", "✍", "📖", "🎯", "⏰", "🍵", "🥗"
        };

        // ==================== 组件渲染 ====================

        private UIElement BuildCheckInWidget(PanelItem item)
        {
            item.CheckInItems ??= new List<CheckInItem>();

            var container = new StackPanel();

            if (item.CheckInItems.Count == 0)
            {
                // 空态：点击提示直接打开添加弹窗
                container.Children.Add(BuildEmptyAddTip(
                    Framework.Lang.T("nativePanel.addCheckIn"),
                    () => ShowCheckInPickerDialog(item)));
            }
            else
            {
                int cols = Math.Clamp(item.CheckInColumns, 1, 4);
                var grid = new UniformGrid { Columns = cols };
                foreach (var ci in item.CheckInItems)
                    grid.Children.Add(BuildCheckInButton(item, ci));
                container.Children.Add(grid);
            }

            var module = WrapWidget(Framework.Lang.T("common.checkIn"), container, item,
                () => ShowCheckInPickerDialog(item), null, Framework.Lang.T("nativePanel.addCheckIn"));

            // 右键「列数」子菜单：1~4 列可选，勾选当前值（与文件区列数菜单一致）
            if (module is ModuleControl mc)
            {
                var columnsMenu = new MenuItem { Header = Framework.Lang.T("common.columnCount") };
                for (int col = 1; col <= 4; col++)
                {
                    int colCount = col;
                    var colItem = new MenuItem
                    {
                        Header = Framework.Lang.TF("nativePanel.columnCountValue", colCount),
                        IsCheckable = true,
                        IsChecked = item.CheckInColumns == colCount
                    };
                    colItem.Click += (s, e) => { item.CheckInColumns = colCount; _configManager?.Save(); RenderPanels(); };
                    columnsMenu.Items.Add(colItem);
                }
                mc.AddCustomMenuItem(columnsMenu);
            }

            return module;
        }

        /// <summary>构建单个打卡按钮卡片：图标 + 文字 + 右上角计数徽标；左键打卡、右键编辑/删除。</summary>
        private UIElement BuildCheckInButton(PanelItem panel, CheckInItem ci)
        {
            var color = TryParseCheckInColor(ci.Color);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = WithAlpha(color, 0.14),
                BorderBrush = WithAlpha(color, 0.55),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3),
                Padding = new Thickness(6, 8, 6, 8),
                Cursor = Cursors.Hand,
                MinHeight = 56
            };

            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!string.IsNullOrEmpty(ci.Icon))
            {
                content.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = ci.Icon,
                    FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(ci.Text) ? Framework.Lang.T("common.checkIn") : ci.Text,
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30
            });
            card.Child = content;

            // 计数徽标（右上角叠加，始终显示数字，初始 0）
            var badgeText = new TextBlock
            {
                Text = ci.TodayCount().ToString(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(9),
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Child = badgeText
            };

            var overlay = new Grid { Margin = new Thickness(0) };
            overlay.Children.Add(card);
            overlay.Children.Add(badge);

            // 左键：打卡 +1（原地刷新徽标，避免整面板重绘造成闪烁）
            overlay.MouseLeftButtonUp += (s, e) =>
            {
                DoCheckIn(panel, ci);
                badgeText.Text = ci.TodayCount().ToString();
                overlay.ToolTip = Framework.Lang.TF("nativePanel.checkInTodayCount", ci.TodayCount());
                e.Handled = true;
            };

            // 右键：编辑 / 删除
            var menu = new ContextMenu();
            var editMi = new MenuItem { Header = Framework.Lang.T("common.edit") };
            editMi.Click += (s, e) => ShowCheckInEditor(panel, ci);
            menu.Items.Add(editMi);
            var delMi = new MenuItem { Header = Framework.Lang.T("common.delete") };
            delMi.Click += (s, e) =>
            {
                panel.CheckInItems?.Remove(ci);
                ReindexCheckIn(panel);
                _configManager?.Save();
                RenderPanels();
            };
            menu.Items.Add(delMi);
            overlay.ContextMenu = menu;

            overlay.ToolTip = Framework.Lang.TF("nativePanel.checkInTodayCount", ci.TodayCount());
            return overlay;
        }

        /// <summary>执行一次打卡：当日计数 +1，镜像写入「每日活跃」同名类型，持久化。</summary>
        private void DoCheckIn(PanelItem panel, CheckInItem ci)
        {
            ci.EnsureTodayCount();
            ci.Count++;

            // 每日活跃：以打卡内容文字为类型名，当日数值 +1（作用域跟随当前窗口，后端不存在时自动建行）
            string text = (ci.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(text))
            {
                string? corpId = _currentWindowConfig?.OwnerType == "team" ? _currentWindowConfig?.CorpId : null;
                _ = Services.DailyActivityService.Instance.AddAsync(corpId, text, DateTime.Now, 1);
            }

            _configManager?.Save();
        }

        // ==================== 添加 / 管理弹窗 ====================

        /// <summary>打卡管理弹窗：常用预设一键添加 + 自定义 + 现有项编辑/删除。所有改动实时落库。</summary>
        private void ShowCheckInPickerDialog(PanelItem item)
        {
            item.CheckInItems ??= new List<CheckInItem>();

            var dialog = new Window
            {
                Title = Framework.Lang.T("nativePanel.addCheckIn"),
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            // 列表容器需被上方预设/自定义按钮的回调引用，故提前声明（RenderList 局部函数在其后定义）
            var listStack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            // ---- 常用打卡预设 ----
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("nativePanel.checkInPresets"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var presetPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (icon, textKey) in CheckInPresets)
            {
                string label = Framework.Lang.T(textKey);
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand
                };
                var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
                chipContent.Children.Add(new Emoji.Wpf.TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                chipContent.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("BrushTextMain") });
                chip.Child = chipContent;
                string capturedIcon = icon;
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    AddCheckInItem(item, label, capturedIcon);
                    RenderList();
                };
                presetPanel.Children.Add(chip);
            }
            root.Children.Add(presetPanel);

            // ---- 自定义添加按钮 ----
            var customBtn = new Button
            {
                Content = Framework.Lang.T("nativePanel.customCheckIn"),
                Style = (Style)FindResource("StandardButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 0, 12)
            };
            customBtn.Click += (s, e) => { ShowCheckInEditor(item, null, dialog); RenderList(); };
            root.Children.Add(customBtn);

            // ---- 现有打卡项列表 ----
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("common.checkIn"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            void RenderList()
            {
                listStack.Children.Clear();
                if (item.CheckInItems.Count == 0)
                {
                    listStack.Children.Add(new TextBlock
                    {
                        Text = Framework.Lang.T("nativePanel.checkInPresets"),
                        FontSize = 11,
                        Opacity = 0.5,
                        Foreground = (Brush)FindResource("BrushTextMuted")
                    });
                    return;
                }
                for (int i = 0; i < item.CheckInItems.Count; i++)
                {
                    int idx = i;
                    var ci = item.CheckInItems[i];
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

                    var idxLabel = new TextBlock { Text = $"{i + 1}.", Width = 22, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(idxLabel, Dock.Left);
                    row.Children.Add(idxLabel);

                    var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var editBtn = new Button
                    {
                        Content = "✎", Width = 22, Height = 22, FontSize = 10,
                        Background = Brushes.Transparent, Foreground = (Brush)FindResource("BrushTextSec"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0),
                        Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0),
                        ToolTip = Framework.Lang.T("common.edit")
                    };
                    editBtn.Click += (s, e) => { ShowCheckInEditor(item, ci, dialog); RenderList(); };
                    rightBtns.Children.Add(editBtn);
                    var delBtn = new Button
                    {
                        Content = "✕", Width = 22, Height = 22, FontSize = 10,
                        Background = Brushes.Transparent, Foreground = (Brush)FindResource("BrushTextMuted"),
                        BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = Cursors.Hand,
                        ToolTip = Framework.Lang.T("common.delete")
                    };
                    delBtn.Click += (s, e) =>
                    {
                        item.CheckInItems.RemoveAt(idx);
                        ReindexCheckIn(item);
                        _configManager?.Save();
                        RenderList();
                    };
                    rightBtns.Children.Add(delBtn);
                    DockPanel.SetDock(rightBtns, Dock.Right);
                    row.Children.Add(rightBtns);

                    var swatch = new System.Windows.Shapes.Ellipse
                    {
                        Width = 14, Height = 14, Fill = new SolidColorBrush(TryParseCheckInColor(ci.Color)),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                    };
                    row.Children.Add(swatch);
                    if (!string.IsNullOrEmpty(ci.Icon))
                    {
                        row.Children.Add(new Emoji.Wpf.TextBlock
                        {
                            Text = ci.Icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                        });
                    }
                    row.Children.Add(new TextBlock
                    {
                        Text = ci.Text, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("BrushTextMain")
                    });

                    listStack.Children.Add(row);
                }
            }
            RenderList();
            root.Children.Add(listStack);

            // ---- 完成 ----
            var doneBtn = new Button
            {
                Content = Framework.Lang.T("common.ok"), Width = 70,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)FindResource("PrimaryButtonStyle")
            };
            doneBtn.Click += (s, e) => dialog.Close();
            root.Children.Add(doneBtn);

            dialog.Content = root;
            // 关闭时统一提交并刷新组件（预设/自定义/删除均已实时落库，这里保证组件重绘反映最终状态）
            dialog.Closed += (s, e) => { _configManager?.Save(); RenderPanels(); };
            dialog.ShowDialog();
        }

        /// <summary>新增一个打卡项（预设/快捷添加用），配色按已有数量循环取用。</summary>
        private void AddCheckInItem(PanelItem panel, string text, string icon)
        {
            panel.CheckInItems ??= new List<CheckInItem>();
            var ci = new CheckInItem
            {
                Text = text,
                Icon = icon,
                Color = CheckInPalette[panel.CheckInItems.Count % CheckInPalette.Length],
                SortIndex = panel.CheckInItems.Count
            };
            panel.CheckInItems.Add(ci);
            ReindexCheckIn(panel);
            _configManager?.Save();
        }

        /// <summary>
        /// 单个打卡项编辑弹窗：文字 / 图标（含快捷 emoji）/ 颜色（预设色板 + hex）。
        /// existing 为 null 时新建并追加；否则原地修改。点确定才写回（Save + RenderPanels）。
        /// </summary>
        private void ShowCheckInEditor(PanelItem panel, CheckInItem? existing, Window? owner = null)
        {
            panel.CheckInItems ??= new List<CheckInItem>();

            var dialog = new Window
            {
                Title = existing == null ? Framework.Lang.T("nativePanel.customCheckIn") : Framework.Lang.T("common.edit"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner ?? Window.GetWindow(this),
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");

            string curColor = existing?.Color ?? CheckInPalette[panel.CheckInItems.Count % CheckInPalette.Length];

            // 文字
            var textRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var textLabel = new TextBlock { Text = Framework.Lang.T("nativePanel.checkInContent"), Width = 72, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(textLabel, Dock.Left);
            textRow.Children.Add(textLabel);
            var textBox = new TextBox { Text = existing?.Text ?? "", Style = textBoxStyle };
            textRow.Children.Add(textBox);
            root.Children.Add(textRow);

            // 图标（输入框 + 实时预览）
            var iconRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var iconLabel = new TextBlock { Text = Framework.Lang.T("common.iconLabel"), Width = 72, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(iconLabel, Dock.Left);
            iconRow.Children.Add(iconLabel);
            var iconPreview = new Emoji.Wpf.TextBlock { Text = existing?.Icon ?? "", FontSize = 18, Width = 30, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(iconPreview, Dock.Right);
            iconRow.Children.Add(iconPreview);
            var iconBox = new TextBox { Text = existing?.Icon ?? "", Style = textBoxStyle };
            iconBox.TextChanged += (s, e) => iconPreview.Text = iconBox.Text;
            iconRow.Children.Add(iconBox);
            root.Children.Add(iconRow);

            // 快捷 emoji
            var emojiPanel = new WrapPanel { Margin = new Thickness(72, 0, 0, 10) };
            foreach (var em in CheckInEmojiChoices)
            {
                var eb = new Button
                {
                    Content = new Emoji.Wpf.TextBlock { Text = em, FontSize = 15 },
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 4, 4),
                    Background = Brushes.Transparent,
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand, Padding = new Thickness(0)
                };
                string captured = em;
                eb.Click += (s, e) => iconBox.Text = captured;
                emojiPanel.Children.Add(eb);
            }
            root.Children.Add(emojiPanel);

            // 颜色（色块按钮 + 预设色板弹出 + hex 输入）
            var colorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var colorLabel = new TextBlock { Text = Framework.Lang.T("common.color"), Width = 72, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(colorLabel, Dock.Left);
            colorRow.Children.Add(colorLabel);

            var hexBox = new TextBox { Text = curColor, Style = textBoxStyle, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var swatchWrapper = new Border { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("BrushBorder"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(TryParseCheckInColor(curColor)), Cursor = Cursors.Hand
            };
            swatchWrapper.Child = swatch;

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = swatchWrapper,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = false
            };
            var pickerPanel = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8)
            };
            var colorGrid = new WrapPanel { Width = 180 };
            foreach (var c in CheckInPalette.Concat(new[] { "#FFFFFF", "#CCCCCC", "#666666", "#333333", "#34495E", "#1A1A2E" }))
            {
                var cell = new Border
                {
                    Width = 24, Height = 24, Margin = new Thickness(2), CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(TryParseCheckInColor(c)), Cursor = Cursors.Hand
                };
                string capturedColor = c;
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    curColor = capturedColor;
                    hexBox.Text = capturedColor;
                    swatch.Background = new SolidColorBrush(TryParseCheckInColor(capturedColor));
                    popup.IsOpen = false;
                };
                colorGrid.Children.Add(cell);
            }
            pickerPanel.Child = colorGrid;
            popup.Child = pickerPanel;
            swatch.MouseLeftButtonUp += (s, e) => popup.IsOpen = !popup.IsOpen;
            hexBox.TextChanged += (s, e) =>
            {
                var t = hexBox.Text.Trim();
                if (!string.IsNullOrEmpty(t)) { curColor = t; swatch.Background = new SolidColorBrush(TryParseCheckInColor(t)); }
            };

            DockPanel.SetDock(swatchWrapper, Dock.Left);
            colorRow.Children.Add(swatchWrapper);
            colorRow.Children.Add(hexBox);
            root.Children.Add(colorRow);

            // 确定 / 取消
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("common.ok"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("common.cancel"), Width = 70, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                string text = textBox.Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    MessageBox.Show(Framework.Lang.T("nativePanel.pleaseEnterCheckInContent"));
                    return;
                }
                string icon = iconBox.Text.Trim();
                if (existing == null)
                {
                    panel.CheckInItems.Add(new CheckInItem
                    {
                        Text = text,
                        Icon = icon,
                        Color = curColor,
                        SortIndex = panel.CheckInItems.Count
                    });
                }
                else
                {
                    existing.Text = text;
                    existing.Icon = icon;
                    existing.Color = curColor;
                }
                ReindexCheckIn(panel);
                _configManager?.Save();
                RenderPanels();
                dialog.Close();
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        // ==================== 辅助 ====================

        /// <summary>按当前列表顺序重排 SortIndex（0..n-1），随行 content 持久化与云同步。</summary>
        private static void ReindexCheckIn(PanelItem panel)
        {
            if (panel.CheckInItems == null) return;
            for (int i = 0; i < panel.CheckInItems.Count; i++)
                panel.CheckInItems[i].SortIndex = i;
        }

        /// <summary>解析 hex 颜色为 Color，失败回退默认蓝。</summary>
        private static Color TryParseCheckInColor(string? hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex ?? ""); }
            catch { return Color.FromRgb(0x4A, 0x90, 0xD9); }
        }

        /// <summary>在给定颜色上叠加透明度（用于卡片淡色填充与描边）。</summary>
        private static SolidColorBrush WithAlpha(Color c, double alpha)
            => new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)(alpha * 255), 0, 255), c.R, c.G, c.B));
    }
}
