using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using SideDesk.Controls;
using SideDesk.Models;

namespace SideDesk
{
    public partial class MainWindow : AppBarWindow
    {
        private ScreenType _currentScreen;
        private WindowConfigManager? _configManager;
        private DockWindowState? _dockState;
        private DispatcherTimer? _hoverTimer;

        public ScreenType CurrentScreen => _currentScreen;


        public MainWindow(DockEdge side, ScreenType screen)
        {
            InitializeComponent();

            _currentScreen = screen;

            // 应用背景透明度
            ApplyBackgroundOpacity();

            // 配置 AppBar
            DockSide = side;
            AppBarWidth = 105;
            ScreenIndex = 0;

            Loaded += MainWindow_Loaded;
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
                Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                return;
            }

            // 本地模式：按设置的背景透明度应用半透明黑
            double opacity = app.AppSettings.BackgroundOpacity / 100.0;
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)(opacity * 255), 0, 0, 0));
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterAppBar();
            UpdateTitle();

            // 按当前停靠侧读取各自的悬停浮动配置并启动/停止计时器
            ReloadHoverConfig();

            // 设置主窗口引用
            sidebarPanel.SetMainWindow(this);

            // 初始化 SidebarPanel（默认 WebView2 模式）
            sidebarPanel.InitializeWebView();

            // 如果已设置窗口配置，切换到当前活动窗口
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

        /// <summary>
        /// 重新加载悬停浮动配置：按当前 DockSide 从设置读取该侧的开关与宽度，
        /// 动态启动或停止悬停计时器。可在设置变更后热重载调用。
        /// </summary>
        public void ReloadHoverConfig()
        {
            var app = (App)Application.Current;
            // 按停靠侧读取对应的开关与展开宽度
            bool enabled = DockSide == DockEdge.Left
                ? app.AppSettings.LeftHoverEnabled
                : app.AppSettings.RightHoverEnabled;
            int width = DockSide == DockEdge.Left
                ? app.AppSettings.LeftHoverWidth
                : app.AppSettings.RightHoverWidth;

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
            titleText.Text = $"SideDesk [{_currentScreen}]";
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

            // 仅双侧模式下显示"关闭面板"（非双侧时不能关闭唯一的 dock）
            var app = (App)Application.Current;
            bool isBothSide = app.CurrentLayout == LayoutMode.Both;
            var showClose = isBothSide ? Visibility.Visible : Visibility.Collapsed;
            closeWindowMenu.Visibility = showClose;
            closeWindowSeparator.Visibility = showClose;

            // 未在设置中开启标题锁定时，隐藏"工具-窗口标题锁定器"（工具子菜单为空则一并隐藏）
            bool titleLockEnabled = app.AppSettings.EnableTitleLock;
            titleLockMenuItem.Visibility = titleLockEnabled ? Visibility.Visible : Visibility.Collapsed;
            toolsMenu.Visibility = titleLockEnabled ? Visibility.Visible : Visibility.Collapsed;
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
            var settingsWin = new SettingsWindow();
            settingsWin.Owner = this;
            settingsWin.ShowDialog();
        }

        /// <summary>
        /// 打开窗口标题锁定器（选择窗口界面，与设置-工具入口一致）
        /// </summary>
        private void TitleLock_Click(object sender, RoutedEventArgs e)
        {
            var window = new TitleLockPickerWindow { Owner = this };
            window.ShowDialog();
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (app.CurrentLayout != LayoutMode.Both) return;

            // 关闭当前 dock 面板：切换为只保留另一侧
            if (DockSide == DockEdge.Left)
                app.SwitchLayout(LayoutMode.Right);
            else
                app.SwitchLayout(LayoutMode.Left);
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
            ((App)Application.Current).SaveState();
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
            ((App)Application.Current).SwitchLayout(LayoutMode.Left);
        }

        private void Layout_Right(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayout(LayoutMode.Right);
        }

        private void Layout_Both(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchLayout(LayoutMode.Both);
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

        private void RefreshWindowTabs()
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

        private void SwitchToWindowByIndex(int index)
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
    }
}