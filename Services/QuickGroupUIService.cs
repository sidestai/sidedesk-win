using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SideDesk.Models;
using SideDesk.Windows;
using SideDesk.Controls;

namespace SideDesk.Services
{
    /// <summary>
    /// 快捷项/分组 UI 管理服务
    /// 负责构建分组UI、项按钮、拖拽排序、打开/关闭窗口等
    /// </summary>
    public class QuickGroupUIService
    {
        private readonly StackPanel _container;
        private readonly Dispatcher _dispatcher;
        private Border? _dragIndicator;
        private readonly HashSet<IntPtr> _quickItemHandles = new();
        private readonly List<ItemButtonRef> _itemButtonRefs = new();
        private DispatcherTimer? _foregroundWatcher;
        private DispatcherTimer? _longTermWatcher;

        /// <summary>
        /// 本服务实例绑定的分组 Id（1:1 面板绑定模式）。
        /// 为 null 表示未绑定特定分组（显示全部分组，拖入落到默认分组）。
        /// 用于让拖入的文件落到当前窗口/面板对应的分组，而不是永远落到默认分组。
        /// </summary>
        private string? _boundGroupId;

        private static readonly Brush ActiveHighlightBorder =
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
        private static Brush InactiveForeground =>
            (Brush)Application.Current.FindResource("BrushTextSec");

        /// <summary>
        /// 为模块容器应用主题背景色（显式获取资源，确保每次创建时使用当前主题）
        /// </summary>
        private static void ApplyModuleBackground(Border border)
        {
            border.Background = (Brush)Application.Current.FindResource("BrushInputBG");
        }

        /// <summary>
        /// 为模块容器应用主题背景色（StackPanel 等无 Background 属性的容器，用 Border 包裹）
        /// </summary>
        private static Border WrapWithThemeBackground(FrameworkElement content)
        {
            var wrapper = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(2),
                Child = content
            };
            wrapper.Background = (Brush)Application.Current.FindResource("BrushInputBG");
            return wrapper;
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>请求主窗口作为对话框的 Owner</summary>
        public Window? OwnerWindow { get; set; }

        /// <summary>本服务所属的窗口配置（新建快捷面板时的落位窗口）</summary>
        public WindowConfig? OwnerWindowConfig { get; set; }

        /// <summary>分组被删除时触发，参数为 groupId；用于 1:1 面板绑定模式下通知上层移除 PanelItem</summary>
        public Action<string>? OnGroupDeleted { get; set; }

        public QuickGroupUIService(StackPanel container)
        {
            _container = container;
            _dispatcher = container.Dispatcher;
        }

        #region Public API

        /// <summary>加载所有分组到 UI</summary>
        public void LoadGroups()
        {
            // 分组已改为面板自有数据：绑定模式只渲染本面板的分组，
            // 避免把其他窗口/面板的快捷面板误渲染进当前容器
            if (!string.IsNullOrEmpty(_boundGroupId))
            {
                LoadSingleGroup(_boundGroupId);
                return;
            }
            _boundGroupId = null;
            _container.Children.Clear();
            _itemButtonRefs.Clear();
            var groups = QuickGroupManager.GetAll();
            foreach (var group in groups)
            {
                _container.Children.Add(BuildGroupPanel(group));
            }

            // 配置容器接受拖放
            _container.AllowDrop = true;
            _dragIndicator = new Border
            {
                Height = 3,
                Background = Brushes.DodgerBlue,
                Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed,
                Tag = "__indicator__"
            };
            _container.DragOver += GroupContainer_DragOver;
            _container.DragLeave += (s, ev) => HideDragIndicator();
            _container.Drop += GroupContainer_Drop;

            EnsureForegroundWatcher();
            EnsureLongTermWatcher();
        }

        /// <summary>仅加载指定分组到 UI（1:1 面板绑定模式）</summary>
        public void LoadSingleGroup(string groupId)
        {
            _boundGroupId = groupId;
            _container.Children.Clear();
            _itemButtonRefs.Clear();
            var group = QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == groupId);
            if (group != null)
            {
                _container.Children.Add(BuildGroupPanel(group));
            }

            // 配置容器接受拖放
            _container.AllowDrop = true;
            _dragIndicator = new Border
            {
                Height = 3,
                Background = Brushes.DodgerBlue,
                Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed,
                Tag = "__indicator__"
            };
            _container.DragOver += GroupContainer_DragOver;
            _container.DragLeave += (s, ev) => HideDragIndicator();
            _container.Drop += GroupContainer_Drop;

            EnsureForegroundWatcher();
            EnsureLongTermWatcher();
        }

        /// <summary>显示添加分组对话框</summary>
        public void ShowAddGroupDialog()
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("添加分组"),
                Width = 350, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Owner = OwnerWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 15, 10, 5) };
            namePanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("名称:"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 50 });
            var nameBox = new TextBox { Width = 220, Text = Framework.Lang.T("新分组") };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var colPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 10, 5) };
            colPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("列数:"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 50 });
            var colCombo = new ComboBox { Width = 100 };
            colCombo.Items.Add(Framework.Lang.T("1 列")); colCombo.Items.Add(Framework.Lang.T("2 列"));
            colCombo.Items.Add(Framework.Lang.T("3 列")); colCombo.Items.Add(Framework.Lang.T("4 列"));
            colCombo.SelectedIndex = 0;
            colPanel.Children.Add(colCombo);
            Grid.SetRow(colPanel, 1);
            grid.Children.Add(colPanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show(Framework.Lang.T("请输入名称")); return; }
                var group = new QuickGroup { Name = name, Columns = colCombo.SelectedIndex + 1 };
                QuickGroupManager.AddGroup(group, OwnerWindowConfig);
                QuickGroupManager.Save();
                _container.Children.Add(BuildGroupPanel(group));
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
        }

        /// <summary>编辑快捷组对话框：分组名 / 列数 / 显示模式 / 所属团队（右键标题栏与 ▼ 菜单的“编辑快捷组”入口）</summary>
        private void ShowEditGroupDialog(QuickGroup group)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("编辑快捷组"),
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Owner = OwnerWindow
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var radioStyle = (Style)Application.Current.FindResource("DarkRadioButtonStyle");
            var root = new StackPanel { Margin = new Thickness(20) };

            // 分组名
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("分组名"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new TextBox { Text = group.Name, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            root.Children.Add(nameBox);

            // 列数
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("列数"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 14, 0, 6)
            });
            int selectedCol = Math.Clamp(group.Columns, 1, 4);
            var colPanel = new StackPanel { Orientation = Orientation.Horizontal };
            for (int c = 1; c <= 4; c++)
            {
                int col = c;
                var r = new RadioButton
                {
                    Content = Framework.Lang.TF("{0} 列", col),
                    Style = radioStyle,
                    IsChecked = col == selectedCol,
                    Margin = new Thickness(0, 0, 16, 0)
                };
                r.Checked += (s, ev) => selectedCol = col;
                colPanel.Children.Add(r);
            }
            root.Children.Add(colPanel);

            // 显示模式
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("显示模式"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 14, 0, 6)
            });
            string selectedMode = group.DisplayMode ?? "grid";
            var modePanel = new StackPanel { Orientation = Orientation.Horizontal };
            var modes = new[] { ("grid", "大图标"), ("list", "列表"), ("popup", "弹出菜单") };
            foreach (var (modeKey, modeLabel) in modes)
            {
                var r = new RadioButton
                {
                    Content = modeLabel,
                    Style = radioStyle,
                    IsChecked = modeKey == selectedMode,
                    Margin = new Thickness(0, 0, 16, 0)
                };
                r.Checked += (s, ev) => selectedMode = modeKey;
                modePanel.Children.Add(r);
            }
            root.Children.Add(modePanel);

            // 所属团队（个人 + 团队列表，异步加载）
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("所属团队"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 14, 0, 6)
            });
            var teamCombo = new ComboBox
            {
                Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle"),
                IsEditable = false
            };
            teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = (string?)null });
            teamCombo.SelectedIndex = 0;
            root.Children.Add(teamCombo);

            // 异步拉取团队列表，加载完成后恢复当前所属团队的选中项
            string? pendingTeamId = group.TeamId;
            _ = Task.Run(async () =>
            {
                try
                {
                    var teams = await TeamService.Instance.ListTeamsAsync();
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (teamCombo.Items.Count > 1) return; // 已加载过，避免重复
                        if (teams != null)
                        {
                            foreach (var t in teams)
                                teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
                        }
                        if (!string.IsNullOrEmpty(pendingTeamId))
                        {
                            for (int i = 1; i < teamCombo.Items.Count; i++)
                            {
                                if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == pendingTeamId)
                                { teamCombo.SelectedIndex = i; break; }
                            }
                        }
                    });
                }
                catch { }
            });

            // 按钮
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"),
                Style = (Style)Application.Current.FindResource("StandardButtonStyle"),
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button
            {
                Content = Framework.Lang.T("保存"),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show(Framework.Lang.T("请输入分组名")); return; }
                group.Name = name;
                group.Columns = selectedCol;
                group.DisplayMode = selectedMode;
                // 所属团队：选中“个人”（Tag=null）则清空团队标识
                var selTeam = teamCombo.SelectedItem as ComboBoxItem;
                group.TeamId = selTeam?.Tag as string;
                group.TeamName = string.IsNullOrEmpty(group.TeamId) ? null : selTeam?.Content?.ToString();
                group.OwnerType = string.IsNullOrEmpty(group.TeamId) ? "personal" : "team";
                QuickGroupManager.UpdateGroup(group);
                QuickGroupManager.Save();
                dialog.Close();
                RefreshGroupView();
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        /// <summary>弹出「+」菜单（添加分组/程序/网址/窗口）</summary>
        public void ShowAddMenu(Button target)
        {
            var menu = new ContextMenu();

            var addGroupItem = new MenuItem { Header = Framework.Lang.T("添加分组") };
            addGroupItem.Click += (s, ev) => ShowAddGroupDialog();
            menu.Items.Add(addGroupItem);
            menu.Items.Add(new Separator());

            var addProgItem = new MenuItem { Header = Framework.Lang.T("添加程序") };
            addProgItem.Click += (s, ev) => AddItemToDefaultGroup("program");
            menu.Items.Add(addProgItem);

            var addUrlItem = new MenuItem { Header = Framework.Lang.T("添加网址") };
            addUrlItem.Click += (s, ev) => AddItemToDefaultGroup("url");
            menu.Items.Add(addUrlItem);

            var addWinItem = new MenuItem { Header = Framework.Lang.T("添加窗口") };
            addWinItem.Click += (s, ev) => ShowPickWindowDialog(null);
            menu.Items.Add(addWinItem);

            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>弹出快捷添加菜单（网页/程序/窗口），用于右键菜单直接添加</summary>
        public void ShowAddShortcutMenu(UIElement target)
        {
            var menu = new ContextMenu();

            var addUrlItem = new MenuItem { Header = Framework.Lang.T("网页") };
            addUrlItem.Click += (s, ev) => AddItemToDefaultGroup("url");
            menu.Items.Add(addUrlItem);

            var addProgItem = new MenuItem { Header = Framework.Lang.T("程序") };
            addProgItem.Click += (s, ev) => AddItemToDefaultGroup("program");
            menu.Items.Add(addProgItem);

            var addWinItem = new MenuItem { Header = Framework.Lang.T("窗口") };
            addWinItem.Click += (s, ev) => ShowPickWindowDialog(null);
            menu.Items.Add(addWinItem);

            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>弹出快捷添加菜单（网页/程序/窗口），添加到指定分组</summary>
        public void ShowAddShortcutMenuForGroup(UIElement target, string groupId)
        {
            var menu = new ContextMenu();

            var addUrlItem = new MenuItem { Header = Framework.Lang.T("网页") };
            addUrlItem.Click += (s, ev) => ShowAddItemDialog(groupId, null, "url");
            menu.Items.Add(addUrlItem);

            var addProgItem = new MenuItem { Header = Framework.Lang.T("程序") };
            addProgItem.Click += (s, ev) => ShowAddItemDialog(groupId, null, "program");
            menu.Items.Add(addProgItem);

            var addWinItem = new MenuItem { Header = Framework.Lang.T("窗口") };
            addWinItem.Click += (s, ev) => ShowPickWindowDialog(groupId);
            menu.Items.Add(addWinItem);

            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>弹出分组名称输入对话框，创建分组并返回 groupId；取消返回 null</summary>
        public string ShowCreateGroupDialog()
        {
            string? resultId = null;

            var dialog = new Window
            {
                Title = Framework.Lang.T("新建快捷方式"),
                Width = 340, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Owner = OwnerWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 15, 10, 5) };
            namePanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("分组名:"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            var nameBox = new TextBox { Width = 220 };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show(Framework.Lang.T("请输入分组名称")); return; }
                var group = new QuickGroup { Name = name, Columns = 2 };
                QuickGroupManager.AddGroup(group, OwnerWindowConfig);
                QuickGroupManager.Save();
                resultId = group.Id;
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            dialog.Content = grid;
            dialog.ShowDialog();

            return resultId!;
        }

        /// <summary>停止所有定时器和清理资源</summary>
        public void Shutdown()
        {
            _foregroundWatcher?.Stop();
            _longTermWatcher?.Stop();

            // 保存分组
            try { QuickGroupManager.RemoveTransientWindowItems(); } catch { }
            QuickGroupManager.Save();

            // 关闭非任务栏窗口
            var preserveHandles = new HashSet<IntPtr>();
            try
            {
                foreach (var grp in QuickGroupManager.GetAll())
                {
                    if (grp?.Items == null) continue;
                    foreach (var it in grp.Items)
                    {
                        if (it != null && it.ShowInTaskbar && it.WindowHandle != IntPtr.Zero)
                            preserveHandles.Add(it.WindowHandle);
                    }
                }
            }
            catch { }

            foreach (var handle in _quickItemHandles)
            {
                if (preserveHandles.Contains(handle)) continue;
                if (NativeMethods.IsWindow(handle))
                {
                    NativeMethods.SendMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            _quickItemHandles.Clear();
        }

        /// <summary>
        /// 仅停止后台监视计时器（不关闭任何窗口）。
        /// 用于面板重新渲染/窗口切换时释放旧服务实例的计时器，避免定时器泄漏，
        /// 也避免旧实例继续被拖放路由引用。
        /// </summary>
        public void StopWatchers()
        {
            _foregroundWatcher?.Stop();
            _foregroundWatcher = null;
            _longTermWatcher?.Stop();
            _longTermWatcher = null;
        }

        /// <summary>处理文件/URL 拖入</summary>
        public void HandleDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent("QuickGroupId")) return; // 内部拖拽不处理

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                // 优先写入本面板绑定的分组；未绑定时才落到默认分组
                string groupId = _boundGroupId ?? QuickGroupManager.GetOrCreateDefaultGroup();
                foreach (string file in files)
                {
                    var item = new QuickItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Type = "program",
                        Target = file,
                        UseAppMode = false
                    };
                    QuickGroupManager.AddItem(groupId, item);
                }
                QuickGroupManager.Save();
                RefreshAfterDrop();
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                string text = (string)e.Data.GetData(DataFormats.UnicodeText);
                if (text.StartsWith("http://") || text.StartsWith("https://"))
                {
                    string groupId = _boundGroupId ?? QuickGroupManager.GetOrCreateDefaultGroup();
                    var item = new QuickItem
                    {
                        Name = text.Length > 30 ? text.Substring(0, 30) + "..." : text,
                        Type = "url",
                        Target = text,
                        UseAppMode = true
                    };
                    QuickGroupManager.AddItem(groupId, item);
                    QuickGroupManager.Save();
                    RefreshAfterDrop();
                }
            }
            e.Handled = true;
        }

        /// <summary>处理 DragOver</summary>
        public void HandleDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent("QuickGroupId")) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        /// <summary>
        /// 拖入完成后刷新 UI：保持与当前绑定模式一致，
        /// 避免 1:1 绑定面板调用 LoadGroups 后错误地显示全部分组。
        /// </summary>
        private void RefreshAfterDrop()
        {
            if (_boundGroupId != null)
                LoadSingleGroup(_boundGroupId);
            else
                LoadGroups();
        }

        #endregion

        #region Group Panel Building

        private Border BuildGroupPanel(QuickGroup group)
        {
            // 切换「隐藏模块外框」并重建当前视图（隐藏后去掉背景/边框/标题栏/内边距，不占位）
            void ToggleHideFrame()
            {
                group.HideFrame = !group.HideFrame;
                QuickGroupManager.UpdateGroup(group);
                QuickGroupManager.Save();
                if (!string.IsNullOrEmpty(_boundGroupId)) LoadSingleGroup(_boundGroupId);
                else LoadGroups();
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(group.HideFrame ? 0 : 4),
                Margin = new Thickness(0, 0, 0, group.HideFrame ? 8 : 5),
                Padding = new Thickness(group.HideFrame ? 0 : 2),
                Tag = group.Id
            };
            ApplyModuleBackground(border);

            var outerPanel = new StackPanel();

            // 标题栏
            var titleBar = new DockPanel { Margin = new Thickness(5, 3, 5, 3) };
            titleBar.Tag = group.Id;
            // 隐藏模块外框时一并隐藏标题栏（不占位）
            if (group.HideFrame)
                titleBar.Visibility = Visibility.Collapsed;

            // 拖拽排序
            Point dragStartPoint = default;
            bool dragReady = false;
            titleBar.MouseLeftButtonDown += (s, ev) => { dragStartPoint = ev.GetPosition(titleBar); dragReady = true; };
            titleBar.MouseMove += (s, ev) =>
            {
                if (dragReady && ev.LeftButton == MouseButtonState.Pressed)
                {
                    Point pos = ev.GetPosition(titleBar);
                    if (Math.Abs(pos.X - dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(pos.Y - dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        dragReady = false;
                        var data = new DataObject("QuickGroupId", group.Id);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                    }
                }
            };
            titleBar.MouseLeftButtonUp += (s, ev) => { dragReady = false; };

            // 右键菜单：编辑/删除整个快捷组
            titleBar.MouseRightButtonUp += (s, ev) =>
            {
                var rmenu = new ContextMenu();
                var editGrpItem = new MenuItem { Header = Framework.Lang.T("编辑快捷组") };
                editGrpItem.Click += (s2, ev2) => ShowEditGroupDialog(group);
                rmenu.Items.Add(editGrpItem);
                var delItem = new MenuItem { Header = Framework.Lang.T("删除快捷组") };
                delItem.Click += (s2, ev2) =>
                {
                    bool hasModules = group.ChildPanels != null && group.ChildPanels.Count > 0;
                    string message = hasModules
                        ? Framework.Lang.TF("分组 \"{0}\" 包含 {1} 个模块，删除后模块也将丢失。确定继续？", group.Name, group.ChildPanels!.Count)
                        : Framework.Lang.TF("确定删除分组 \"{0}\"？", group.Name);
                    if (MessageBox.Show(message, Framework.Lang.T("确认"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        QuickGroupManager.RemoveGroup(group.Id);
                        QuickGroupManager.Save();
                        _container.Children.Remove(border);
                        OnGroupDeleted?.Invoke(group.Id);
                    }
                };
                rmenu.Items.Add(delItem);
                var hideItem = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = group.HideFrame };
                hideItem.Click += (s2, ev2) => ToggleHideFrame();
                rmenu.Items.Add(hideItem);
                rmenu.PlacementTarget = titleBar;
                rmenu.Placement = PlacementMode.MousePoint;
                rmenu.IsOpen = true;
                ev.Handled = true;
            };

            // ▼ 菜单按钮
            var toggleBtn = new Button
            {
                Content = "▼", Width = 22, Height = 22,
                Background = Brushes.Transparent, Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0), FontSize = 10, Padding = new Thickness(0)
            };
            DockPanel.SetDock(toggleBtn, Dock.Right);
            titleBar.Children.Add(toggleBtn);



            // 分组名
            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = group.Name, Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), FontSize = 12,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand
            };
            titleBar.Children.Add(titleText);
            outerPanel.Children.Add(titleBar);

            // 显示模式：grid=大图标网格（默认）、list=单列列表、popup=弹出菜单
            bool isList = group.DisplayMode == "list";
            bool isPopup = group.DisplayMode == "popup";

            // 内容区（列表模式强制单列）
            var contentPanel = new UniformGrid
            {
                Columns = isList ? 1 : group.Columns,
                Visibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed,
                Tag = "content:" + group.Id,
                AllowDrop = true
            };
            
            // 处理项拖放到分组空白区域
            contentPanel.DragOver += (s, ev) =>
            {
                if (ev.Data.GetDataPresent("QuickItemId"))
                {
                    ev.Effects = DragDropEffects.Move;
                    ev.Handled = true;
                }
            };
            
            contentPanel.Drop += (s, ev) =>
            {
                if (!ev.Data.GetDataPresent("QuickItemId")) return;
                
                string draggedItemId = (string)ev.Data.GetData("QuickItemId");
                string sourceGroupId = (string)ev.Data.GetData("QuickItemGroupId");
                
                // 如果是同一个分组内的拖拽，不处理（由组内排序逻辑处理）
                if (sourceGroupId == group.Id)
                {
                    ev.Handled = true;
                    return;
                }
                
                // 从源分组移除
                QuickGroupManager.RemoveItem(sourceGroupId, draggedItemId);
                
                // 获取被拖拽的项
                var allGroups = QuickGroupManager.GetAll();
                var sourceGroup = allGroups.FirstOrDefault(g => g.Id == sourceGroupId);
                var draggedItem = sourceGroup?.Items.FirstOrDefault(i => i.Id == draggedItemId);
                
                if (draggedItem != null)
                {
                    // 添加到目标分组
                    QuickGroupManager.AddItem(group.Id, draggedItem);
                    QuickGroupManager.Save();
                    
                    // 刷新 UI
                    LoadGroups();
                }
                
                ev.Handled = true;
            };

            titleText.MouseLeftButtonDown += (s, ev) =>
            {
                group.IsExpanded = !group.IsExpanded;
                contentPanel.Visibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                QuickGroupManager.UpdateGroup(group);
            };

            // 弹出菜单模式：用单个触发按钮（模块名）代替项目网格，点击后弹出全部项
            if (isPopup)
            {
                outerPanel.Children.Add(BuildPopupTriggerButton(group));
            }
            else
            {
                foreach (var item in group.Items)
                {
                    contentPanel.Children.Add(BuildItemButton(item, group.Id, listMode: isList, largeIcon: !isList));
                }
            }
            
            // 渲染子模块（便签、计时器等）
            if (group.ChildPanels != null && group.ChildPanels.Count > 0)
            {
                foreach (var childPanel in group.ChildPanels)
                {
                    var widget = BuildModuleWidget(childPanel, group.Id);
                    if (widget != null)
                    {
                        // 如果是Timer且SkipWrapper=true，直接添加到contentPanel
                        // 否则需要Wrap一下
                        contentPanel.Children.Add(widget);
                    }
                }
            }
            
            outerPanel.Children.Add(contentPanel);
            border.Child = outerPanel;

            // 隐藏外框后标题栏与 ▼ 菜单不可见，为容器挂右键菜单，确保右键内容区仍可恢复外框
            if (group.HideFrame)
            {
                var bareMenu = new ContextMenu();
                var bareHide = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = true };
                bareHide.Click += (s, ev) => ToggleHideFrame();
                bareMenu.Items.Add(bareHide);
                border.ContextMenu = bareMenu;
            }

            // ▼ 菜单
            toggleBtn.Click += (s, ev) =>
            {
                var popup = new ContextMenu();

                // 添加子菜单
                var addSubMenu = new MenuItem { Header = Framework.Lang.T("添加") };
                var miProg = new MenuItem { Header = Framework.Lang.T("添加程序") };
                miProg.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "program");
                addSubMenu.Items.Add(miProg);
                var miUrl = new MenuItem { Header = Framework.Lang.T("添加网址") };
                miUrl.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "url");
                addSubMenu.Items.Add(miUrl);
                var miWin = new MenuItem { Header = Framework.Lang.T("添加窗口") };
                miWin.Click += (s2, ev2) => ShowPickWindowDialog(group.Id);
                addSubMenu.Items.Add(miWin);
                popup.Items.Add(addSubMenu);
                popup.Items.Add(new Separator());

                var expandItem = new MenuItem { Header = group.IsExpanded ? Framework.Lang.T("收起") : Framework.Lang.T("展开") };
                expandItem.Click += (s2, ev2) =>
                {
                    group.IsExpanded = !group.IsExpanded;
                    contentPanel.Visibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                    QuickGroupManager.UpdateGroup(group);
                };
                popup.Items.Add(expandItem);
                popup.Items.Add(new Separator());

                // 编辑快捷组（分组名/列数/显示模式/所属团队，与右键标题栏入口同对话框；重命名已合并到此入口）
                var editGrpMenuItem = new MenuItem { Header = Framework.Lang.T("编辑快捷组") };
                editGrpMenuItem.Click += (s2, ev2) => ShowEditGroupDialog(group);
                popup.Items.Add(editGrpMenuItem);

                var colItem = new MenuItem { Header = Framework.Lang.T("修改列数") };
                for (int c = 1; c <= 4; c++)
                {
                    int col = c;
                    var mi = new MenuItem { Header = Framework.Lang.TF("{0} 列", col), IsChecked = (group.Columns == col) };
                    mi.Click += (s2, ev2) => { group.Columns = col; if (group.DisplayMode != "list") contentPanel.Columns = col; QuickGroupManager.UpdateGroup(group); QuickGroupManager.Save(); };
                    colItem.Items.Add(mi);
                }
                popup.Items.Add(colItem);

                // 显示模式子菜单：大图标 / 列表 / 弹出菜单
                var displayItem = new MenuItem { Header = Framework.Lang.T("显示") };
                var modeGrid = new MenuItem { Header = Framework.Lang.T("大图标"), IsCheckable = true, IsChecked = group.DisplayMode == "grid" };
                modeGrid.Click += (s2, ev2) => SetDisplayMode(group, "grid");
                displayItem.Items.Add(modeGrid);
                var modeList = new MenuItem { Header = Framework.Lang.T("列表"), IsCheckable = true, IsChecked = group.DisplayMode == "list" };
                modeList.Click += (s2, ev2) => SetDisplayMode(group, "list");
                displayItem.Items.Add(modeList);
                var modePopup = new MenuItem { Header = Framework.Lang.T("弹出菜单"), IsCheckable = true, IsChecked = group.DisplayMode == "popup" };
                modePopup.Click += (s2, ev2) => SetDisplayMode(group, "popup");
                displayItem.Items.Add(modePopup);
                popup.Items.Add(displayItem);
                popup.Items.Add(new Separator());

                var tileItem = new MenuItem { Header = Framework.Lang.T("平铺窗口") };
                tileItem.Click += (s2, ev2) => TileGroupWindows(group);
                popup.Items.Add(tileItem);
                popup.Items.Add(new Separator());

                var hideFrameItem = new MenuItem { Header = Framework.Lang.T("隐藏模块外框"), IsCheckable = true, IsChecked = group.HideFrame };
                hideFrameItem.Click += (s2, ev2) => ToggleHideFrame();
                popup.Items.Add(hideFrameItem);
                popup.Items.Add(new Separator());

                var deleteItem = new MenuItem { Header = Framework.Lang.T("删除快捷组") };
                deleteItem.Click += (s2, ev2) =>
                {
                    bool hasModules = group.ChildPanels != null && group.ChildPanels.Count > 0;
                    string message;
                    if (hasModules)
                    {
                        int count = group.ChildPanels!.Count;
                        message = Framework.Lang.TF("分组 \"{0}\" 包含 {1} 个模块，删除后模块也将丢失。确定继续？", group.Name, count);
                    }
                    else
                    {
                        message = Framework.Lang.TF("确定删除分组 \"{0}\"？", group.Name);
                    }

                    if (MessageBox.Show(message, Framework.Lang.T("确认"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        QuickGroupManager.RemoveGroup(group.Id);
                        QuickGroupManager.Save();
                        _container.Children.Remove(border);
                        OnGroupDeleted?.Invoke(group.Id);
                    }
                };
                popup.Items.Add(deleteItem);

                popup.PlacementTarget = toggleBtn;
                popup.Placement = PlacementMode.Bottom;
                popup.IsOpen = true;
            };



            return border;
        }

        /// <summary>弹出菜单模式：构建以分组名为文字的触发按钮，点击后弹出包含全部快捷项的浮层。</summary>
        /// <remarks>WPF 默认 MenuItem 模板无法为每个菜单项干净地呈现自定义背景色，故改用 Popup + 真实按钮列表（类似菜单）。</remarks>
        private FrameworkElement BuildPopupTriggerButton(QuickGroup group)
        {
            // 按钮内容：分组名 + 下拉箭头（提示可展开）
            var triggerContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            triggerContent.Children.Add(new Emoji.Wpf.TextBlock { Text = group.Name, VerticalAlignment = VerticalAlignment.Center });
            triggerContent.Children.Add(new Emoji.Wpf.TextBlock { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0), Opacity = 0.75 });

            var triggerBtn = new Button
            {
                Content = triggerContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Background = (Brush)Application.Current.FindResource("BrushAccent"),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };

            var itemPopup = new Popup
            {
                PlacementTarget = triggerBtn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var popupStack = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var item in group.Items)
            {
                popupStack.Children.Add(BuildItemButton(item, group.Id, listMode: true, afterClick: () => itemPopup.IsOpen = false));
            }

            var popupScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 360,
                Content = popupStack
            };

            var popupBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("BrushBG"),
                BorderBrush = (Brush)Application.Current.FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 160,
                Child = popupScroll
            };
            itemPopup.Child = popupBorder;

            triggerBtn.Click += (s, e) => { itemPopup.IsOpen = !itemPopup.IsOpen; };

            return triggerBtn;
        }

        /// <summary>切换分组显示模式并重建视图。</summary>
        private void SetDisplayMode(QuickGroup group, string mode)
        {
            group.DisplayMode = mode;
            QuickGroupManager.UpdateGroup(group);
            QuickGroupManager.Save();
            RefreshGroupView();
        }

        /// <summary>重建当前分组视图（显示模式切换、增删项后使用）。</summary>
        private void RefreshGroupView()
        {
            if (!string.IsNullOrEmpty(_boundGroupId)) LoadSingleGroup(_boundGroupId);
            else LoadGroups();
        }

        /// <summary>将新增快捷项刷新到 UI：网格模式增量追加到 contentPanel，列表/弹出模式整体重建视图。</summary>
        private void AddNewItemToUI(QuickItem newItem, string groupId, UniformGrid? contentPanel)
        {
            var group = QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == groupId);
            if ((group?.DisplayMode ?? "grid") == "grid" && contentPanel != null)
            {
                contentPanel.Children.Add(BuildItemButton(newItem, groupId));
                return;
            }
            RefreshGroupView();
        }

        #endregion

        #region Module Widget Building

        /// <summary>
        /// 构建模块widget（便签、计时器等）
        /// </summary>
        private FrameworkElement BuildModuleWidget(PanelItem panel, string groupId)
        {
            switch (panel.Type)
            {
                case PanelType.StickyNotes:
                    return BuildStickyNotesModule(panel, groupId);
                case PanelType.Timer:
                    return BuildTimerModule(panel, groupId);
                case PanelType.Countdown:
                    return BuildCountdownModule(panel, groupId);
                case PanelType.Clipboard:
                    return BuildClipboardModule(panel, groupId);
                case PanelType.UsageStats:
                    return BuildUsageStatsModule(panel, groupId);
                case PanelType.AIChat:
                    return BuildAIChatModule(panel, groupId);
                case PanelType.SystemMonitor:
                    return BuildSystemMonitorModule(panel, groupId);
                case PanelType.FileArea:
                    return BuildFileAreaModule(panel, groupId);
                case PanelType.DataDashboard:
                    return BuildDataDashboardModule(panel, groupId);
                case PanelType.Plugin:
                    return BuildPluginModule(panel, groupId);
                default:
                    return null;
            }
        }

        private FrameworkElement BuildStickyNotesModule(PanelItem panel, string groupId)
        {
            // 初始化便签列表
            if (panel.StickyNotes == null)
            {
                panel.StickyNotes = new List<StickyNote>();
            }

            var container = new StackPanel();

            // 添加便签按钮
            var addBtn = new Button
            {
                Content = Framework.Lang.T("+ 添加便签"),
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            addBtn.Click += (s, e) =>
            {
                var note = new StickyNote();
                panel.StickyNotes.Insert(0, note);
                QuickGroupManager.Save();
                LoadGroups();
            };
            container.Children.Add(addBtn);

            // 便签列表
            foreach (var note in panel.StickyNotes)
            {
                container.Children.Add(BuildStickyNoteItem(note, panel));
            }

            return WrapWithThemeBackground(container);
        }

        private FrameworkElement BuildStickyNoteItem(StickyNote note, PanelItem panel)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            // 删除按钮
            var delBtn = new Button
            {
                Content = "×",
                Width = 20,
                Height = 20,
                FontSize = 14,
                Background = Brushes.Transparent,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#FF6B6B"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            row.Children.Add(delBtn);

            // 文本预览
            var textBlock = new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrEmpty(note.Content) ? Framework.Lang.T("空便签") : note.Content,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#CCCCCC"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200,
                ToolTip = note.Content?.Length > 200 ? note.Content.Substring(0, 200) + "..." : note.Content
            };
            row.Children.Add(textBlock);

            delBtn.Click += (s, e) =>
            {
                panel.StickyNotes.RemoveAll(n => n.Id == note.Id);
                QuickGroupManager.Save();
                LoadGroups();
                e.Handled = true;
            };

            return row;
        }

        private FrameworkElement BuildTimerModule(PanelItem panel, string groupId)
        {
            // 初始化计时器列表
            panel.TimerItems ??= new List<TimerItem>();

            var container = new StackPanel();

            // ✅ SkipWrapper=true 时不显示外层容器（标题+添加按钮），直接返回计时器卡片
            if (!panel.SkipWrapper)
            {
                // 添加计时器按钮
                var addBtn = new Button
                {
                    Content = Framework.Lang.T("+ 添加计时器"),
                    Height = 28,
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = Cursors.Hand
                };
                addBtn.Click += (s, e) =>
                {
                    panel.TimerItems.Add(new TimerItem());
                    QuickGroupManager.Save();
                    LoadGroups();
                };
                container.Children.Add(addBtn);
            }

            // 渲染每个计时器
            foreach (var timer in panel.TimerItems)
            {
                container.Children.Add(BuildTimerCard(timer, panel));
            }

            return WrapWithThemeBackground(container);
        }

        private FrameworkElement BuildCountdownModule(PanelItem panel, string groupId)
        {
            panel.CountdownItems ??= new List<CountdownItem>();

            var container = new StackPanel();

            if (panel.CountdownItems.Count == 0)
            {
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无倒计时（在主面板中使用完整功能）"),
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                    FontSize = 11,
                    Opacity = 0.7,
                    Margin = new Thickness(4)
                });
            }

            foreach (var cd in panel.CountdownItems)
            {
                var remaining = cd.GetRemaining();
                var status = cd.IsFinished ? Framework.Lang.T("✔ 已到期") : cd.IsRunning ? Framework.Lang.T("▶ 进行中") : Framework.Lang.T("⏸ 待开始");
                container.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"{cd.Name}  {(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}  {status}",
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    FontSize = 12,
                    Margin = new Thickness(4, 2, 4, 2)
                });
            }

            return WrapWithThemeBackground(container);
        }

        private FrameworkElement BuildTimerCard(TimerItem timer, PanelItem panel)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 0, 3)
            };
            ApplyModuleBackground(card);

            var mainStack = new StackPanel();
            
            // 时间显示（先声明，供 toggleBtn 的点击事件引用）
            var timeDisplay = new Emoji.Wpf.TextBlock
            {
                Text = FormatTimeSpan(timer.GetElapsed()),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };
            // 用 Viewbox 包裹实现自适应缩放：时间超长时自动缩小字号以完整显示
            var timeBox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = timeDisplay
            };
            
            // 标题行
            var titleRow = new DockPanel();
            var titleLabel = new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrEmpty(timer.Name) ? Framework.Lang.T("新计时") : timer.Name,
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(titleLabel);
            
            // 开始/停止圆钮（放在标题右侧）
            var toggleBtn = new Button
            {
                Content = timer.IsRunning ? "⏹" : "▶",
                Width = 22,
                Height = 22,
                FontSize = 10,
                Background = timer.IsRunning ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                Foreground = (Brush)Application.Current.FindResource("BrushAccentText"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Template = new ControlTemplate(typeof(Button))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border))
                }
            };
            // 用圆角 Border 包裹实现圆形按钮效果
            var toggleBtnBorder = new Border
            {
                CornerRadius = new CornerRadius(11),
                Child = toggleBtn
            };
            DockPanel.SetDock(toggleBtnBorder, Dock.Right);
            toggleBtn.Click += (s, e) =>
            {
                if (!timer.IsRunning)
                {
                    timer.IsRunning = true;
                    timer.StartTime = DateTime.Now;
                    QuickGroupManager.Save();
                    StartTimerUpdate(timer, timeDisplay, panel, toggleBtn);
                    toggleBtn.Content = "⏹";
                    toggleBtn.Background = Brushes.IndianRed;
                }
                else
                {
                    if (timer.StartTime.HasValue)
                        timer.ElapsedTime += (DateTime.Now - timer.StartTime.Value);
                    timer.IsRunning = false;
                    timer.StartTime = null;
                    AccumulateToDailyRecord(timer);
                    QuickGroupManager.Save();
                    LoadGroups();
                }
            };
            titleRow.Children.Add(toggleBtnBorder);
            
            // 编辑按钮
            var editBtn = new Button
            {
                Content = "",
                Width = 24,
                Height = 24,
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#9CA3AF"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            DockPanel.SetDock(editBtn, Dock.Right);
            editBtn.Click += (s, e) => ShowEditTimerDialog(timer, panel);
            titleRow.Children.Add(editBtn);
            
            mainStack.Children.Add(titleRow);
            mainStack.Children.Add(timeBox);
            
            // 今日统计
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (timer.DailyRecords.TryGetValue(today, out int todaySeconds) && todaySeconds > 0)
            {
                var todayText = new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.TF("今日: {0}", FormatTimeSpan(TimeSpan.FromSeconds(todaySeconds))),
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#9CA3AF"),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                mainStack.Children.Add(todayText);
            }

            card.Child = mainStack;
            return card;
        }

        private void StartTimerUpdate(TimerItem timer, TextBlock display, PanelItem panel, Button toggleBtn)
        {
            var timerObj = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timerObj.Tick += (s, e) =>
            {
                if (!timer.IsRunning)
                {
                    timerObj.Stop();
                    return;
                }
                display.Text = FormatTimeSpan(timer.GetElapsed());
            };
            timerObj.Start();
        }

        private void ShowEditTimerDialog(TimerItem timer, PanelItem panel)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("编辑计时器"),
                Width = 380,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain")
            };

            var rootPanel = new StackPanel { Margin = new Thickness(16) };

            // 标题
            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("编辑计时器"),
                FontSize = (double)Application.Current.FindResource("FontSizeSubtitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            rootPanel.Children.Add(titleText);

            // 名称标签
            var nameLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("名称"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmallPlus"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            rootPanel.Children.Add(nameLabel);

            // 名称输入框
            var nameBox = new TextBox
            {
                Text = timer.Name,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            rootPanel.Children.Add(nameBox);

            // 超时标签
            var timeoutLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("超时（秒）"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmallPlus"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            rootPanel.Children.Add(timeoutLabel);

            // 超时输入框
            var timeoutBox = new TextBox
            {
                Text = timer.TimeoutSeconds > 0 ? timer.TimeoutSeconds.ToString() : "",
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle"),
                Margin = new Thickness(0, 0, 0, 20)
            };
            rootPanel.Children.Add(timeoutBox);

            // 按钮区域
            var btnPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Right 
            };

            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"),
                Style = (Style)Application.Current.FindResource("StandardButtonStyle"),
                Width = 75,
                Margin = new Thickness(0, 0, 10, 0)
            };
            cancelBtn.Click += (s, e) => dialog.Close();

            var saveBtn = new Button
            {
                Content = Framework.Lang.T("保存"),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
                Width = 75
            };
            saveBtn.Click += (s, e) =>
            {
                timer.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Framework.Lang.T("计时器") : nameBox.Text.Trim();
                if (int.TryParse(timeoutBox.Text, out int timeout) && timeout > 0)
                {
                    timer.TimeoutSeconds = timeout;
                }
                QuickGroupManager.Save();
                LoadGroups();
                dialog.Close();
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            rootPanel.Children.Add(btnPanel);

            dialog.Content = rootPanel;
            dialog.ShowDialog();
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private void AccumulateToDailyRecord(TimerItem timer)
        {
            var elapsed = timer.GetElapsed();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!timer.DailyRecords.ContainsKey(today))
                timer.DailyRecords[today] = 0;
            timer.DailyRecords[today] += (int)elapsed.TotalSeconds;
        }

        // 其他模块的占位实现
        private FrameworkElement BuildClipboardModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("📋 剪贴板收藏"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });
        private FrameworkElement BuildUsageStatsModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("📊 使用统计"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });
        private FrameworkElement BuildAIChatModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("🤖 AI 对话"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });
        private FrameworkElement BuildSystemMonitorModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("💻 系统信息"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });
        private FrameworkElement BuildFileAreaModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("📁 文件区"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });
        private FrameworkElement BuildDataDashboardModule(PanelItem panel, string groupId) => WrapWithThemeBackground(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("📈 数据看板"), Foreground = (Brush)Application.Current.FindResource("BrushTextMain"), Margin = new Thickness(8) });

        private FrameworkElement BuildPluginModule(PanelItem panel, string groupId)
        {
            var pluginId = panel.PluginId;
            if (string.IsNullOrEmpty(pluginId))
                return WrapWithThemeBackground(new TextBlock { Text = Framework.Lang.T("未指定插件"), Opacity = 0.6, Margin = new Thickness(8) });

            var loader = Plugins.PluginLoaderService.Instance;
            var ui = loader.CreatePluginUI(pluginId);
            if (ui == null)
                return WrapWithThemeBackground(new TextBlock { Text = Framework.Lang.TF("插件未加载: {0}", pluginId), Opacity = 0.6, Margin = new Thickness(8) });

            // UIElement 包装为 FrameworkElement
            var host = ui as FrameworkElement ?? new ContentControl { Content = ui };
            return WrapWithThemeBackground(host);
        }

        private FrameworkElement BuildItemButton(QuickItem item, string groupId, bool listMode = false, bool largeIcon = false, Action? afterClick = null)
        {
            var panel = new DockPanel { Margin = new Thickness(2), Tag = item.Id };

            Brush bgBrush;
            try { bgBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(item.BackgroundColor)!; }
            catch { bgBrush = Brushes.SteelBlue; }

            var btn = new Button
            {
                Padding = new Thickness(6, 4, 6, 4),
                Background = bgBrush,
                Foreground = (Brush)Application.Current.FindResource("BrushItemButtonText"),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 11,
                AllowDrop = true
            };

            // 按钮内容：大图标模式 = 桌面快捷方式风格（纯大图标在上、名称在下、无按钮背景）；列表模式水平居中；默认横向图标+名称
            if (largeIcon)
            {
                btn.Content = BuildLargeIconContent(item);
                btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                btn.Padding = new Thickness(2, 6, 2, 4);
                // 纯图标风格：去掉按钮背景色，名称用主题文字色（同桌面快捷方式，无边框包裹）
                btn.Background = Brushes.Transparent;
                btn.Foreground = (Brush)Application.Current.FindResource("BrushTextMain");
            }
            else
            {
                btn.Content = BuildItemContent(item, HorizontalAlignment.Center);
                if (listMode)
                {
                    btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                    btn.Padding = new Thickness(6, 3, 6, 3);
                }
            }

            // 圆角样式（保留每项自定义背景色与拖拽高亮边框）
            var roundedStyle = Application.Current.TryFindResource("QuickItemButtonStyle") as Style;
            if (roundedStyle != null) btn.Style = roundedStyle;
            
            // 拖拽开始：检测鼠标移动距离后启动拖拽
            Point? dragStartPoint = null;
            bool isDragging = false;
            
            btn.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                if (!isDragging)
                {
                    dragStartPoint = ev.GetPosition(btn);
                }
            };
            
            btn.PreviewMouseMove += (s, ev) =>
            {
                if (dragStartPoint == null || isDragging) return;
                
                Point currentPoint = ev.GetPosition(btn);
                Vector diff = dragStartPoint.Value - currentPoint;
                
                // 鼠标移动超过阈值，启动拖拽
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    isDragging = true;
                    try
                    {
                        var dragData = new DataObject("QuickItemId", item.Id);
                        dragData.SetData("QuickItemGroupId", groupId);
                        DragDrop.DoDragDrop(btn, dragData, DragDropEffects.Move);
                    }
                    finally
                    {
                        isDragging = false;
                        dragStartPoint = null;
                    }
                }
            };
            
            // 拖拽进入：显示可放置提示
            btn.DragEnter += (s, ev) =>
            {
                if (ev.Data.GetDataPresent("QuickItemId"))
                {
                    ev.Effects = DragDropEffects.Move;
                    btn.BorderBrush = ActiveHighlightBorder;
                    ev.Handled = true;
                }
            };
            
            // 拖拽离开：清除提示
            btn.DragLeave += (s, ev) =>
            {
                if (ev.Data.GetDataPresent("QuickItemId"))
                {
                    btn.BorderBrush = Brushes.Transparent;
                    ev.Handled = true;
                }
            };
            
            // 放下：移动项到目标分组
            btn.Drop += (s, ev) =>
            {
                if (!ev.Data.GetDataPresent("QuickItemId")) return;
                
                string draggedItemId = (string)ev.Data.GetData("QuickItemId");
                string sourceGroupId = (string)ev.Data.GetData("QuickItemGroupId");
                
                // 如果是同一个分组，不处理（组内排序由其他逻辑处理）
                if (sourceGroupId == groupId && draggedItemId == item.Id)
                {
                    btn.BorderBrush = Brushes.Transparent;
                    ev.Handled = true;
                    return;
                }
                
                // 从源分组移除
                QuickGroupManager.RemoveItem(sourceGroupId, draggedItemId);
                
                // 获取被拖拽的项
                var allGroups = QuickGroupManager.GetAll();
                var sourceGroup = allGroups.FirstOrDefault(g => g.Id == sourceGroupId);
                var draggedItem = sourceGroup?.Items.FirstOrDefault(i => i.Id == draggedItemId);
                
                if (draggedItem != null)
                {
                    // 添加到目标分组
                    QuickGroupManager.AddItem(groupId, draggedItem);
                    QuickGroupManager.Save();
                    
                    // 刷新 UI
                    LoadGroups();
                }
                
                btn.BorderBrush = Brushes.Transparent;
                ev.Handled = true;
            };
            
            btn.Click += (s, ev) => { OpenQuickItem(item); afterClick?.Invoke(); };

            // 置顶标记：白色小圆点
            var pinDot = new System.Windows.Shapes.Ellipse
            {
                Width = 4, Height = 4,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Visibility = item.AlwaysOnTop ? Visibility.Visible : Visibility.Collapsed
            };

            var grid = new Grid();
            grid.Children.Add(btn);
            grid.Children.Add(pinDot);
            panel.Children.Add(grid);

            _itemButtonRefs.Add(new ItemButtonRef { Item = item, Button = btn, BaseBackground = bgBrush });

            // 右键菜单
            var menu = new ContextMenu();
            var editItem = new MenuItem { Header = Framework.Lang.T("编辑") };
            editItem.Click += (s, ev) => ShowEditItemDialog(item, groupId, btn);
            menu.Items.Add(editItem);

            var topmostItem = new MenuItem { Header = Framework.Lang.T("窗口置顶"), IsCheckable = true, IsChecked = item.AlwaysOnTop };
            topmostItem.Click += (s, ev) => { item.AlwaysOnTop = topmostItem.IsChecked; pinDot.Visibility = item.AlwaysOnTop ? Visibility.Visible : Visibility.Collapsed; ApplyTopmostIfPossible(item); QuickGroupManager.Save(); };
            menu.Items.Add(topmostItem);

            var taskbarItem = new MenuItem { Header = Framework.Lang.T("在任务栏显示"), IsCheckable = true, IsChecked = item.ShowInTaskbar };
            taskbarItem.Click += (s, ev) => { item.ShowInTaskbar = taskbarItem.IsChecked; ApplyTaskbarVisibility(item); QuickGroupManager.Save(); };
            menu.Items.Add(taskbarItem);

            var closeItem = new MenuItem { Header = Framework.Lang.T("关闭窗口") };
            closeItem.Click += (s, ev) => CloseItemWindow(item);
            menu.Items.Add(closeItem);
            menu.Items.Add(new Separator());

            var delItem = new MenuItem { Header = Framework.Lang.T("删除") };
            delItem.Click += (s, ev) =>
            {
                QuickGroupManager.RemoveItem(groupId, item.Id);
                QuickGroupManager.Save();
                _itemButtonRefs.RemoveAll(r => r.Item == item);
                (panel.Parent as UniformGrid)?.Children.Remove(panel);
            };
            menu.Items.Add(delItem);

            menu.Opened += (s, ev) =>
            {
                topmostItem.IsChecked = item.AlwaysOnTop;
                taskbarItem.IsChecked = item.ShowInTaskbar;
                closeItem.IsEnabled = item.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(item.WindowHandle);
            };
            btn.ContextMenu = menu;

            return panel;
        }

        #endregion

        #region Item Actions

        private void OpenQuickItem(QuickItem item)
        {
            try
            {
                if (TryActivateExistingWindow(item)) return;

                if (item.Type == "program")
                {
                    var psi = new ProcessStartInfo();
                    string target = item.Target ?? "";
                    int spaceIdx = target.IndexOf(' ');
                    if (spaceIdx > 0 && !File.Exists(target))
                    {
                        psi.FileName = target.Substring(0, spaceIdx);
                        psi.Arguments = target.Substring(spaceIdx + 1);
                    }
                    else
                    {
                        psi.FileName = target;
                    }
                    if (!string.IsNullOrEmpty(item.LaunchArgs))
                    {
                        psi.Arguments = string.IsNullOrEmpty(psi.Arguments)
                            ? item.LaunchArgs
                            : psi.Arguments + " " + item.LaunchArgs;
                    }
                    psi.UseShellExecute = true;
                    var proc = Process.Start(psi);
                    if (proc != null) TrackProcessMainWindow(item, proc);
                }
                else
                {
                    // URL
                    // 优先检查 ProgramRegistry 浏览器配置
                    if (!string.IsNullOrEmpty(item.BrowserId))
                    {
                        var app = (App)Application.Current;
                        var browser = app.ProgramRegistry.GetBrowserById(item.BrowserId);
                        if (browser != null && File.Exists(browser.ExePath))
                        {
                            string args = item.Target ?? "";
                            string regProfileArgs = "";

                            if (!string.IsNullOrEmpty(item.ProfileId))
                            {
                                var profile = app.ProgramRegistry.GetProfileById(item.BrowserId, item.ProfileId);
                                if (profile != null && !string.IsNullOrEmpty(profile.Directory))
                                {
                                    regProfileArgs = $" --user-data-dir=\"{profile.Directory}\"";
                                }
                            }

                            if (item.UseAppMode)
                            {
                                args = $"--app={item.Target}" + regProfileArgs;
                            }
                            else
                            {
                                args = (item.Target ?? "") + regProfileArgs;
                            }

                            var process = new Process();
                            process.StartInfo.FileName = browser.ExePath;
                            process.StartInfo.Arguments = args;
                            process.Start();

                            string pName = Path.GetFileNameWithoutExtension(browser.ExePath);
                            Task.Run(async () =>
                            {
                                IntPtr foundHandle = IntPtr.Zero;
                                int attempts = 0;
                                while (foundHandle == IntPtr.Zero && attempts < 20)
                                {
                                    await Task.Delay(500);
                                    attempts++;
                                    foundHandle = WindowFinder.FindNewWindow(pName, _quickItemHandles);
                                }
                                if (foundHandle != IntPtr.Zero)
                                {
                                    await _dispatcher.InvokeAsync(() =>
                                    {
                                        item.WindowHandle = foundHandle;
                                        _quickItemHandles.Add(foundHandle);
                                        ApplyTaskbarVisibility(item);
                                        ApplyTopmostIfPossible(item);
                                    });
                                }
                            });
                            return;
                        }
                    }

                    // 回退到原有 BrowserProfileManager 逻辑
                    string? browserExe = null;
                    var exeEntry = BrowserProfileManager.GetExeById(item.BrowserExeId);
                    if (exeEntry != null && !string.IsNullOrEmpty(exeEntry.ExePath))
                        browserExe = exeEntry.ExePath;

                    bool hasProfile = false;
                    string profileArgs = "";
                    var profileEntry = BrowserProfileManager.GetProfileById(item.BrowserProfileId);
                    if (profileEntry != null && !string.IsNullOrEmpty(profileEntry.UserDataDir))
                    {
                        hasProfile = true;
                        profileArgs = $" --user-data-dir=\"{profileEntry.UserDataDir}\"";
                    }

                    // 需要用特定浏览器启动的情况：App模式 或 指定了profile
                    bool needSpecificBrowser = item.UseAppMode || hasProfile;

                    if (needSpecificBrowser)
                    {
                        // 确定浏览器完整路径
                        if (string.IsNullOrEmpty(browserExe))
                            browserExe = FindEdgePath();

                        if (string.IsNullOrEmpty(browserExe))
                        {
                            // 找不到浏览器，回退到系统默认浏览器（无法支持app模式和profile）
                            Process.Start(new ProcessStartInfo(item.Target ?? "") { UseShellExecute = true });
                            return;
                        }

                        string args = item.UseAppMode ? $"--app={item.Target}" : (item.Target ?? "");
                        args += profileArgs;

                        var process = new Process();
                        process.StartInfo.FileName = browserExe;
                        process.StartInfo.Arguments = args;
                        process.Start();

                        string pName = Path.GetFileNameWithoutExtension(browserExe);
                        Task.Run(async () =>
                        {
                            IntPtr foundHandle = IntPtr.Zero;
                            int attempts = 0;
                            while (foundHandle == IntPtr.Zero && attempts < 20)
                            {
                                await Task.Delay(500);
                                attempts++;
                                foundHandle = WindowFinder.FindNewWindow(pName, _quickItemHandles);
                            }
                            if (foundHandle != IntPtr.Zero)
                            {
                                await _dispatcher.InvokeAsync(() =>
                                {
                                    item.WindowHandle = foundHandle;
                                    _quickItemHandles.Add(foundHandle);
                                    ApplyTaskbarVisibility(item);
                                    ApplyTopmostIfPossible(item);
                                });
                            }
                        });
                    }
                    else
                    {
                        // 普通模式：使用系统默认浏览器
                        if (!string.IsNullOrEmpty(browserExe))
                        {
                            // 用户指定了浏览器exe
                            var process = new Process();
                            process.StartInfo.FileName = browserExe;
                            process.StartInfo.Arguments = item.Target ?? "";
                            process.Start();

                            string pName = Path.GetFileNameWithoutExtension(browserExe);
                            Task.Run(async () =>
                            {
                                IntPtr foundHandle = IntPtr.Zero;
                                int attempts = 0;
                                while (foundHandle == IntPtr.Zero && attempts < 20)
                                {
                                    await Task.Delay(500);
                                    attempts++;
                                    foundHandle = WindowFinder.FindNewWindow(pName, _quickItemHandles);
                                }
                                if (foundHandle != IntPtr.Zero)
                                {
                                    await _dispatcher.InvokeAsync(() =>
                                    {
                                        item.WindowHandle = foundHandle;
                                        _quickItemHandles.Add(foundHandle);
                                        ApplyTaskbarVisibility(item);
                                        ApplyTopmostIfPossible(item);
                                    });
                                }
                            });
                        }
                        else
                        {
                            // 使用系统默认浏览器
                            Process.Start(new ProcessStartInfo(item.Target ?? "") { UseShellExecute = true });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("打开失败：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string? FindEdgePath()
        {
            var paths = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
            };
            return paths.FirstOrDefault(File.Exists);
        }

        private bool TryActivateExistingWindow(QuickItem item)
        {
            if (item.WindowHandle == IntPtr.Zero) return false;
            var handle = item.WindowHandle;
            if (!NativeMethods.IsWindow(handle))
            {
                _quickItemHandles.Remove(handle);
                item.WindowHandle = IntPtr.Zero;
                return false;
            }
            try
            {
                if (NativeMethods.IsIconic(handle))
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(handle);
                ApplyTopmostIfPossible(item);
            }
            catch { }
            return true;
        }

        private void TrackProcessMainWindow(QuickItem item, Process proc)
        {
            Task.Run(async () =>
            {
                IntPtr handle = IntPtr.Zero;
                int attempts = 0;
                while (handle == IntPtr.Zero && attempts < 30)
                {
                    await Task.Delay(500);
                    attempts++;
                    try
                    {
                        if (proc.HasExited) break;
                        proc.Refresh();
                        if (proc.MainWindowHandle != IntPtr.Zero)
                            handle = proc.MainWindowHandle;
                    }
                    catch { break; }
                }
                if (handle != IntPtr.Zero)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        item.WindowHandle = handle;
                        _quickItemHandles.Add(handle);
                        ApplyTaskbarVisibility(item);
                        ApplyTopmostIfPossible(item);
                    });
                }
            });
        }

        private void CloseItemWindow(QuickItem item)
        {
            if (item.WindowHandle == IntPtr.Zero) return;
            var handle = item.WindowHandle;
            try
            {
                if (NativeMethods.IsWindow(handle))
                    NativeMethods.SendMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            _quickItemHandles.Remove(handle);
            item.WindowHandle = IntPtr.Zero;
        }

        private void ApplyTaskbarVisibility(QuickItem item)
        {
            if (item.WindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(item.WindowHandle)) return;
            try
            {
                var taskbarList = (NativeMethods.ITaskbarList)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"))!)!;
                taskbarList.HrInit();
                if (item.ShowInTaskbar)
                    taskbarList.AddTab(item.WindowHandle);
                else
                    taskbarList.DeleteTab(item.WindowHandle);
                Marshal.ReleaseComObject(taskbarList);
            }
            catch { }
        }

        private void ApplyTopmostIfPossible(QuickItem item)
        {
            if (item.WindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(item.WindowHandle)) return;
            try
            {
                IntPtr flag = item.AlwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST;
                NativeMethods.SetWindowPos(item.WindowHandle, flag, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        #endregion

        #region Drag & Drop (Group Reorder)

        private void GroupContainer_DragOver(object sender, DragEventArgs ev)
        {
            if (!ev.Data.GetDataPresent("QuickGroupId"))
            {
                ev.Effects = DragDropEffects.None;
                ev.Handled = true;
                return;
            }
            ev.Effects = DragDropEffects.Move;
            ev.Handled = true;
            int insertIdx = GetDropIndex(ev);
            ShowDragIndicator(insertIdx);
        }

        private void GroupContainer_Drop(object sender, DragEventArgs ev)
        {
            HideDragIndicator();
            if (!ev.Data.GetDataPresent("QuickGroupId")) return;
            string draggedId = (string)ev.Data.GetData("QuickGroupId");
            int insertIdx = GetDropIndex(ev);
            MoveGroupToIndex(draggedId, insertIdx);
            ev.Handled = true;
        }

        private int GetDropIndex(DragEventArgs ev)
        {
            Point pos = ev.GetPosition(_container);
            double y = 0;
            for (int i = 0; i < _container.Children.Count; i++)
            {
                var child = _container.Children[i] as FrameworkElement;
                if (child == null || child == _dragIndicator) continue;
                double childH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
                if (pos.Y < y + childH / 2) return i;
                y += childH;
            }
            return _container.Children.Count;
        }

        private void ShowDragIndicator(int insertIdx)
        {
            if (_dragIndicator == null) return;
            if (_container.Children.Contains(_dragIndicator))
                _container.Children.Remove(_dragIndicator);
            _dragIndicator.Visibility = Visibility.Visible;
            if (insertIdx >= _container.Children.Count)
                _container.Children.Add(_dragIndicator);
            else
                _container.Children.Insert(insertIdx, _dragIndicator);
        }

        private void HideDragIndicator()
        {
            if (_dragIndicator == null) return;
            _dragIndicator.Visibility = Visibility.Collapsed;
            if (_container.Children.Contains(_dragIndicator))
                _container.Children.Remove(_dragIndicator);
        }

        private void MoveGroupToIndex(string draggedId, int insertIdx)
        {
            Border? draggedBorder = null;
            int draggedIdx = -1;
            for (int i = 0; i < _container.Children.Count; i++)
            {
                var child = _container.Children[i] as Border;
                if (child != null && (child.Tag as string) == draggedId)
                {
                    draggedBorder = child;
                    draggedIdx = i;
                    break;
                }
            }
            if (draggedBorder == null || draggedIdx == insertIdx || draggedIdx == insertIdx - 1) return;

            _container.Children.RemoveAt(draggedIdx);
            int newIdx = insertIdx > draggedIdx ? insertIdx - 1 : insertIdx;
            _container.Children.Insert(newIdx, draggedBorder);

            for (int i = 0; i < _container.Children.Count; i++)
            {
                var child = _container.Children[i] as Border;
                if (child == null) continue;
                string? id = child.Tag as string;
                var g = QuickGroupManager.GetAll().FirstOrDefault(x => x.Id == id);
                if (g != null)
                {
                    g.Order = i + 1;
                    QuickGroupManager.UpdateGroup(g);
                }
            }
            QuickGroupManager.Save();
        }

        #endregion

        #region Foreground Watcher & Long-term Window

        private void EnsureForegroundWatcher()
        {
            if (_foregroundWatcher != null) return;
            _foregroundWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _foregroundWatcher.Tick += (s, e) => RefreshActiveHighlight();
            _foregroundWatcher.Start();
            RefreshActiveHighlight();
        }

        private void EnsureLongTermWatcher()
        {
            if (_longTermWatcher != null) return;
            _longTermWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _longTermWatcher.Tick += (s, e) => ScanLongTermWindows();
            _longTermWatcher.Start();
            try { ScanLongTermWindows(); } catch { }
        }

        private void RefreshActiveHighlight()
        {
            if (_itemButtonRefs.Count == 0) return;
            IntPtr fg;
            try { fg = NativeMethods.GetForegroundWindow(); }
            catch { return; }

            foreach (var r in _itemButtonRefs)
            {
                if (r.Item == null || r.Button == null) continue;
                bool alive = r.Item.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(r.Item.WindowHandle);
                if (alive != r.IsAlive)
                {
                    r.IsAlive = alive;
                    r.Button.Foreground = (Brush)Application.Current.FindResource("BrushItemButtonText");
                }
                bool active = alive && fg != IntPtr.Zero && r.Item.WindowHandle == fg;
                if (active == r.IsActive) continue;
                r.IsActive = active;
                r.Button.BorderBrush = active ? ActiveHighlightBorder : Brushes.Transparent;
                r.Button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private void ScanLongTermWindows()
        {
            var pending = new List<QuickItem>();
            foreach (var grp in QuickGroupManager.GetAll())
            {
                if (grp?.Items == null) continue;
                foreach (var it in grp.Items)
                {
                    if (it == null || !it.IsLongTermWindow) continue;
                    if (it.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(it.WindowHandle)) continue;
                    if (string.IsNullOrEmpty(it.MatchExe) && string.IsNullOrEmpty(it.MatchClass) && string.IsNullOrEmpty(it.MatchTitle)) continue;
                    if (it.WindowHandle != IntPtr.Zero && !NativeMethods.IsWindow(it.WindowHandle))
                    {
                        _quickItemHandles.Remove(it.WindowHandle);
                        it.WindowHandle = IntPtr.Zero;
                    }
                    pending.Add(it);
                }
            }
            if (pending.Count == 0) return;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (pending.Count == 0) return false;
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                if (_quickItemHandles.Contains(hWnd)) return true;

                int len = NativeMethods.GetWindowTextLength(hWnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                string? className = null;
                try
                {
                    var csb = new StringBuilder(256);
                    NativeMethods.GetClassName(hWnd, csb, csb.Capacity);
                    className = csb.ToString();
                }
                catch { }

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var it = pending[i];
                    if (MatchLongTermWindow(it, title, className))
                    {
                        it.WindowHandle = hWnd;
                        _quickItemHandles.Add(hWnd);
                        try { ApplyTopmostIfPossible(it); } catch { }
                        try { ApplyTaskbarVisibility(it); } catch { }
                        pending.RemoveAt(i);
                        break;
                    }
                }
                return pending.Count > 0;
            }, IntPtr.Zero);

            try { RefreshActiveHighlight(); } catch { }
        }

        private bool MatchLongTermWindow(QuickItem item, string title, string? className)
        {
            bool any = false;
            if (!string.IsNullOrEmpty(item.MatchClass))
            {
                any = true;
                if (string.IsNullOrEmpty(className) || !string.Equals(className, item.MatchClass, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (!string.IsNullOrEmpty(item.MatchTitle))
            {
                any = true;
                if (string.IsNullOrEmpty(title)) return false;
                if (string.Equals(item.MatchTitleMode, "exact", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(title, item.MatchTitle, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else
                {
                    if (title.IndexOf(item.MatchTitle, StringComparison.OrdinalIgnoreCase) < 0)
                        return false;
                }
            }
            return any;
        }

        #endregion

        #region Tile Windows

        private async void TileGroupWindows(QuickGroup group)
        {
            try
            {
                bool hasUnopened = false;
                foreach (var item in group.Items)
                {
                    if (item.WindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(item.WindowHandle))
                    {
                        hasUnopened = true;
                        OpenQuickItem(item);
                    }
                }
                if (hasUnopened) await Task.Delay(1500);

                var windowHandles = new List<IntPtr>();
                foreach (var item in group.Items)
                {
                    if (item.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(item.WindowHandle))
                        windowHandles.Add(item.WindowHandle);
                }
                if (windowHandles.Count == 0)
                {
                    MessageBox.Show(Framework.Lang.T("该分组中没有可平铺的窗口"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 步骤1：先将所有需要平铺的窗口恢复为「正常状态」（既非最大化也非最小化），
                // 否则处于最大化状态的窗口 SetWindowPos 无法生效（会保持最大化），无法调整位置和尺寸。
                foreach (var hwnd in windowHandles)
                {
                    if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                }
                // 给窗口从最大化/最小化恢复到正常状态留出短暂时间，确保后续尺寸调整可靠生效。
                await Task.Delay(200);

                // 获取主屏幕工作区
                var workingArea = SystemParameters.WorkArea;
                int windowCount = windowHandles.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(windowCount));
                int rows = (int)Math.Ceiling((double)windowCount / cols);
                int windowWidth = (int)(workingArea.Width / cols);
                int windowHeight = (int)(workingArea.Height / rows);

                // 步骤2：逐个调整窗口位置和尺寸
                for (int i = 0; i < windowHandles.Count; i++)
                {
                    IntPtr hwnd = windowHandles[i];
                    int row = i / cols;
                    int col = i % cols;
                    int x = (int)workingArea.Left + col * windowWidth;
                    int y = (int)workingArea.Top + row * windowHeight;

                    // 双保险：若窗口仍处于最大化/最小化状态，再次恢复为正常状态
                    if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, windowWidth, windowHeight, SWP_NOACTIVATE);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("平铺窗口失败：{0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Dialogs

        private void AddItemToDefaultGroup(string presetType)
        {
            string groupId = QuickGroupManager.GetOrCreateDefaultGroup();
            ShowAddItemDialog(groupId, null, presetType);
        }

        private void ShowAddItemDialog(string groupId, UniformGrid? contentPanel, string? presetType)
        {
            if (presetType == "url")
            {
                // 使用 AddShortcutDialog（支持批量模式）
                var shortcutDialog = new AddShortcutDialog();
                shortcutDialog.Owner = OwnerWindow;
                if (shortcutDialog.ShowDialog() == true)
                {
                    if (shortcutDialog.BatchResults != null && shortcutDialog.BatchResults.Count > 0)
                    {
                        foreach (var result in shortcutDialog.BatchResults)
                        {
                            var newItem = new QuickItem
                            {
                                Name = string.IsNullOrEmpty(result.Name)
                                    ? (string.IsNullOrEmpty(result.Url) ? Framework.Lang.T("新快捷项") : result.Url)
                                    : result.Name,
                                Icon = result.Icon,
                                Type = "url",
                                Target = result.Url,
                                UseAppMode = result.IsAppMode,
                                BrowserId = result.BrowserId,
                                ProfileId = result.ProfileId
                            };
                            QuickGroupManager.AddItem(groupId, newItem);
                        }
                        QuickGroupManager.Save();
                        RefreshGroupView();
                    }
                    else if (shortcutDialog.SingleResult != null)
                    {
                        var result = shortcutDialog.SingleResult;
                        var newItem = new QuickItem
                        {
                            Name = string.IsNullOrEmpty(result.Name)
                                ? (string.IsNullOrEmpty(result.Url) ? Framework.Lang.T("新快捷项") : result.Url)
                                : result.Name,
                            Icon = result.Icon,
                            Type = "url",
                            Target = result.Url,
                            UseAppMode = result.IsAppMode,
                            BrowserId = result.BrowserId,
                            ProfileId = result.ProfileId,
                            BackgroundColor = result.BackgroundColor
                        };
                        QuickGroupManager.AddItem(groupId, newItem);
                        QuickGroupManager.Save();
                        AddNewItemToUI(newItem, groupId, contentPanel);
                    }
                }
                return;
            }

            // 程序类型：Tab式对话框
            var dialog = new Window
            {
                Title = Framework.Lang.T("添加程序"),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Owner = OwnerWindow
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog);

            var app = (App)Application.Current;
            var rootPanel = new StackPanel { Margin = new Thickness(20) };

            // 标题
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("添加程序"),
                FontWeight = FontWeights.Bold,
                FontSize = (double)Application.Current.FindResource("FontSizeSubtitle"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // === Tab 按钮栏 ===
            var tabBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };

            // 应用市场入口（右侧）
            var marketBtn = new Button
            {
                Content = Framework.Lang.T("🛒 应用市场"),
                Padding = new Thickness(12, 6, 12, 6),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle")
            };
            marketBtn.Click += (s, ev2) => MarketWindow.Open("/app-market", "🛒 应用市场");
            DockPanel.SetDock(marketBtn, Dock.Right);
            tabBar.Children.Add(marketBtn);

            // 插件市场入口（右侧，紧挨应用市场）
            var pluginMarketBtn = new Button
            {
                Content = Framework.Lang.T("🧩 插件市场"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle")
            };
            pluginMarketBtn.Click += (s, ev2) => MarketWindow.Open("/plugin-market", "🧩 插件市场");
            DockPanel.SetDock(pluginMarketBtn, Dock.Right);
            tabBar.Children.Add(pluginMarketBtn);

            var tabStack = new StackPanel { Orientation = Orientation.Horizontal };
            var tabManual = new Button { Content = Framework.Lang.T("自行输入"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            var tabList = new Button { Content = Framework.Lang.T("从列表"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            var tabBrowser = new Button { Content = Framework.Lang.T("浏览器"), Padding = new Thickness(12, 6, 12, 6) };
            tabStack.Children.Add(tabManual);
            tabStack.Children.Add(tabList);
            tabStack.Children.Add(tabBrowser);
            tabBar.Children.Add(tabStack);
            rootPanel.Children.Add(tabBar);

            // === Tab 内容容器 ===
            var tabContentArea = new Border
            {
                MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 12),
                Background = (Brush)Application.Current.FindResource("BrushInputBG"),
                BorderBrush = (Brush)Application.Current.FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12)
            };
            rootPanel.Children.Add(tabContentArea);

            // Tab 1 内容：自行输入
            var manualPanel = new StackPanel();
            var browseBtn = new Button
            {
                Content = Framework.Lang.T("浏览选择 exe..."),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6)
            };
            manualPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("点击下方按钮选择可执行文件，或在下方直接输入路径"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
            manualPanel.Children.Add(browseBtn);

            // Tab 2 内容：从列表选择
            var listPanel = new StackPanel();
            if (app.ProgramRegistry.Programs.Count == 0)
            {
                listPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无已注册程序，请先在设置→程序管理中添加"),
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                    FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                var progListBox = new ListBox
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    MaxHeight = 150,
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    ItemContainerStyle = (Style)Application.Current.FindResource("GlobalListBoxItemStyle")
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(progListBox, ScrollBarVisibility.Disabled);
                foreach (var prog in app.ProgramRegistry.Programs)
                {
                    var itemPanel = new StackPanel();
                    itemPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = prog.Name,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        FontSize = (double)Application.Current.FindResource("FontSizeNormal")
                    });
                    itemPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = prog.ExePath,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                        FontSize = (double)Application.Current.FindResource("FontSizeTiny"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    progListBox.Items.Add(itemPanel);
                }
                listPanel.Children.Add(progListBox);
            }

            // Tab 3 内容：浏览器选择
            var browserPanel = new StackPanel();
            ComboBox? profileCombo = null;
            if (app.ProgramRegistry.Browsers.Count == 0)
            {
                browserPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无已配置浏览器，请先在设置→程序管理中添加"),
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                    FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                var browserListBox = new ListBox
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    MaxHeight = 150,
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    ItemContainerStyle = (Style)Application.Current.FindResource("GlobalListBoxItemStyle")
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(browserListBox, ScrollBarVisibility.Disabled);
                foreach (var browser in app.ProgramRegistry.Browsers)
                {
                    var itemPanel = new StackPanel();
                    itemPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = browser.Name,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        FontSize = (double)Application.Current.FindResource("FontSizeNormal")
                    });
                    itemPanel.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = browser.ExePath,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMuted"),
                        FontSize = (double)Application.Current.FindResource("FontSizeTiny"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    browserListBox.Items.Add(itemPanel);
                }
                browserPanel.Children.Add(browserListBox);

                // Profile 选择（默认隐藏，选中有 Profile 的浏览器后才显示）
                profileCombo = new ComboBox
                {
                    Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle"),
                    Margin = new Thickness(0, 8, 0, 0),
                    Visibility = Visibility.Collapsed
                };
                browserPanel.Children.Add(profileCombo);
            }

            // 默认显示 Tab 1
            tabContentArea.Child = manualPanel;

            // === 通用区域 ===
            // 名称
            var nameDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var nameLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("名称："), Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(nameLabel, Dock.Left);
            nameDock.Children.Add(nameLabel);
            var nameBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            nameDock.Children.Add(nameBox);
            rootPanel.Children.Add(nameDock);

            // 图标
            var iconDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var iconLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("图标："), Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(iconLabel, Dock.Left);
            iconDock.Children.Add(iconLabel);
            var iconBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle"), Width = 80, MaxLength = 4 };
            iconDock.Children.Add(iconBox);
            var iconPreview = new Emoji.Wpf.TextBlock { Text = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            iconBox.TextChanged += (s, ev2) => iconPreview.Text = iconBox.Text.Trim();
            iconDock.Children.Add(iconPreview);
            rootPanel.Children.Add(iconDock);

            // 路径
            var targetDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var targetLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("路径："), Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(targetLabel, Dock.Left);
            targetDock.Children.Add(targetLabel);
            var targetBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            targetDock.Children.Add(targetBox);
            rootPanel.Children.Add(targetDock);

            // 启动命令行
            var argsDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var argsLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("命令行："), Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(argsLabel, Dock.Left);
            argsDock.Children.Add(argsLabel);
            var argsBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            argsDock.Children.Add(argsBox);
            rootPanel.Children.Add(argsDock);

            // === 背景颜色选择 ===
            var colorLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("按钮颜色："),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                FontSize = (double)Application.Current.FindResource("FontSizeNormal"),
                Margin = new Thickness(0, 4, 0, 8)
            };
            rootPanel.Children.Add(colorLabel);

            var presetColors = new[]
            {
                "#3498DB", "#2ECC71", "#E74C3C", "#9B59B6",
                "#F39C12", "#1ABC9C", "#E67E22", "#34495E",
                "#2980B9", "#27AE60", "#C0392B", "#8E44AD"
            };
            string selectedColor = "#3498DB";

            var colorPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            Border? selectedColorBorder = null;

            foreach (var color in presetColors)
            {
                var swatch = new Border
                {
                    Width = 32, Height = 32,
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!,
                    BorderBrush = color == selectedColor ? (Brush)Application.Current.FindResource("BrushAccent") : Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = color
                };
                if (color == selectedColor) selectedColorBorder = swatch;

                swatch.MouseLeftButtonUp += (s, ev2) =>
                {
                    selectedColor = (string)swatch.Tag!;
                    // 清除所有边框
                    foreach (UIElement child in colorPanel.Children)
                    {
                        if (child is Border b) b.BorderBrush = Brushes.Transparent;
                    }
                    swatch.BorderBrush = (Brush)Application.Current.FindResource("BrushAccent");
                    selectedColorBorder = swatch;
                };
                colorPanel.Children.Add(swatch);
            }
            rootPanel.Children.Add(colorPanel);

            // === Tab 切换逻辑 ===
            Action<int> switchTab = (int tabIndex) =>
            {
                tabManual.Style = tabIndex == 0 ? (Style)Application.Current.FindResource("PrimaryButtonStyle") : (Style)Application.Current.FindResource("GhostButtonStyle");
                tabList.Style = tabIndex == 1 ? (Style)Application.Current.FindResource("PrimaryButtonStyle") : (Style)Application.Current.FindResource("GhostButtonStyle");
                tabBrowser.Style = tabIndex == 2 ? (Style)Application.Current.FindResource("PrimaryButtonStyle") : (Style)Application.Current.FindResource("GhostButtonStyle");

                switch (tabIndex)
                {
                    case 0: tabContentArea.Child = manualPanel; break;
                    case 1: tabContentArea.Child = listPanel; break;
                    case 2: tabContentArea.Child = browserPanel; break;
                }
            };
            // 默认选中第一个
            switchTab(0);

            tabManual.Click += (s, ev2) => switchTab(0);
            tabList.Click += (s, ev2) => switchTab(1);
            tabBrowser.Click += (s, ev2) => switchTab(2);

            // === 浏览按钮事件 ===
            browseBtn.Click += (s, ev2) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = Framework.Lang.T("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
                    Title = Framework.Lang.T("选择程序")
                };
                if (dlg.ShowDialog() == true)
                {
                    targetBox.Text = dlg.FileName;
                    if (string.IsNullOrEmpty(nameBox.Text))
                        nameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            };

            // === 列表选择事件 ===
            if (app.ProgramRegistry.Programs.Count > 0)
            {
                var progListBox = listPanel.Children[0] as ListBox;
                if (progListBox != null)
                {
                    progListBox.SelectionChanged += (s, ev2) =>
                    {
                        if (progListBox.SelectedIndex < 0) return;
                        var prog = app.ProgramRegistry.Programs[progListBox.SelectedIndex];
                        nameBox.Text = prog.Name;
                        targetBox.Text = prog.ExePath;
                    };
                }
            }

            // === 浏览器选择事件 ===
            string? selectedBrowserId = null;
            string? selectedProfileId = null;
            if (app.ProgramRegistry.Browsers.Count > 0)
            {
                var browserListBox = browserPanel.Children[0] as ListBox;
                if (browserListBox != null)
                {
                    browserListBox.SelectionChanged += (s, ev2) =>
                    {
                        if (browserListBox.SelectedIndex < 0) return;
                        var browser = app.ProgramRegistry.Browsers[browserListBox.SelectedIndex];
                        nameBox.Text = browser.Name;
                        targetBox.Text = browser.ExePath;
                        selectedBrowserId = browser.Id;
                        selectedProfileId = null;

                        // 显示 Profile
                        if (profileCombo != null)
                        {
                            profileCombo.Items.Clear();
                            if (browser.Profiles.Count > 0)
                            {
                                profileCombo.Items.Add(Framework.Lang.T("无"));
                                foreach (var p in browser.Profiles)
                                    profileCombo.Items.Add(p.Name);
                                profileCombo.SelectedIndex = 0;
                                profileCombo.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                profileCombo.Visibility = Visibility.Collapsed;
                            }
                        }
                    };
                }
                if (profileCombo != null)
                {
                    profileCombo.SelectionChanged += (s, ev2) =>
                    {
                        if (profileCombo.SelectedIndex <= 0) { selectedProfileId = null; return; }
                        var browserListBox2 = browserPanel.Children[0] as ListBox;
                        if (browserListBox2?.SelectedIndex >= 0)
                        {
                            var browser = app.ProgramRegistry.Browsers[browserListBox2.SelectedIndex];
                            int pIdx = profileCombo.SelectedIndex - 1;
                            if (pIdx >= 0 && pIdx < browser.Profiles.Count)
                                selectedProfileId = browser.Profiles[pIdx].Id;
                        }
                    };
                }
            }

            // === 按钮 ===
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, ev2) =>
            {
                string name = nameBox.Text.Trim();
                string target = targetBox.Text.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) { MessageBox.Show(Framework.Lang.T("请填写名称和路径")); return; }
                var newItem = new QuickItem
                {
                    Name = name,
                    Icon = string.IsNullOrEmpty(iconBox.Text.Trim()) ? null : iconBox.Text.Trim(),
                    Type = "program",
                    Target = target,
                    UseAppMode = false,
                    LaunchArgs = argsBox.Text.Trim(),
                    BrowserId = selectedBrowserId,
                    ProfileId = selectedProfileId,
                    BackgroundColor = selectedColor
                };
                QuickGroupManager.AddItem(groupId, newItem);
                QuickGroupManager.Save();
                AddNewItemToUI(newItem, groupId, contentPanel);
                dialog.Close();
            };
            cancelBtn.Click += (s, ev2) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            rootPanel.Children.Add(btnPanel);

            dialog.Content = rootPanel;
            dialog.ShowDialog();
        }

        private void ShowEditItemDialog(QuickItem item, string groupId, Button itemBtn)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("编辑项目"), Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Owner = OwnerWindow
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog);

            var rootPanel = new StackPanel { Margin = new Thickness(20) };

            // 名称
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("名称"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new EmojiTextBox { Text = item.Name };
            rootPanel.Children.Add(nameBox);

            // 图标（支持 emoji 字符 或 文件路径；程序类项默认预填当前图标来源路径，可编辑）
            string iconInitial = !string.IsNullOrEmpty(item.Icon) ? item.Icon : (item.Type == "program" ? item.Target : "");
            var iconDock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var iconLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("图标："), Width = 50, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(iconLabel, Dock.Left);
            iconDock.Children.Add(iconLabel);
            var iconPreviewHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Content = BuildIconPreview(iconInitial) };
            DockPanel.SetDock(iconPreviewHost, Dock.Right);
            iconDock.Children.Add(iconPreviewHost);
            var iconBox = new TextBox { Text = iconInitial, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            iconBox.TextChanged += (s, ev) => iconPreviewHost.Content = BuildIconPreview(iconBox.Text.Trim());
            iconDock.Children.Add(iconBox);
            rootPanel.Children.Add(iconDock);

            // 目标
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("目标"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 12, 0, 6)
            });
            var targetBox = new TextBox
            {
                Text = item.Target,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            rootPanel.Children.Add(targetBox);

            // App 模式开关
            var appModePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var appModeSwitch = new CheckBox
            {
                IsChecked = item.UseAppMode,
                Style = (Style)Application.Current.FindResource("RaycastSwitchStyle"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var appModeLabel = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("App 模式"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            appModePanel.Children.Add(appModeSwitch);
            appModePanel.Children.Add(appModeLabel);
            rootPanel.Children.Add(appModePanel);

            // 按钮颜色
            rootPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("按钮颜色"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 14, 0, 6)
            });
            var colorPanel = new WrapPanel();
            string selectedColor = item.BackgroundColor ?? "#3498DB";
            var accentBrush = (Brush)Application.Current.FindResource("BrushAccent");
            var presetColors = new[]
            {
                "#3498DB", "#2ECC71", "#E74C3C", "#9B59B6",
                "#F39C12", "#1ABC9C", "#E67E22", "#34495E",
                "#2980B9", "#27AE60", "#C0392B", "#8E44AD"
            };
            foreach (var color in presetColors)
            {
                var swatch = new Border
                {
                    Width = 28, Height = 28,
                    Margin = new Thickness(0, 0, 8, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!,
                    BorderBrush = color.Equals(selectedColor, StringComparison.OrdinalIgnoreCase) ? accentBrush : Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                swatch.MouseLeftButtonUp += (s, ev) =>
                {
                    selectedColor = (string)swatch.Tag!;
                    foreach (UIElement child in colorPanel.Children)
                        if (child is Border b) b.BorderBrush = Brushes.Transparent;
                    swatch.BorderBrush = accentBrush;
                };
                colorPanel.Children.Add(swatch);
            }
            rootPanel.Children.Add(colorPanel);

            // 按钮
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"),
                Style = (Style)Application.Current.FindResource("StandardButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var okBtn = new Button
            {
                Content = Framework.Lang.T("保存"),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8)
            };
            okBtn.Click += (s, ev) =>
            {
                item.Name = nameBox.Text.Trim();
                item.Icon = string.IsNullOrEmpty(iconBox.Text.Trim()) ? null : iconBox.Text.Trim();
                item.Target = targetBox.Text.Trim();
                item.UseAppMode = appModeSwitch.IsChecked == true;
                item.BackgroundColor = selectedColor;
                QuickGroupManager.UpdateGroup(QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == groupId) ?? new QuickGroup());
                QuickGroupManager.Save();
                // 更新按钮背景色
                try { itemBtn.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(item.BackgroundColor)!; }
                catch { itemBtn.Background = Brushes.SteelBlue; }
                // 更新按钮显示（图标 + 名称，支持 emoji/路径图标）
                itemBtn.Content = BuildItemContent(item);
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            rootPanel.Children.Add(btnPanel);

            dialog.Content = rootPanel;
            dialog.ShowDialog();
        }

        private void ShowPickWindowDialog(string? targetGroupId)
        {
            // 采集候选窗口
            var candidates = new List<(IntPtr Hwnd, string Title, string? ExePath, string? ProcessName, string? ClassName)>();
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                int len = NativeMethods.GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                string? exe = null;
                string? procName = null;
                try
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0)
                    {
                        var p = Process.GetProcessById((int)pid);
                        procName = p.ProcessName;
                        try { exe = p.MainModule?.FileName; } catch { }
                    }
                }
                catch { }

                string? className = null;
                try
                {
                    var csb = new StringBuilder(256);
                    NativeMethods.GetClassName(hWnd, csb, csb.Capacity);
                    className = csb.ToString();
                }
                catch { }

                candidates.Add((hWnd, title, exe, procName, className));
                return true;
            }, IntPtr.Zero);

            if (candidates.Count == 0) { MessageBox.Show(Framework.Lang.T("未检测到可选的窗口")); return; }

            var dialog = new SelectWindowDialog
            {
                Owner = OwnerWindow
            };
            dialog.PopulateCandidates(candidates);

            if (dialog.ShowDialog() == true)
            {
                string groupId = string.IsNullOrEmpty(targetGroupId)
                    ? QuickGroupManager.GetOrCreateDefaultGroup()
                    : targetGroupId;

                foreach (var info in dialog.SelectedWindows)
                {
                    if (string.IsNullOrEmpty(info.ExePath)) continue;
                    var newItem = new QuickItem
                    {
                        Name = string.IsNullOrEmpty(info.ProcessName) ? info.Title : info.ProcessName,
                        Type = "program",
                        Target = info.ExePath,
                        UseAppMode = false,
                        WindowHandle = info.Hwnd,
                        Source = "window",
                        ShowInTaskbar = true
                    };
                    QuickGroupManager.AddItem(groupId, newItem);
                    _quickItemHandles.Add(info.Hwnd);
                }
                QuickGroupManager.Save();
                LoadGroups();
            }
        }

        #endregion

        #region Helpers

        /// <summary>大图标模式内容：桌面快捷方式风格，纯大图标在上 + 名称在下（居中、单行超长省略），无按钮背景。</summary>
        private object BuildLargeIconContent(QuickItem item)
        {
            BitmapSource? imgIcon = null;
            string? emojiIcon = null;
            if (!string.IsNullOrEmpty(item.Icon))
            {
                if (FaviconService.IsPathIcon(item.Icon))
                    imgIcon = FaviconService.LoadFileIcon(item.Icon);
                else
                    emojiIcon = item.Icon;
            }
            // 程序/窗口类项未设图标时，自动从 Target 提取应用图标
            if (imgIcon == null && emojiIcon == null && item.Type == "program")
                imgIcon = FaviconService.LoadFileIcon(item.Target);

            var panel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };

            // 图标尺寸随列宽动态计算：大图标占满单元格宽度（预留左右内边距，上限 40x40），高度=宽度保持 1:1，
            // 避免固定尺寸与列数不匹配导致图标变形/裁边
            FrameworkElement iconEl;
            if (emojiIcon != null)
                iconEl = new Emoji.Wpf.TextBlock { Text = emojiIcon, FontSize = 34 };
            else if (imgIcon != null)
            {
                // 单个 Rectangle 自定尺寸（无子元素测量干扰，不会被图片原始像素撑大），
                // DrawingBrush 在 1:1 区域内等比居中绘制图标
                if (imgIcon.CanFreeze) imgIcon.Freeze();
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Fill = new System.Windows.Media.DrawingBrush
                    {
                        Drawing = new System.Windows.Media.ImageDrawing(imgIcon, new Rect(0, 0, 1, 1)),
                        Stretch = System.Windows.Media.Stretch.Uniform
                    },
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                // 布局就绪后：宽 = min(单元格宽 - 边距, 40)，高 = 宽（1:1）
                rect.Loaded += (s, e) =>
                {
                    rect.SetBinding(FrameworkElement.WidthProperty,
                        new System.Windows.Data.Binding("ActualWidth") { Source = panel, Converter = new SubtractConverter(6) });
                    rect.SetBinding(FrameworkElement.HeightProperty,
                        new System.Windows.Data.Binding("ActualWidth") { Source = rect });
                };
                iconEl = rect;
            }
            else
                iconEl = new Emoji.Wpf.TextBlock { Text = "🔗", FontSize = 34 };
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            iconEl.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(iconEl);

            panel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = item.Name,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            return panel;
        }

        /// <summary>构建按钮内容（图标 + 名称）。图标优先级：emoji 字符 > 路径图标 > 程序项自动从 Target 提取。</summary>
        private object BuildItemContent(QuickItem item, HorizontalAlignment align = HorizontalAlignment.Center)
        {
            BitmapSource? imgIcon = null;
            string? emojiIcon = null;
            if (!string.IsNullOrEmpty(item.Icon))
            {
                if (FaviconService.IsPathIcon(item.Icon))
                    imgIcon = FaviconService.LoadFileIcon(item.Icon);   // 路径图标
                else
                    emojiIcon = item.Icon;              // emoji 图标
            }
            // 程序/窗口类项未设图标时，自动从 Target 提取应用图标
            if (imgIcon == null && emojiIcon == null && item.Type == "program")
                imgIcon = FaviconService.LoadFileIcon(item.Target);

            var nameTb = new Emoji.Wpf.TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center };

            if (emojiIcon != null)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align };
                panel.Children.Add(new Emoji.Wpf.TextBlock { Text = emojiIcon, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                panel.Children.Add(nameTb);
                return panel;
            }
            if (imgIcon != null)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align };
                panel.Children.Add(new Image { Source = imgIcon, Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                panel.Children.Add(nameTb);
                return panel;
            }
            return nameTb;
        }

        /// <summary>减法转换器：值 - offset，上限 40（大图标宽度 = min(单元格宽 - 边距, 40)）</summary>
        private sealed class SubtractConverter : System.Windows.Data.IValueConverter
        {
            private readonly double _offset;
            public SubtractConverter(double offset) { _offset = offset; }
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => value is double d ? Math.Min(40.0, Math.Max(0, d - _offset)) : 0.0;
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => throw new NotSupportedException();
        }

        /// <summary>构建图标预览元素：路径显示提取的图标，其余显示 emoji 文本。</summary>
        private static FrameworkElement BuildIconPreview(string text)
        {
            if (FaviconService.IsPathIcon(text))
            {
                var src = FaviconService.LoadFileIcon(text);
                if (src != null)
                    return new Image { Source = src, Width = 20, Height = 20, Stretch = System.Windows.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                return new TextBlock { Text = "?", FontSize = 14, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            }
            return new Emoji.Wpf.TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        }

        private class ItemButtonRef
        {
            public QuickItem Item = null!;
            public Button Button = null!;
            public Brush BaseBackground = Brushes.Transparent;
            public bool IsActive;
            public bool IsAlive;
        }

        #endregion
    }
}
