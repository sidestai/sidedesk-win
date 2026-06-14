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
using System.Windows.Media;
using System.Windows.Threading;
using SideDesk.Models;
using SideDesk.Windows;

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

        private static readonly Brush ActiveHighlightBorder =
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
        private static readonly Brush InactiveForeground = Brushes.Black;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>请求主窗口作为对话框的 Owner</summary>
        public Window? OwnerWindow { get; set; }

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
            QuickGroupManager.MigrateFromLegacy();
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
                Title = "添加分组",
                Width = 350, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                Owner = OwnerWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 15, 10, 5) };
            namePanel.Children.Add(new TextBlock { Text = "名称:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 50 });
            var nameBox = new TextBox { Width = 220, Text = "新分组" };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var colPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 10, 5) };
            colPanel.Children.Add(new TextBlock { Text = "列数:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 50 });
            var colCombo = new ComboBox { Width = 100 };
            colCombo.Items.Add("1 列"); colCombo.Items.Add("2 列");
            colCombo.Items.Add("3 列"); colCombo.Items.Add("4 列");
            colCombo.SelectedIndex = 0;
            colPanel.Children.Add(colCombo);
            Grid.SetRow(colPanel, 1);
            grid.Children.Add(colPanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show("请输入名称"); return; }
                var group = new QuickGroup { Name = name, Columns = colCombo.SelectedIndex + 1 };
                QuickGroupManager.AddGroup(group);
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

        /// <summary>弹出「+」菜单（添加分组/程序/网址/窗口）</summary>
        public void ShowAddMenu(Button target)
        {
            var menu = new ContextMenu();

            var addGroupItem = new MenuItem { Header = "添加分组" };
            addGroupItem.Click += (s, ev) => ShowAddGroupDialog();
            menu.Items.Add(addGroupItem);
            menu.Items.Add(new Separator());

            var addProgItem = new MenuItem { Header = "添加程序" };
            addProgItem.Click += (s, ev) => AddItemToDefaultGroup("program");
            menu.Items.Add(addProgItem);

            var addUrlItem = new MenuItem { Header = "添加网址" };
            addUrlItem.Click += (s, ev) => AddItemToDefaultGroup("url");
            menu.Items.Add(addUrlItem);

            var addWinItem = new MenuItem { Header = "添加窗口" };
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

            var addUrlItem = new MenuItem { Header = "网页" };
            addUrlItem.Click += (s, ev) => AddItemToDefaultGroup("url");
            menu.Items.Add(addUrlItem);

            var addProgItem = new MenuItem { Header = "程序" };
            addProgItem.Click += (s, ev) => AddItemToDefaultGroup("program");
            menu.Items.Add(addProgItem);

            var addWinItem = new MenuItem { Header = "窗口" };
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

            var addUrlItem = new MenuItem { Header = "网页" };
            addUrlItem.Click += (s, ev) => ShowAddItemDialog(groupId, null, "url");
            menu.Items.Add(addUrlItem);

            var addProgItem = new MenuItem { Header = "程序" };
            addProgItem.Click += (s, ev) => ShowAddItemDialog(groupId, null, "program");
            menu.Items.Add(addProgItem);

            var addWinItem = new MenuItem { Header = "窗口" };
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
                Title = "新建快捷方式",
                Width = 340, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                Owner = OwnerWindow
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 15, 10, 5) };
            namePanel.Children.Add(new TextBlock { Text = "分组名:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 60 });
            var nameBox = new TextBox { Width = 220 };
            namePanel.Children.Add(nameBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 15, 10, 10) };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show("请输入分组名称"); return; }
                var group = new QuickGroup { Name = name, Columns = 2 };
                QuickGroupManager.AddGroup(group);
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

        /// <summary>处理文件/URL 拖入</summary>
        public void HandleDrop(DragEventArgs e)
        {
            if (e.Data.GetDataPresent("QuickGroupId")) return; // 内部拖拽不处理

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string groupId = QuickGroupManager.GetOrCreateDefaultGroup();
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
                LoadGroups();
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                string text = (string)e.Data.GetData(DataFormats.UnicodeText);
                if (text.StartsWith("http://") || text.StartsWith("https://"))
                {
                    string groupId = QuickGroupManager.GetOrCreateDefaultGroup();
                    var item = new QuickItem
                    {
                        Name = text.Length > 30 ? text.Substring(0, 30) + "..." : text,
                        Type = "url",
                        Target = text,
                        UseAppMode = true
                    };
                    QuickGroupManager.AddItem(groupId, item);
                    QuickGroupManager.Save();
                    LoadGroups();
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

        #endregion

        #region Group Panel Building

        private Border BuildGroupPanel(QuickGroup group)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(2),
                Tag = group.Id
            };

            var outerPanel = new StackPanel();

            // 标题栏
            var titleBar = new DockPanel { Margin = new Thickness(5, 3, 5, 3) };
            titleBar.Tag = group.Id;

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

            // ▼ 菜单按钮
            var toggleBtn = new Button
            {
                Content = "▼", Width = 22, Height = 22,
                Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderThickness = new Thickness(0), FontSize = 10, Padding = new Thickness(0)
            };
            DockPanel.SetDock(toggleBtn, Dock.Right);
            titleBar.Children.Add(toggleBtn);



            // 分组名
            var titleText = new TextBlock
            {
                Text = group.Name, Foreground = Brushes.White, FontSize = 12,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand
            };
            titleBar.Children.Add(titleText);
            outerPanel.Children.Add(titleBar);

            // 内容区
            var contentPanel = new UniformGrid
            {
                Columns = group.Columns,
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

            foreach (var item in group.Items)
            {
                contentPanel.Children.Add(BuildItemButton(item, group.Id));
            }
            outerPanel.Children.Add(contentPanel);
            border.Child = outerPanel;

            // ▼ 菜单
            toggleBtn.Click += (s, ev) =>
            {
                var popup = new ContextMenu();

                // 添加子菜单
                var addSubMenu = new MenuItem { Header = "添加" };
                var miProg = new MenuItem { Header = "添加程序" };
                miProg.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "program");
                addSubMenu.Items.Add(miProg);
                var miUrl = new MenuItem { Header = "添加网址" };
                miUrl.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "url");
                addSubMenu.Items.Add(miUrl);
                var miWin = new MenuItem { Header = "添加窗口" };
                miWin.Click += (s2, ev2) => ShowPickWindowDialog(group.Id);
                addSubMenu.Items.Add(miWin);
                popup.Items.Add(addSubMenu);

                // 添加模块子菜单
                var moduleSubMenu = new MenuItem { Header = "添加模块" };
                var moduleTypes = new (string Header, PanelType Type)[]
                {
                    ("便签", PanelType.StickyNotes),
                    ("剪贴板收藏", PanelType.Clipboard),
                    ("系统信息", PanelType.SystemMonitor),
                    ("使用统计", PanelType.UsageStats),
                    ("AI 对话", PanelType.AIChat),
                    ("文件区", PanelType.FileArea),
                    ("数据看板", PanelType.DataDashboard),
                    ("计时器", PanelType.Timer)
                };
                foreach (var (header, type) in moduleTypes)
                {
                    var mi = new MenuItem { Header = header };
                    var panelType = type;
                    mi.Click += (s2, ev2) => AddModuleToGroup(group, panelType);
                    moduleSubMenu.Items.Add(mi);
                }
                popup.Items.Add(moduleSubMenu);
                popup.Items.Add(new Separator());

                var expandItem = new MenuItem { Header = group.IsExpanded ? "收起" : "展开" };
                expandItem.Click += (s2, ev2) =>
                {
                    group.IsExpanded = !group.IsExpanded;
                    contentPanel.Visibility = group.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                    QuickGroupManager.UpdateGroup(group);
                };
                popup.Items.Add(expandItem);
                popup.Items.Add(new Separator());

                var renameItem = new MenuItem { Header = "重命名" };
                renameItem.Click += (s2, ev2) =>
                {
                    var dlg = new Window
                    {
                        Title = "重命名分组", Width = 350,
                        SizeToContent = SizeToContent.Height,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        ResizeMode = ResizeMode.NoResize,
                        Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain")
                    };
                    dlg.SourceInitialized += (s3, ev3) => App.UpdateWindowDwmTheme(dlg);
                    var sp = new StackPanel { Margin = new Thickness(20) };
                    sp.Children.Add(new TextBlock
                    {
                        Text = "重命名分组",
                        FontWeight = FontWeights.Bold,
                        FontSize = (double)Application.Current.FindResource("FontSizeSubtitle"),
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                    var tb = new TextBox { Text = group.Name, Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
                    sp.Children.Add(tb);
                    var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
                    var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
                    var cancel = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
                    ok.Click += (s3, ev3) => { group.Name = tb.Text.Trim(); titleText.Text = group.Name; QuickGroupManager.UpdateGroup(group); QuickGroupManager.Save(); dlg.Close(); };
                    cancel.Click += (s3, ev3) => dlg.Close();
                    btnPanel.Children.Add(ok);
                    btnPanel.Children.Add(cancel);
                    sp.Children.Add(btnPanel);
                    dlg.Content = sp;
                    dlg.ShowDialog();
                };
                popup.Items.Add(renameItem);

                var colItem = new MenuItem { Header = "修改列数" };
                for (int c = 1; c <= 4; c++)
                {
                    int col = c;
                    var mi = new MenuItem { Header = $"{col} 列", IsChecked = (group.Columns == col) };
                    mi.Click += (s2, ev2) => { group.Columns = col; contentPanel.Columns = col; QuickGroupManager.UpdateGroup(group); QuickGroupManager.Save(); };
                    colItem.Items.Add(mi);
                }
                popup.Items.Add(colItem);
                popup.Items.Add(new Separator());

                var tileItem = new MenuItem { Header = "平铺窗口" };
                tileItem.Click += (s2, ev2) => TileGroupWindows(group);
                popup.Items.Add(tileItem);
                popup.Items.Add(new Separator());

                var deleteItem = new MenuItem { Header = "删除分组" };
                deleteItem.Click += (s2, ev2) =>
                {
                    bool hasModules = group.ChildPanels != null && group.ChildPanels.Count > 0;
                    string message;
                    if (hasModules)
                    {
                        int count = group.ChildPanels!.Count;
                        message = $"分组 \"{group.Name}\" 包含 {count} 个模块，删除后模块也将丢失。确定继续？";
                    }
                    else
                    {
                        message = $"确定删除分组 \"{group.Name}\"？";
                    }

                    if (MessageBox.Show(message, "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
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

        private FrameworkElement BuildItemButton(QuickItem item, string groupId)
        {
            var panel = new DockPanel { Margin = new Thickness(2), Tag = item.Id };

            Brush bgBrush;
            try { bgBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(item.BackgroundColor)!; }
            catch { bgBrush = Brushes.SteelBlue; }

            var btn = new Button
            {
                Content = item.Name,
                Padding = new Thickness(6, 4, 6, 4),
                Background = bgBrush,
                Foreground = item.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(item.WindowHandle)
                    ? Brushes.White : InactiveForeground,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 11,
                AllowDrop = true
            };
            
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
            
            btn.Click += (s, ev) => OpenQuickItem(item);
            panel.Children.Add(btn);

            _itemButtonRefs.Add(new ItemButtonRef { Item = item, Button = btn, BaseBackground = bgBrush });

            // 右键菜单
            var menu = new ContextMenu();
            var editItem = new MenuItem { Header = "编辑" };
            editItem.Click += (s, ev) => ShowEditItemDialog(item, groupId, btn);
            menu.Items.Add(editItem);

            var topmostItem = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = item.AlwaysOnTop };
            topmostItem.Click += (s, ev) => { item.AlwaysOnTop = topmostItem.IsChecked; ApplyTopmostIfPossible(item); QuickGroupManager.Save(); };
            menu.Items.Add(topmostItem);

            var taskbarItem = new MenuItem { Header = "在任务栏显示", IsCheckable = true, IsChecked = item.ShowInTaskbar };
            taskbarItem.Click += (s, ev) => { item.ShowInTaskbar = taskbarItem.IsChecked; ApplyTaskbarVisibility(item); QuickGroupManager.Save(); };
            menu.Items.Add(taskbarItem);

            var closeItem = new MenuItem { Header = "关闭窗口" };
            closeItem.Click += (s, ev) => CloseItemWindow(item);
            menu.Items.Add(closeItem);
            menu.Items.Add(new Separator());

            var delItem = new MenuItem { Header = "删除" };
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
                MessageBox.Show($"打开失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    r.Button.Foreground = alive ? Brushes.White : InactiveForeground;
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
                    MessageBox.Show("该分组中没有可平铺的窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 获取主屏幕工作区
                var workingArea = SystemParameters.WorkArea;
                int windowCount = windowHandles.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(windowCount));
                int rows = (int)Math.Ceiling((double)windowCount / cols);
                int windowWidth = (int)(workingArea.Width / cols);
                int windowHeight = (int)(workingArea.Height / rows);

                for (int i = 0; i < windowHandles.Count; i++)
                {
                    IntPtr hwnd = windowHandles[i];
                    int row = i / cols;
                    int col = i % cols;
                    int x = (int)workingArea.Left + col * windowWidth;
                    int y = (int)workingArea.Top + row * windowHeight;

                    if (NativeMethods.IsIconic(hwnd))
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, windowWidth, windowHeight, SWP_NOACTIVATE);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"平铺窗口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                                    ? (string.IsNullOrEmpty(result.Url) ? "新快捷项" : result.Url)
                                    : result.Name,
                                Type = "url",
                                Target = result.Url,
                                UseAppMode = result.IsAppMode,
                                BrowserId = result.BrowserId,
                                ProfileId = result.ProfileId
                            };
                            QuickGroupManager.AddItem(groupId, newItem);
                            contentPanel?.Children.Add(BuildItemButton(newItem, groupId));
                        }
                        QuickGroupManager.Save();
                        if (contentPanel == null) LoadGroups();
                    }
                    else if (shortcutDialog.SingleResult != null)
                    {
                        var result = shortcutDialog.SingleResult;
                        var newItem = new QuickItem
                        {
                            Name = string.IsNullOrEmpty(result.Name)
                                ? (string.IsNullOrEmpty(result.Url) ? "新快捷项" : result.Url)
                                : result.Name,
                            Type = "url",
                            Target = result.Url,
                            UseAppMode = result.IsAppMode,
                            BrowserId = result.BrowserId,
                            ProfileId = result.ProfileId
                        };
                        QuickGroupManager.AddItem(groupId, newItem);
                        QuickGroupManager.Save();
                        if (contentPanel != null)
                            contentPanel.Children.Add(BuildItemButton(newItem, groupId));
                        else
                            LoadGroups();
                    }
                }
                return;
            }

            // 程序类型：Tab式对话框
            var dialog = new Window
            {
                Title = "添加程序",
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
            rootPanel.Children.Add(new TextBlock
            {
                Text = "添加程序",
                FontWeight = FontWeights.Bold,
                FontSize = (double)Application.Current.FindResource("FontSizeSubtitle"),
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // === Tab 按钮栏 ===
            var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var tabManual = new Button { Content = "自行输入", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            var tabList = new Button { Content = "从列表", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            var tabBrowser = new Button { Content = "浏览器", Padding = new Thickness(12, 6, 12, 6) };
            tabBar.Children.Add(tabManual);
            tabBar.Children.Add(tabList);
            tabBar.Children.Add(tabBrowser);
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
                Content = "浏览选择 exe...",
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6)
            };
            manualPanel.Children.Add(new TextBlock
            {
                Text = "点击下方按钮选择可执行文件，或在下方直接输入路径",
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
                listPanel.Children.Add(new TextBlock
                {
                    Text = "暂无已注册程序，请先在设置→程序管理中添加",
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
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = prog.Name,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        FontSize = (double)Application.Current.FindResource("FontSizeNormal")
                    });
                    itemPanel.Children.Add(new TextBlock
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
                browserPanel.Children.Add(new TextBlock
                {
                    Text = "暂无已配置浏览器，请先在设置→程序管理中添加",
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
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = browser.Name,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        FontSize = (double)Application.Current.FindResource("FontSizeNormal")
                    });
                    itemPanel.Children.Add(new TextBlock
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
            var nameLabel = new TextBlock { Text = "名称：", Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(nameLabel, Dock.Left);
            nameDock.Children.Add(nameLabel);
            var nameBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            nameDock.Children.Add(nameBox);
            rootPanel.Children.Add(nameDock);

            // 路径
            var targetDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var targetLabel = new TextBlock { Text = "路径：", Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(targetLabel, Dock.Left);
            targetDock.Children.Add(targetLabel);
            var targetBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            targetDock.Children.Add(targetBox);
            rootPanel.Children.Add(targetDock);

            // 启动命令行
            var argsDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var argsLabel = new TextBlock { Text = "命令行：", Width = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("BrushTextMain") };
            DockPanel.SetDock(argsLabel, Dock.Left);
            argsDock.Children.Add(argsLabel);
            var argsBox = new TextBox { Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle") };
            argsDock.Children.Add(argsBox);
            rootPanel.Children.Add(argsDock);

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
                    Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    Title = "选择程序"
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
                                profileCombo.Items.Add("无");
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
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            okBtn.Click += (s, ev2) =>
            {
                string name = nameBox.Text.Trim();
                string target = targetBox.Text.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) { MessageBox.Show("请填写名称和路径"); return; }
                var newItem = new QuickItem
                {
                    Name = name,
                    Type = "program",
                    Target = target,
                    UseAppMode = false,
                    LaunchArgs = argsBox.Text.Trim(),
                    BrowserId = selectedBrowserId,
                    ProfileId = selectedProfileId
                };
                QuickGroupManager.AddItem(groupId, newItem);
                QuickGroupManager.Save();
                if (contentPanel != null)
                    contentPanel.Children.Add(BuildItemButton(newItem, groupId));
                else
                    LoadGroups();
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
                Title = "编辑项目", Width = 440,
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
            rootPanel.Children.Add(new TextBlock
            {
                Text = "名称",
                Foreground = (Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = (double)Application.Current.FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new TextBox
            {
                Text = item.Name,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            rootPanel.Children.Add(nameBox);

            // 目标
            rootPanel.Children.Add(new TextBlock
            {
                Text = "目标",
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
            var appModeLabel = new TextBlock
            {
                Text = "App 模式",
                Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            appModePanel.Children.Add(appModeSwitch);
            appModePanel.Children.Add(appModeLabel);
            rootPanel.Children.Add(appModePanel);

            // 按钮
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.FindResource("StandardButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var okBtn = new Button
            {
                Content = "保存",
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 8, 16, 8)
            };
            okBtn.Click += (s, ev) =>
            {
                item.Name = nameBox.Text.Trim();
                item.Target = targetBox.Text.Trim();
                item.UseAppMode = appModeSwitch.IsChecked == true;
                QuickGroupManager.UpdateGroup(QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == groupId) ?? new QuickGroup());
                QuickGroupManager.Save();
                itemBtn.Content = item.Name;
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

            if (candidates.Count == 0) { MessageBox.Show("未检测到可选的窗口"); return; }

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

        /// <summary>
        /// 向分组添加模块（便签、剪贴板等）
        /// </summary>
        private void AddModuleToGroup(QuickGroup group, PanelType type)
        {
            group.ChildPanels ??= new List<PanelItem>();
            
            var newPanel = new PanelItem { Type = type };
            
            // ✅ 为Timer类型设置SkipWrapper=true，跳过外层容器包装
            if (type == PanelType.Timer)
            {
                newPanel.SkipWrapper = true;  // 跳过外层容器包装
                newPanel.TimerItems = new List<TimerItem>();  // 初始化为空列表
            }
            // 新面板初始化为空，避免触发全局迁移
            else if (type == PanelType.StickyNotes)
            {
                newPanel.StickyNotes = new List<StickyNote>();
            }
            
            group.ChildPanels.Add(newPanel);
            QuickGroupManager.Save();
            LoadGroups(); // 重新加载分组UI
        }

        #endregion

        #region Helpers

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
