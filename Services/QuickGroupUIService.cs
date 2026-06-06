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

            // + 按钮
            var addItemBtn = new Button
            {
                Content = "+", Width = 22, Height = 22,
                Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderThickness = new Thickness(0), FontSize = 14, FontWeight = FontWeights.Bold, Padding = new Thickness(0)
            };
            DockPanel.SetDock(addItemBtn, Dock.Right);
            titleBar.Children.Add(addItemBtn);

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
                Tag = "content:" + group.Id
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
                        Title = "重命名分组", Width = 300, Height = 130,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        ResizeMode = ResizeMode.NoResize,
                        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                    };
                    var sp = new StackPanel { Margin = new Thickness(10) };
                    var tb = new TextBox { Text = group.Name };
                    sp.Children.Add(tb);
                    var ok = new Button { Content = "确定", Width = 60, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                    ok.Click += (s3, ev3) => { group.Name = tb.Text.Trim(); titleText.Text = group.Name; QuickGroupManager.UpdateGroup(group); QuickGroupManager.Save(); dlg.Close(); };
                    sp.Children.Add(ok);
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
                    if (MessageBox.Show($"确定删除分组 \"{group.Name}\"？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        QuickGroupManager.RemoveGroup(group.Id);
                        QuickGroupManager.Save();
                        _container.Children.Remove(border);
                    }
                };
                popup.Items.Add(deleteItem);

                popup.PlacementTarget = toggleBtn;
                popup.Placement = PlacementMode.Bottom;
                popup.IsOpen = true;
            };

            // + 按钮事件
            addItemBtn.Click += (s, ev) =>
            {
                var addMenu = new ContextMenu();
                var miProg = new MenuItem { Header = "添加程序" };
                miProg.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "program");
                addMenu.Items.Add(miProg);
                var miUrl = new MenuItem { Header = "添加网址" };
                miUrl.Click += (s2, ev2) => ShowAddItemDialog(group.Id, contentPanel, "url");
                addMenu.Items.Add(miUrl);
                var miWin = new MenuItem { Header = "添加窗口" };
                miWin.Click += (s2, ev2) => ShowPickWindowDialog(group.Id);
                addMenu.Items.Add(miWin);
                addMenu.PlacementTarget = addItemBtn;
                addMenu.Placement = PlacementMode.Bottom;
                addMenu.IsOpen = true;
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
                FontSize = 11
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
                    var proc = Process.Start(psi);
                    if (proc != null) TrackProcessMainWindow(item, proc);
                }
                else
                {
                    // URL
                    string browserExe = "msedge";
                    var exeEntry = BrowserProfileManager.GetExeById(item.BrowserExeId);
                    if (exeEntry != null && !string.IsNullOrEmpty(exeEntry.ExePath))
                        browserExe = exeEntry.ExePath;

                    var process = new Process();
                    process.StartInfo.FileName = browserExe;
                    string args = item.Target ?? "";
                    if (item.UseAppMode) args = $"--app={item.Target}";

                    var profileEntry = BrowserProfileManager.GetProfileById(item.BrowserProfileId);
                    if (profileEntry != null && !string.IsNullOrEmpty(profileEntry.UserDataDir))
                        args += $" --user-data-dir=\"{profileEntry.UserDataDir}\"";

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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            var dialog = new Window
            {
                Title = "添加项目", Width = 440, Height = 340,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                Owner = OwnerWindow
            };

            var rootPanel = new StackPanel { Margin = new Thickness(15) };

            var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            typePanel.Children.Add(new TextBlock { Text = "类型:", Foreground = Brushes.White, Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var typeCombo = new ComboBox { Width = 120 };
            typeCombo.Items.Add("网址"); typeCombo.Items.Add("程序");
            typeCombo.SelectedIndex = (presetType == "program") ? 1 : 0;
            if (presetType != null) typeCombo.IsEnabled = false;
            typePanel.Children.Add(typeCombo);
            rootPanel.Children.Add(typePanel);

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            namePanel.Children.Add(new TextBlock { Text = "名称:", Foreground = Brushes.White, Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var nameBox = new TextBox { Width = 280 };
            namePanel.Children.Add(nameBox);
            rootPanel.Children.Add(namePanel);

            var targetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            targetPanel.Children.Add(new TextBlock { Text = "目标:", Foreground = Brushes.White, Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var targetBox = new TextBox { Width = 280 };
            targetPanel.Children.Add(targetBox);
            rootPanel.Children.Add(targetPanel);

            var appModeCheck = new CheckBox { Content = "App 模式 (无地址栏)", Foreground = Brushes.White, IsChecked = true, Margin = new Thickness(60, 5, 0, 5) };
            rootPanel.Children.Add(appModeCheck);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                string name = nameBox.Text.Trim();
                string target = targetBox.Text.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) { MessageBox.Show("请填写名称和目标"); return; }
                var newItem = new QuickItem
                {
                    Name = name,
                    Type = typeCombo.SelectedIndex == 0 ? "url" : "program",
                    Target = target,
                    UseAppMode = appModeCheck.IsChecked == true
                };
                QuickGroupManager.AddItem(groupId, newItem);
                QuickGroupManager.Save();
                if (contentPanel != null)
                    contentPanel.Children.Add(BuildItemButton(newItem, groupId));
                else
                    LoadGroups();
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            rootPanel.Children.Add(btnPanel);

            dialog.Content = rootPanel;
            dialog.ShowDialog();
        }

        private void ShowEditItemDialog(QuickItem item, string groupId, Button itemBtn)
        {
            var dialog = new Window
            {
                Title = "编辑项目", Width = 440, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                Owner = OwnerWindow
            };

            var rootPanel = new StackPanel { Margin = new Thickness(15) };

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            namePanel.Children.Add(new TextBlock { Text = "名称:", Foreground = Brushes.White, Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var nameBox = new TextBox { Width = 280, Text = item.Name };
            namePanel.Children.Add(nameBox);
            rootPanel.Children.Add(namePanel);

            var targetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            targetPanel.Children.Add(new TextBlock { Text = "目标:", Foreground = Brushes.White, Width = 60, VerticalAlignment = VerticalAlignment.Center });
            var targetBox = new TextBox { Width = 280, Text = item.Target };
            targetPanel.Children.Add(targetBox);
            rootPanel.Children.Add(targetPanel);

            var appModeCheck = new CheckBox { Content = "App 模式", Foreground = Brushes.White, IsChecked = item.UseAppMode, Margin = new Thickness(60, 5, 0, 5) };
            rootPanel.Children.Add(appModeCheck);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var okBtn = new Button { Content = "保存", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                item.Name = nameBox.Text.Trim();
                item.Target = targetBox.Text.Trim();
                item.UseAppMode = appModeCheck.IsChecked == true;
                QuickGroupManager.UpdateGroup(QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == groupId) ?? new QuickGroup());
                QuickGroupManager.Save();
                itemBtn.Content = item.Name;
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
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

            var dialog = new Window
            {
                Title = "选择窗口", Width = 500, Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                Owner = OwnerWindow
            };

            var rootPanel = new DockPanel { Margin = new Thickness(10) };
            var listBox = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                SelectionMode = SelectionMode.Extended
            };
            foreach (var c in candidates)
            {
                string display = string.IsNullOrEmpty(c.ProcessName) ? c.Title : $"[{c.ProcessName}] {c.Title}";
                var lbItem = new ListBoxItem { Content = display, Tag = c };
                listBox.Items.Add(lbItem);
            }

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70 };
            okBtn.Click += (s, ev) =>
            {
                var selected = listBox.SelectedItems.Cast<ListBoxItem>().ToList();
                if (selected.Count == 0) { MessageBox.Show("请选择至少一个窗口"); return; }

                string groupId = string.IsNullOrEmpty(targetGroupId)
                    ? QuickGroupManager.GetOrCreateDefaultGroup()
                    : targetGroupId;

                foreach (var lbi in selected)
                {
                    var info = ((IntPtr Hwnd, string Title, string? ExePath, string? ProcessName, string? ClassName))lbi.Tag;
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
                dialog.Close();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn); btnPanel.Children.Add(cancelBtn);
            rootPanel.Children.Add(btnPanel);
            rootPanel.Children.Add(listBox);

            dialog.Content = rootPanel;
            dialog.ShowDialog();
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
