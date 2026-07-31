using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using SideDesk.Controls;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    public partial class MainWindow : AppBarWindow
    {
        private ScreenType _currentScreen;
        private WindowConfigManager? _configManager;
        private DockWindowState? _dockState;
        private DispatcherTimer? _hoverTimer;
        private SettingsWindow? _settingsWindow;

        public ScreenType CurrentScreen => _currentScreen;
        internal WindowConfigManager? ConfigManager => _configManager;
        internal DockWindowState? DockState => _dockState;


        public MainWindow(DockEdge side, ScreenType screen, int monitorIndex)
        {
            InitializeComponent();

            _currentScreen = screen;

            // 应用背景透明度
            ApplyBackgroundOpacity();

            // 订阅主题变更事件，重新应用背景和刷新窗口标签
            App.ThemeChanged += () =>
            {
                ApplyBackgroundOpacity();
                RefreshWindowTabs();
            };

            // 配置 AppBar
            DockSide = side;
            AppBarWidth = 105;
            ScreenIndex = monitorIndex;

            Loaded += MainWindow_Loaded;

            // Ctrl+1~9 快速切换窗口标签
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            // 初始欢迎通知（多条通知垂直堆叠）
            ShowNotification("🔔 示例通知：欢迎使用 元桌小屏！点击查看详情");
        }

        private void ApplyBackgroundOpacity()
        {
            // 默认按本地模式（半透明）应用；切换窗口时会根据类型修正
            ApplyBackgroundForConfig(null);
        }

        /// <summary>
        /// 根据窗口类型应用背景：本地模式使用配置的半透明度，网页模式使用不透明背景。
        /// AllowsTransparency 为窗口级属性无法运行时切换，故通过动态背景实现“仅本地模式半透明”。
        /// </summary>
        private void ApplyBackgroundForConfig(WindowConfig? config)
        {
            var app = (App)Application.Current;

            // 网页模式：使用不透明背景，避免半透明窗口与 WebView2 叠加导致异常显示
            if (config != null && config.Type == WindowType.WebView)
            {
                // 使用动态资源引用，主题切换时自动更新
                SetResourceReference(BackgroundProperty, "BrushBG");
                return;
            }

            // 本地模式：按设置的背景透明度应用半透明背景色
            double opacity = app.AppSettings.BackgroundOpacity / 100.0;
            var bgColor = (Color)FindResource("ColorBG");
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)(opacity * 255), bgColor.R, bgColor.G, bgColor.B));
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterAppBar();
            UpdateTitle();

            // 按当前停靠侧读取各自的悬停浮动配置并启动/停止计时器
            ReloadHoverConfig();

            // 应用通知条显示设置
            ApplyNotificationStrip();

            // 设置主窗口引用
            sidebarPanel.SetMainWindow(this);

            // 如果已设置窗口配置，切换到当前活动窗口（WebView2 仅在切换到 WebView 窗口时才懒初始化）
            if (_dockState != null && _dockState.WindowIds.Count > 0)
            {
                SwitchToWindowByIndex(_dockState.ActiveIndex);
            }
        }

        private void NavigateToCurrentScreen()
        {
            var app = (App)Application.Current;
            string url = app.GetUrlForScreen(_currentScreen);
            sidebarPanel.NavigateToUrl(url);
        }

        /// <summary>重新渲染原生模式面板（设置变更后调用）。</summary>
        public void RefreshNativePanels() => sidebarPanel.RefreshNativePanels();

        /// <summary>
        /// 重新加载悬停浮动配置：按当前 DockSide 从设置读取该侧的开关与宽度，
        /// 动态启动或停止悬停计时器。可在设置变更后热重载调用。
        /// </summary>
        public void ReloadHoverConfig()
        {
            var app = (App)Application.Current;
            var cfg = app.AppSettings.GetCurrentMonitor(ScreenIndex, app.GetCurrentDesktopId());
            // 按停靠侧读取对应的开关与展开宽度
            bool enabled = DockSide == DockEdge.Left
                ? cfg.LeftHoverEnabled
                : cfg.RightHoverEnabled;
            int width = DockSide == DockEdge.Left
                ? cfg.LeftHoverWidth
                : cfg.RightHoverWidth;

            if (enabled)
            {
                ExpandedWidth = width;
                if (_hoverTimer == null)
                {
                    _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                    _hoverTimer.Tick += HoverTimer_Tick;
                    _hoverTimer.Start();
                }
            }
            else
            {
                // 禁用：若正处于展开态先收回，再停止计时器
                // 收回宽度仅在 AppBar 已注册时安全，保持与 HoverTimer_Tick 一致的守护
                if (IsAppBarRegistered)
                {
                    SetHoverExpanded(false);
                }
                if (_hoverTimer != null)
                {
                    _hoverTimer.Stop();
                    _hoverTimer.Tick -= HoverTimer_Tick;
                    _hoverTimer = null;
                }
            }
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsAppBarRegistered) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethods.GetCursorPos(out var p)) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
            bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
            SetHoverExpanded(inside);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _hoverTimer?.Stop();
            if (_hoverTimer != null) _hoverTimer.Tick -= HoverTimer_Tick;
            _hoverTimer = null;
            base.OnClosing(e);
        }

        private void UpdateTitle()
        {
            titleText.Text = $"元桌小屏 [{_currentScreen}]";
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void MoreMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = sender as ContextMenu;
            if (menu == null) return;

            // 清除旧的动态窗口列表项（menuSeparator1 之前的所有项）
            int separatorIndex = menu.Items.IndexOf(menuSeparator1);
            for (int i = separatorIndex - 1; i >= 0; i--)
            {
                menu.Items.RemoveAt(i);
            }

            // 动态生成窗口列表
            if (_dockState != null && _configManager != null)
            {
                int insertIndex = 0;
                for (int i = 0; i < _dockState.WindowIds.Count; i++)
                {
                    var config = _configManager.GetById(_dockState.WindowIds[i]);
                    if (config == null) continue;

                    int index = i;
                    var item = new MenuItem
                    {
                        Header = $"{i + 1}. {config.Name}",
                        IsCheckable = true,
                        IsChecked = (i == _dockState.ActiveIndex)
                    };
                    item.Click += (s, args) => SwitchToWindowByIndex(index);
                    menu.Items.Insert(insertIndex, item);
                    insertIndex++;
                }
            }

            // 仅双侧模式下显示“关闭面板”（非双侧时不能关闭唯一的 dock）
            var app = (App)Application.Current;
            bool isBothSide = app.AppSettings.GetCurrentMonitor(ScreenIndex, app.GetCurrentDesktopId()).Layout == LayoutMode.Both;
            var showClose = isBothSide ? Visibility.Visible : Visibility.Collapsed;
            closeWindowMenu.Visibility = showClose;
            closeWindowSeparator.Visibility = showClose;

            // 未在设置中开启标题锁定时，隐藏"工具-窗口标题锁定器"（工具子菜单为空则一并隐藏）
            bool titleLockEnabled = app.AppSettings.EnableTitleLock;
            titleLockMenuItem.Visibility = titleLockEnabled ? Visibility.Visible : Visibility.Collapsed;
            toolsMenu.Visibility = titleLockEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>“小屏管理”菜单入口：弹出独立管理窗口。</summary>
        private void ScreenManager_Click(object sender, RoutedEventArgs e)
        {
            if (_configManager == null || _dockState == null) return;
            new ScreenManagerWindow(this).ShowDialog();
        }

        /// <summary>通用主题化确认对话框（返回是否点击了确定）。</summary>
        internal bool ShowConfirmDialog(string title, string message, string okText = "确定", string cancelText = "取消")
        {
            var dialog = new Window
            {
                Title = title,
                Width = 340,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 20)
            });

            bool result = false;
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button
            {
                Content = cancelText, Width = 75, MinHeight = 30, Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("StandardButtonStyle"),
                IsCancel = true
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            var okBtn = new Button
            {
                Content = okText, Width = 75, MinHeight = 30, IsDefault = true,
                Style = (Style)FindResource("StandardButtonStyle")
            };
            okBtn.Click += (s, e) => { result = true; dialog.Close(); };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        private void EditWindowAtIndex(int index)
        {
            if (_configManager == null || _dockState == null) return;
            if (index < 0 || index >= _dockState.WindowIds.Count) return;

            var windowId = _dockState.WindowIds[index];
            var windowConfig = _configManager.GetById(windowId);
            if (windowConfig == null) return;

            var dialog = new EditWindowDialog(windowConfig.Name);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                windowConfig.Name = dialog.NewName;
                _configManager.Save();
                RefreshWindowTabs();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsVisible)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (s, ev) => _settingsWindow = null;
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        /// <summary>
        /// 打开窗口标题锁定器（选择窗口界面，与设置-工具入口一致）
        /// </summary>
        private void TitleLock_Click(object sender, RoutedEventArgs e)
        {
            var window = new TitleLockPickerWindow { Owner = this };
            window.ShowDialog();
        }

        private void PluginEditor_Click(object sender, RoutedEventArgs e)
        {
            var window = new Windows.PluginEditorWindow();
            window.Show();
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (app.AppSettings.GetCurrentMonitor(ScreenIndex, app.GetCurrentDesktopId()).Layout != LayoutMode.Both) return;

            // 关闭当前 dock 面板：切换为只保留另一侧（仅作用于本显示器）
            if (DockSide == DockEdge.Left)
                app.SwitchLayoutForMonitor(ScreenIndex, LayoutMode.Right);
            else
                app.SwitchLayoutForMonitor(ScreenIndex, LayoutMode.Left);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).CloseAll();
        }


        #region 屏幕切换

        /// <summary>
        /// 切换当前窗口显示的屏幕内容
        /// </summary>
        public void SwitchScreen(ScreenType screen)
        {
            _currentScreen = screen;
            UpdateTitle();
            NavigateToCurrentScreen();
            var app = (App)Application.Current;
            app.RememberScreen(ScreenIndex, DockSide, screen);
            app.SaveState();
        }

        private void SwitchToScreenA(object sender, RoutedEventArgs e)
        {
            SwitchScreen(ScreenType.A);
        }

        private void SwitchToScreenB(object sender, RoutedEventArgs e)
        {
            SwitchScreen(ScreenType.B);
        }

        #endregion

        #region 布局切换菜单

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            var loginWindow = new LoginWindow();
            loginWindow.Owner = this;
            loginWindow.ShowDialog();
        }

        private void Layout_Left(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayoutForMonitor(ScreenIndex, LayoutMode.Left);
        }

        private void Layout_Right(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayoutForMonitor(ScreenIndex, LayoutMode.Right);
        }

        private void Layout_Both(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayoutForMonitor(ScreenIndex, LayoutMode.Both);
        }

        #endregion

        #region 窗口标签页系统

        /// <summary>
        /// 由 App 调用设置窗口配置管理器
        /// </summary>
        public void SetWindowConfigManager(WindowConfigManager manager)
        {
            _configManager = manager;
            _dockState = (DockSide == DockEdge.Left) ? manager.LeftDock : manager.RightDock;
            RefreshWindowTabs();
        }

        internal void RefreshWindowTabs()
        {
            if (_configManager == null || _dockState == null) return;
            windowTabsPanel.Children.Clear();

            // 只有一个窗口时不显示标签和加号，显示标题
            if (_dockState.WindowIds.Count <= 1)
            {
                singleWindowTitle.Visibility = Visibility.Visible;
                windowTabsPanel.Visibility = Visibility.Collapsed;
                addWindowBtn.Visibility = Visibility.Collapsed;
                return;
            }

            singleWindowTitle.Visibility = Visibility.Collapsed;
            windowTabsPanel.Visibility = Visibility.Visible;
            addWindowBtn.Visibility = Visibility.Visible;

            for (int i = 0; i < _dockState.WindowIds.Count; i++)
            {
                var winId = _dockState.WindowIds[i];
                var config = _configManager.GetById(winId);
                if (config == null) continue;

                var index = i;
                var btn = new Button
                {
                    Content = $"{i + 1}",
                    ToolTip = config.Name,
                    Tag = index,
                    Margin = new Thickness(1, 0, 1, 0),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = (i == _dockState.ActiveIndex)
                        ? (Brush)FindResource("BrushAccent")
                        : Brushes.Transparent,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    BorderThickness = new Thickness(0)
                };
                btn.Click += (s, e) => WindowTab_Click(index);

                // 右键菜单
                var ctx = new ContextMenu();
                
                // 登录选项
                var loginItem = new MenuItem { Header = "登录" };
                loginItem.Click += (s, e) => Login_Click(s, e);
                ctx.Items.Add(loginItem);
                
                ctx.Items.Add(new Separator());

                var editItem = new MenuItem { Header = "编辑窗口" };
                int editIdx = index;
                editItem.Click += (s, e) => EditWindowAtIndex(editIdx);
                ctx.Items.Add(editItem);

                var deleteItem = new MenuItem { Header = "删除此窗口" };
                deleteItem.Click += (s, e) => RemoveWindow(index);
                if (_dockState.WindowIds.Count <= 1)
                    deleteItem.Visibility = Visibility.Collapsed;
                ctx.Items.Add(deleteItem);
                
                // 添加分隔符
                ctx.Items.Add(new Separator());
                
                // 布局切换选项
                var layoutLeftItem = new MenuItem { Header = "居左" };
                layoutLeftItem.Click += (s, e) => Layout_Left(s, e);
                ctx.Items.Add(layoutLeftItem);
                
                var layoutRightItem = new MenuItem { Header = "居右" };
                layoutRightItem.Click += (s, e) => Layout_Right(s, e);
                ctx.Items.Add(layoutRightItem);
                
                var layoutBothItem = new MenuItem { Header = "双侧" };
                layoutBothItem.Click += (s, e) => Layout_Both(s, e);
                ctx.Items.Add(layoutBothItem);
                
                // 添加分隔符
                ctx.Items.Add(new Separator());
                
                // 退出选项
                var exitItem = new MenuItem { Header = "退出" };
                exitItem.Click += (s, e) => Exit_Click(s, e);
                ctx.Items.Add(exitItem);

                btn.ContextMenu = ctx;

                windowTabsPanel.Children.Add(btn);
            }
        }

        private void WindowTab_Click(int index)
        {
            if (_configManager == null || _dockState == null) return;
            if (index < 0 || index >= _dockState.WindowIds.Count) return;

            _dockState.ActiveIndex = index;
            var config = _configManager.GetById(_dockState.WindowIds[index]);
            sidebarPanel.SwitchToWindow(config, _configManager);
            ApplyBackgroundForConfig(config);
            RefreshWindowTabs();
            _configManager.Save();
        }

        internal void SwitchToWindowByIndex(int index)
        {
            if (_dockState == null || _configManager == null) return;
            if (index < 0 || index >= _dockState.WindowIds.Count) index = 0;
            if (_dockState.WindowIds.Count == 0) return;

            _dockState.ActiveIndex = index;
            var config = _configManager.GetById(_dockState.WindowIds[index]);
            sidebarPanel.SwitchToWindow(config, _configManager);
            ApplyBackgroundForConfig(config);
            RefreshWindowTabs();
        }

        /// <summary>
        /// Ctrl+1~9 / Ctrl+小键盘1~9 快速切换到对应序号的窗口标签。
        /// 注：焦点在 WebView2 内时因 airspace 限制可能无法捕获，焦点在 WPF 侧时生效。
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            int index = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) index = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) index = e.Key - Key.NumPad1;
            if (index < 0) return;

            if (_dockState == null || index >= _dockState.WindowIds.Count) return;

            WindowTab_Click(index);
            e.Handled = true;
        }

        /// <summary>供全局热键调用：切换到指定序号的窗口标签（越界则忽略）。</summary>
        public void SwitchTabByHotkey(int index)
        {
            if (_dockState == null || index < 0 || index >= _dockState.WindowIds.Count) return;
            WindowTab_Click(index);
        }

        private void AddWindow_Click(object sender, RoutedEventArgs e)
        {
            CreateNewWindow();
        }

        private void NewSideScreen_Click(object sender, RoutedEventArgs e)
        {
            CreateNewWindow();
        }

        private void CreateNewWindow()
        {
            var dialog = new AddWindowDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                _configManager!.Add(dialog.Result);
                _dockState!.WindowIds.Add(dialog.Result.Id);
                _configManager.Save();
                RefreshWindowTabs();
                WindowTab_Click(_dockState.WindowIds.Count - 1);

                // 已登录且开启云同步时，调用专用接口只提交新建的窗口（避免全量同步副作用；未登录/未开启时方法内部会跳过）
                _ = Services.CloudSyncService.Instance.CreateBarOnServerAsync(dialog.Result);
            }
        }

        private void RemoveWindow(int index)
        {
            if (_configManager == null || _dockState == null) return;
            if (_dockState.WindowIds.Count <= 1) return; // 至少保留一个

            _dockState.WindowIds.RemoveAt(index);
            if (_dockState.ActiveIndex >= _dockState.WindowIds.Count)
                _dockState.ActiveIndex = _dockState.WindowIds.Count - 1;

            _configManager.Save();
            RefreshWindowTabs();
            SwitchToWindowByIndex(_dockState.ActiveIndex);
        }

        #endregion

        #region 拖拽支持

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                foreach (var file in files)
                {
                    Debug.WriteLine($"[Drop] 文件: {file}");
                }
                // 无目标接收处理（如文件区），弹出窗口让用户选择
                // 注意：不能在 Drop 处理器内同步 ShowDialog，否则会阻塞 OLE 拖放的返回，
                // 导致后续拖拽不再触发。因此延迟到 Drop 返回后再弹窗。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = new FileDropDialog(files) { Owner = this };
                    dialog.ShowDialog();
                }), DispatcherPriority.Background);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.Data.GetData(DataFormats.Text)!;
                Debug.WriteLine($"[Drop] 文本: {text}");
            }
            e.Handled = true;
        }

        #endregion

        #region 通知栏

        /// <summary>显示通知（追加一条到通知栈，多条垂直排列互不遮挡）</summary>
        public void ShowNotification(string text) => AddNotification(text, null);

        /// <summary>显示通知（带自定义点击动作）</summary>
        public void ShowNotification(string text, Action clickAction) => AddNotification(text, clickAction);

        /// <summary>显示通知（自定义背景色 + 点击动作）</summary>
        public void ShowNotification(string text, Brush background, Action clickAction) => AddNotification(text, clickAction, background);

        /// <summary>隐藏通知栏（清空全部通知）</summary>
        public void HideNotification()
        {
            notificationStack.Children.Clear();
            notificationBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>向通知栈追加一条通知并显示容器</summary>
        private void AddNotification(string text, Action? clickAction, Brush? background = null)
        {
            var item = CreateNotificationItem(text, clickAction, background);
            notificationStack.Children.Add(item);
            notificationBar.Visibility = Visibility.Visible;
        }

        /// <summary>移除指定通知；栈空时隐藏容器（其余通知自动上移）</summary>
        private void RemoveNotificationItem(Border item)
        {
            notificationStack.Children.Remove(item);
            if (notificationStack.Children.Count == 0)
                notificationBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>构建单条通知 UI：主题色块 + 文字（点击触发动作）+ 悬浮关闭按钮</summary>
        private Border CreateNotificationItem(string text, Action? clickAction, Brush? background = null)
        {
            var bg = background ?? (Brush)FindResource("BrushAccent");
            var textBlock = new Emoji.Wpf.TextBlock
            {
                Text = text,
                Foreground = (Brush)FindResource("BrushAccentText"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 32,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false // 命中测试穿透到外层色块 Border，保证整块（含 Padding）响应一致
            };

            var closeBtn = new Button
            {
                Content = "✕",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                FontSize = 11,
                Width = 18,
                Height = 18,
                Background = bg,
                Foreground = (Brush)FindResource("BrushAccentText"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "关闭通知",
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid();
            grid.Children.Add(textBlock);
            grid.Children.Add(closeBtn);

            var item = new Border
            {
                Background = bg,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 4), // 多条通知之间的间隔
                Child = grid,
                Cursor = Cursors.Hand
            };

            // 悬停显示完整文字（绑定在整个色块上，含 Padding 区域；含 Emoji 渲染）
            item.ToolTip = new ToolTip
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                MaxWidth = 420,
                Content = new Emoji.Wpf.TextBlock
                {
                    Text = text,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            // 点击整个色块（含 Padding 区域）触发动作
            item.MouseLeftButtonUp += (s, e) =>
            {
                if (clickAction != null) clickAction.Invoke();
                else ShowNotificationDetail(text);
            };

            // 鼠标悬停时显示关闭按钮（不占布局，文字始终铺满）
            item.MouseEnter += (s, e) => closeBtn.Visibility = Visibility.Visible;
            item.MouseLeave += (s, e) => closeBtn.Visibility = Visibility.Collapsed;
            closeBtn.Click += (s, e) => RemoveNotificationItem(item);

            return item;
        }

        /// <summary>默认通知详情弹窗（主题化）</summary>
        private void ShowNotificationDetail(string text)
        {
            var (dialog, content) = CreateThemedDialog("通知详情", 400, 220);

            content.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            var closeBtn = new Button
            {
                Content = "关闭",
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)FindResource("PrimaryButtonStyle") // 高度自适应（文字+样式内边距），避免固定高度裁掉文字下部
            };
            closeBtn.Click += (s, ev) => dialog.Close();
            content.Children.Add(closeBtn);

            dialog.ShowDialog();
        }

        /// <summary>显示好友请求通知（顶部通知栏 + 右侧通知条色块）</summary>
        public void ShowFriendRequestNotification(string fromName, int requestId)
        {
            // 顶部通知栏
            ShowNotification($"👤 您收到来自「{fromName}」的添加好友通知，点击这里同意或拒绝", () =>
            {
                ShowFriendRequestDialog(fromName, requestId);
            });

            // 右侧通知条添加色块（绿色，24px 高，悬停提示 + 点击打开好友请求弹窗）
            AddStripNotification(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), 24,
                $"来自「{fromName}」的添加好友请求",
                () => ShowFriendRequestDialog(fromName, requestId));
        }

        /// <summary>好友请求处理弹窗（同意/拒绝，主题化）</summary>
        private void ShowFriendRequestDialog(string fromName, int requestId)
        {
            var (dialog, content) = CreateThemedDialog("好友请求", 320, 170);

            content.Children.Add(new TextBlock
            {
                Text = $"「{fromName}」请求添加你为好友",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var acceptBtn = new Button
            {
                Content = "同意",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("PrimaryButtonStyle"), // 高度自适应，避免固定高度裁掉文字下部
                Cursor = Cursors.Hand
            };
            acceptBtn.Click += async (s, ev) =>
            {
                var (ok, msg) = await Services.FriendService.Instance.HandleRequestAsync(requestId, "accept");
                ThemedMessageBox.Show(dialog, ok ? "已添加好友" : (string.IsNullOrEmpty(msg) ? "操作失败" : msg), "提示");
                dialog.Close();
            };
            btnPanel.Children.Add(acceptBtn);

            var rejectBtn = new Button
            {
                Content = "拒绝",
                Width = 80,
                Style = (Style)FindResource("StandardButtonStyle"), // 高度自适应，避免固定高度裁掉文字下部
                Cursor = Cursors.Hand
            };
            rejectBtn.Click += async (s, ev) =>
            {
                await Services.FriendService.Instance.HandleRequestAsync(requestId, "reject");
                dialog.Close();
            };
            btnPanel.Children.Add(rejectBtn);

            content.Children.Add(btnPanel);
            dialog.ShowDialog();

            // 处理完后隐藏通知栏、清除点击动作
            HideNotification();
        }

        /// <summary>创建统一主题的弹窗：无边框、圆角、自定义标题栏（可拖动/关闭）。返回弹窗和供填充的内容面板。</summary>
        private (Window dialog, StackPanel content) CreateThemedDialog(string title, double width, double height)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false
            };

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

            // 自定义标题栏（按住可拖动窗口）
            var titleBar = new Grid { Background = Brushes.Transparent };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button) return; // 点在关闭按钮上时不拖动
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove();
            };

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 12, 0, 12)
            };

            var closeBtn = new Button
            {
                Content = "✕",
                Width = 26,
                Height = 26,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("GhostButtonStyle"),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (s, e) => dialog.Close();

            titleBar.Children.Add(titleText);
            titleBar.Children.Add(closeBtn);
            Grid.SetRow(titleBar, 0);

            var content = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
            Grid.SetRow(content, 1);

            grid.Children.Add(titleBar);
            grid.Children.Add(content);
            root.Child = grid;
            dialog.Content = root;

            // 以停靠窗口（贴屏幕边缘）为 Owner 居中时，弹窗可能超出屏幕边缘（如右侧贴边时右对齐的按钮被裁切），
            // 加载后将位置钳制到所属屏幕工作区内
            dialog.Loaded += (s, e) => ClampDialogToScreen(dialog);

            return (dialog, content);
        }

        /// <summary>将弹窗钳制到其 Owner 所在屏幕的工作区内，避免超出屏幕导致内容被裁切。</summary>
        private void ClampDialogToScreen(Window dialog)
        {
            try
            {
                var owner = dialog.Owner;
                if (owner == null) return;

                // 取 Owner 中心点（WPF 逻辑坐标），用于定位所属屏幕
                double cx = owner.Left + owner.Width / 2;
                double cy = owner.Top + owner.Height / 2;

                // WPF 逻辑坐标 与 WinForms 物理像素 之间的 DPI 缩放
                var source = PresentationSource.FromVisual(dialog);
                double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)(cx * scaleX), (int)(cy * scaleY)));
                var wa = screen.WorkingArea;

                // 物理像素 → WPF 逻辑坐标
                double waLeft = wa.Left / scaleX;
                double waTop = wa.Top / scaleY;
                double waRight = wa.Right / scaleX;
                double waBottom = wa.Bottom / scaleY;

                // 钳制：仅在弹窗不超出屏幕时保证完整显示在工作区内
                if (dialog.Width <= waRight - waLeft)
                {
                    if (dialog.Left + dialog.Width > waRight) dialog.Left = waRight - dialog.Width;
                    if (dialog.Left < waLeft) dialog.Left = waLeft;
                }
                if (dialog.Height <= waBottom - waTop)
                {
                    if (dialog.Top + dialog.Height > waBottom) dialog.Top = waBottom - dialog.Height;
                    if (dialog.Top < waTop) dialog.Top = waTop;
                }
            }
            catch { }
        }

        #endregion

        #region 通知条（右侧 5px 竖条）

        /// <summary>根据设置应用通知条的显示/隐藏（设置变更后热重载调用）。</summary>
        public void ApplyNotificationStrip()
        {
            var app = (App)Application.Current;
            notificationStrip.Visibility = app.AppSettings.ShowNotificationStrip
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>向通知条添加一个彩色通知段（从上往下堆叠）。</summary>
        /// <param name="brush">通知段颜色</param>
        /// <param name="height">通知段高度（逻辑像素），默认 24</param>
        /// <param name="tip">鼠标悬停提示文本（可选）</param>
        /// <param name="onClick">点击该通知段的回调（可选）</param>
        public void AddStripNotification(Brush brush, double height = 24, string? tip = null, Action? onClick = null)
        {
            var segment = new Border
            {
                Background = brush,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (!string.IsNullOrEmpty(tip))
                segment.ToolTip = tip;
            if (onClick != null)
            {
                segment.Cursor = Cursors.Hand;
                segment.MouseLeftButtonUp += (s, e) => onClick();
            }
            notificationStripPanel.Children.Add(segment);
        }

        /// <summary>清空通知条上的所有通知段。</summary>
        public void ClearStripNotifications()
        {
            notificationStripPanel.Children.Clear();
        }

        #endregion

        #region 气泡提示

        /// <summary>显示气泡浮动提示（底部圆角小卡片，自动淡出消失）</summary>
        /// <param name="text">提示文本（支持 emoji）</param>
        /// <param name="durationMs">显示时长（毫秒），默认 3000</param>
        public void ShowBubbleNotification(string text, int durationMs = 3000)
        {
            var textBlock = new Emoji.Wpf.TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var bubble = new Border
            {
                Background = (Brush)FindResource("BrushDialogBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 0, 4),
                Child = textBlock,
                Opacity = 0
            };

            // 阴影效果
            bubble.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.25,
                Color = Colors.Black
            };

            bubbleStack.Children.Add(bubble);

            // 淡入
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            bubble.BeginAnimation(OpacityProperty, fadeIn);

            // 定时淡出并移除
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                fadeOut.Completed += (_, _) => bubbleStack.Children.Remove(bubble);
                bubble.BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        }

        #endregion
    }
}