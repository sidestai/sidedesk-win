using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using SideDesk.Controls;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Windows;

namespace SideDesk
{
    public partial class MainWindow : AppBarWindow
    {
        private WindowConfigManager? _configManager;
        private DockWindowState? _dockState;
        private DispatcherTimer? _hoverTimer;
        private SettingsWindow? _settingsWindow;

        /// <summary>启动引导通知是否已入队（多窗口实例共享，避免重复入队；富通知会广播到所有窗口）</summary>
        private static bool _welcomeNoticeEnqueued;

        internal WindowConfigManager? ConfigManager => _configManager;
        internal DockWindowState? DockState => _dockState;


        public MainWindow(DockEdge side, int monitorIndex)
        {
            InitializeComponent();

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

            // 启动引导通知（富通知）：点击在独立 WebView2 窗口打开网页；点击过一次后永久不再出现
            var app = (App)Application.Current;
            const string welcomeNoticeKey = "welcome_open_webview";
            if (!_welcomeNoticeEnqueued && app.AppSettings.ShouldShowNotice(welcomeNoticeKey))
            {
                _welcomeNoticeEnqueued = true;
                Services.NotificationService.Instance.Enqueue(new NotificationPayload
                {
                    Title = Framework.Lang.T("nativePanel.clickToOpenA"),
                    Body = Framework.Lang.T("nativePanel.browseTheWebIn"),
                    NoticeKey = welcomeNoticeKey,
                    ClickMain = new NotificationAction
                    {
                        Type = "open_webview",
                        Payload = new Dictionary<string, object>
                        {
                            ["url"] = "https://www.baidu.com",
                            ["title"] = Framework.Lang.T("common.web")
                        }
                    }
                });
            }
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

            // 按当前停靠侧读取各自的悬停浮动配置并启动/停止计时器
            ReloadHoverConfig();

            // 应用通知条显示设置
            ApplyNotificationStrip();

            // 设置主窗口引用
            sidebarPanel.SetMainWindow(this);

            // 初始化浮动上下文窗口（输入框焦点/剪贴板复制触发，全局跨窗口 tab）
            InitFloatingContext();

            // 初始化全局语音输入热键（启动键开始识别并实时显示在浮动窗，停止键粘贴到焦点位置）
            InitVoiceHotkey();

            // 初始化手机扫码远程语音（手机按住说话经后端 WS 中继，流式转写后粘贴到焦点位置）
            InitMobileVoice();

            // 如果已设置窗口配置，切换到当前活动窗口（WebView2 仅在切换到 WebView 窗口时才懒初始化）
            if (_dockState != null && _dockState.WindowIds.Count > 0)
            {
                SwitchToWindowByIndex(_dockState.ActiveIndex);
            }
        }

        /// <summary>重新渲染原生模式面板（设置变更后调用）。</summary>
        public void RefreshNativePanels() => sidebarPanel.RefreshNativePanels();

        /// <summary>好友资料变更通知（WS 推送）：更新原生面板的好友缓存并刷新展示。</summary>
        public void UpdateFriendProfile(string userId, string? nickName, string? avatar)
            => sidebarPanel.UpdateFriendProfile(userId, nickName, avatar);

        /// <summary>好友上/下线通知（WS 推送）：更新原生面板好友缓存的在线状态并刷新展示。</summary>
        public void UpdateFriendOnlineStatus(string userId, bool online)
            => sidebarPanel.UpdateFriendOnlineStatus(userId, online);

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
        }

        /// <summary>
        /// "工具"子菜单展开时动态填充：与底部右键菜单共用同一份工具列表（NativeModePanel.PopulateToolsMenu）
        /// </summary>
        private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;
            // SubmenuOpened 是冒泡事件：需过滤子级子菜单冒泡上来的事件，只响应本菜单项自身展开
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            sidebarPanel.PopulateToolsMenu(menu);
        }

        /// <summary>
        /// "添加模块"子菜单展开时动态填充：与空白处右键菜单的 addModuleMenu 内容一致（NativeModePanel.PopulateAddModuleMenu）
        /// </summary>
        private void AddModuleMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuItem;
            if (menu == null) return;
            // SubmenuOpened 是冒泡事件：内部的“添加数据块/插件”子菜单展开时也会冒泡到这里，
            // 若在此重建 Items 会导致整个菜单崩塌，只响应本菜单项自身展开
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            sidebarPanel.PopulateAddModuleMenu(menu);
        }

        /// <summary>“小屏管理”菜单入口：弹出独立管理窗口。</summary>
        private void ScreenManager_Click(object sender, RoutedEventArgs e)
        {
            if (_configManager == null || _dockState == null) return;
            new ScreenManagerWindow(this).ShowDialog();
        }

        /// <summary>打开通知中心窗口（已有实例则激活）</summary>
        private void NotificationCenter_Click(object sender, RoutedEventArgs e)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is NotificationCenterWindow existing)
                {
                    existing.Show();
                    existing.Activate();
                    return;
                }
            }
            new NotificationCenterWindow().Show();
        }

        /// <summary>通用主题化确认对话框（返回是否点击了确定）。</summary>
        internal bool ShowConfirmDialog(string title, string message, string okText = null, string cancelText = null)
        {
            okText ??= Framework.Lang.T("common.ok");
            cancelText ??= Framework.Lang.T("common.cancel");
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
                // 改名影响所有窗口标签的 ToolTip，广播刷新
                ((App)Application.Current).RefreshAllWindowTabs();
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


        #region 布局切换菜单

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            // 非模态打开登录窗（已有实例则激活前置，避免重复弹出）
            LoginWindow.Open();
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

            // 只有一个窗口时产品名放在星号列左对齐；多窗口时放在标签列右侧右对齐，
            // 星号列始终保持占位，确保更多按钮始终顶到右上角
            if (_dockState.WindowIds.Count <= 1)
            {
                Grid.SetColumn(singleWindowTitle, 0);
                singleWindowTitle.HorizontalAlignment = HorizontalAlignment.Left;
                windowTabsPanel.Visibility = Visibility.Collapsed;
                addWindowBtn.Visibility = Visibility.Collapsed;
                return;
            }

            Grid.SetColumn(singleWindowTitle, 1);
            singleWindowTitle.HorizontalAlignment = HorizontalAlignment.Right;

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
                var loginItem = new MenuItem { Header = Framework.Lang.T("common.logIn") };
                loginItem.Click += (s, e) => Login_Click(s, e);
                ctx.Items.Add(loginItem);
                
                ctx.Items.Add(new Separator());

                var editItem = new MenuItem { Header = Framework.Lang.T("window.editWindow") };
                int editIdx = index;
                editItem.Click += (s, e) => EditWindowAtIndex(editIdx);
                ctx.Items.Add(editItem);

                var deleteItem = new MenuItem { Header = Framework.Lang.T("nativePanel.deleteThisWindow") };
                deleteItem.Click += (s, e) => RemoveWindow(index);
                if (_dockState.WindowIds.Count <= 1)
                    deleteItem.Visibility = Visibility.Collapsed;
                ctx.Items.Add(deleteItem);
                
                // 添加分隔符
                ctx.Items.Add(new Separator());
                
                // 布局切换选项
                var layoutLeftItem = new MenuItem { Header = Framework.Lang.T("nativePanel.left") };
                layoutLeftItem.Click += (s, e) => Layout_Left(s, e);
                ctx.Items.Add(layoutLeftItem);
                
                var layoutRightItem = new MenuItem { Header = Framework.Lang.T("nativePanel.right") };
                layoutRightItem.Click += (s, e) => Layout_Right(s, e);
                ctx.Items.Add(layoutRightItem);
                
                var layoutBothItem = new MenuItem { Header = Framework.Lang.T("common.bothSides") };
                layoutBothItem.Click += (s, e) => Layout_Both(s, e);
                ctx.Items.Add(layoutBothItem);
                
                // 添加分隔符
                ctx.Items.Add(new Separator());
                
                // 退出选项
                var exitItem = new MenuItem { Header = Framework.Lang.T("common.exit") };
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
        /// Ctrl+1~9 / Ctrl+小键盘1~9 快速切换到对应序号的窗口标签；
        /// ESC 隐藏正在显示的 AI推荐 浮动框。
        /// 注：焦点在 WebView2 内时因 airspace 限制可能无法捕获，焦点在 WPF 侧时生效。
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ESC：若 AI推荐 浮动框正在显示则隐藏（同关闭按钮，下次触发仍会重新出现）
            if (e.Key == Key.Escape && TryHideFloatingContextByEscape())
            {
                e.Handled = true;
                return;
            }

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

        private void ImportShare_Click(object sender, RoutedEventArgs e)
        {
            ShowImportShareDialog();
        }

        /// <summary>
        /// 导入分享对话框：两步流程。
        /// 步骤1：输入短码 → 查询预览信息展示给用户；
        /// 步骤2：用户确认 → 执行导入。
        /// </summary>
        private void ShowImportShareDialog()
        {
            var app = (App)Application.Current;
            if (string.IsNullOrEmpty(app.AppSettings?.AccountToken))
            {
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("nativePanel.loginRequiredImportShare")), Framework.Lang.T("common.notice"));
                return;
            }

            var dialog = new Window
            {
                Title = Framework.Lang.T("cloudDrive.importShare"),
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false
            };

            var root = new Border
            {
                Background = (System.Windows.Media.Brush)TryFindResource("BrushDialogBG"),
                BorderBrush = (System.Windows.Media.Brush)TryFindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            var outerStack = new StackPanel();

            // ===== 标题栏 =====
            var titleBar = new Grid { Background = System.Windows.Media.Brushes.Transparent };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button) return;
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove();
            };
            var titleText = new TextBlock
            {
                Text = Framework.Lang.T("cloudDrive.importShare"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 12, 0, 12)
            };
            titleBar.Children.Add(titleText);
            var closeBtn = new Button
            {
                Content = "\u2715",
                Width = 26, Height = 26, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)TryFindResource("GhostButtonStyle"),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (s, e) => dialog.Close();
            titleBar.Children.Add(closeBtn);
            outerStack.Children.Add(titleBar);

            // ===== 步骤1：输入区域 =====
            var inputArea = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
            inputArea.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("settings.enterTheShareShort"),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextSub"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var inputBox = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = (System.Windows.Media.Brush)TryFindResource("BrushBorder"),
                Background = (System.Windows.Media.Brush)TryFindResource("BrushInputBG"),
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextMain"),
                CaretBrush = (System.Windows.Media.Brush)TryFindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(inputBox, 0);
            var lookupBtn = new Button
            {
                Content = Framework.Lang.T("common.query"),
                Width = 64,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)TryFindResource("StandardButtonStyle"),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = true
            };
            Grid.SetColumn(lookupBtn, 1);
            inputRow.Children.Add(inputBox);
            inputRow.Children.Add(lookupBtn);
            inputArea.Children.Add(inputRow);
            outerStack.Children.Add(inputArea);

            // ===== 步骤2：预览信息区域（初始隐藏）=====
            var previewArea = new Border
            {
                Margin = new Thickness(16, 12, 16, 0),
                Padding = new Thickness(12),
                BorderThickness = new Thickness(1),
                BorderBrush = (System.Windows.Media.Brush)TryFindResource("BrushBorder"),
                CornerRadius = new CornerRadius(6),
                Background = (System.Windows.Media.Brush)TryFindResource("BrushInputBG"),
                Visibility = Visibility.Collapsed
            };
            var previewStack = new StackPanel();
            var previewTitle  = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 4) };
            var previewType   = new TextBlock { FontSize = 12,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextSub"),
                Margin = new Thickness(0, 0, 0, 2) };
            var previewMode   = new TextBlock { FontSize = 12,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextSub"),
                Margin = new Thickness(0, 0, 0, 2) };
            var previewCount  = new TextBlock { FontSize = 12,
                Foreground = (System.Windows.Media.Brush)TryFindResource("BrushTextSub") };
            previewStack.Children.Add(previewTitle);
            previewStack.Children.Add(previewType);
            previewStack.Children.Add(previewMode);
            previewStack.Children.Add(previewCount);
            previewArea.Child = previewStack;
            outerStack.Children.Add(previewArea);

            // ===== 底部按钮 =====
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 12, 16, 16)
            };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("common.cancel"),
                Width = 72,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)TryFindResource("StandardButtonStyle"),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsCancel = true
            };
            cancelBtn.Click += (s, e) => dialog.Close();
            var importBtn = new Button
            {
                Content = Framework.Lang.T("nativePanel.confirmImport"),
                Width = 88,
                Style = (Style)TryFindResource("PrimaryButtonStyle"),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = false   // 查询成功后才激活
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(importBtn);
            outerStack.Children.Add(btnPanel);

            root.Child = outerStack;
            dialog.Content = root;

            // 缓存预览结果，供导入步骤使用
            string cachedCode = "";
            string cachedShareLevel = "";

            // ===== 步骤1：查询 =====
            lookupBtn.Click += async (_, _) =>
            {
                string input = inputBox.Text.Trim();
                if (string.IsNullOrEmpty(input)) return;

                string code = input;
                if (input.Contains("/s/"))
                {
                    var parts = input.Split("/s/");
                    if (parts.Length > 1) code = parts[1].Trim().TrimEnd('/');
                }
                Debug.WriteLine($"[ImportShare] user input: '{input}' -> code: '{code}'");

                lookupBtn.IsEnabled = false;
                lookupBtn.Content = Framework.Lang.T("nativePanel.querying");
                previewArea.Visibility = Visibility.Collapsed;
                importBtn.IsEnabled = false;
                try
                {
                    var token = app.AppSettings.AccountToken;
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    string previewUrl = $"{Constants.URLs.ServerBaseUrl}/api/sidebar/share/{Uri.EscapeDataString(code)}";
                    Debug.WriteLine($"[ImportShare] preview request: GET {previewUrl}");
                    using var req = new HttpRequestMessage(HttpMethod.Get, previewUrl);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
                    string json = await resp.Content.ReadAsStringAsync();
                    Debug.WriteLine($"[ImportShare] preview response HTTP {(int)resp.StatusCode}: {json}");
                    var root2 = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root2["code"]?.ToObject<int>() != 200)
                    {
                        string errMsg = root2["msg"]?.ToString() ?? Framework.Lang.T("nativePanel.shareExpiredOrMissing");
                        Debug.WriteLine($"[ImportShare] preview failed: {errMsg}");
                        ThemedMessageBox.Show(dialog, errMsg, Framework.Lang.T(Framework.Lang.T("screenTime.queryFailed")));
                        return;
                    }

                    var d = root2["data"]!;
                    string shareLevel = d["shareLevel"]?.ToString() ?? "bar";
                    string shareMode  = d["shareMode"]?.ToString() ?? "copy";
                    string barTitle   = d["barTitle"]?.ToString() ?? Framework.Lang.T("settings.untitledWindow");
                    string itemType   = d["itemType"]?.ToString() ?? "";
                    string itemTitle  = d["itemTitle"]?.ToString() ?? "";
                    int    itemCount  = d["itemCount"]?.ToObject<int>() ?? 0;

                    // itemTitle 若为空用 itemType 兜底
                    string displayName = !string.IsNullOrEmpty(itemTitle) ? itemTitle
                                       : !string.IsNullOrEmpty(itemType)  ? itemType
                                       : barTitle;
                    Debug.WriteLine($"[ImportShare] preview ok: shareLevel={shareLevel}, shareMode={shareMode}, barTitle={barTitle}, itemTitle={itemTitle}, itemType={itemType}");

                    // 填充预览区
                    previewTitle.Text = shareLevel == "item" ? displayName : barTitle;
                    previewType.Text  = shareLevel == "item"
                        ? Framework.Lang.TF("nativePanel.typeModuleWindow", itemType, barTitle)
                        : Framework.Lang.TF("nativePanel.typeWindowModulesIn", itemCount);
                    previewMode.Text  = Framework.Lang.TF("nativePanel.shareMode", shareMode == "copy" ? Framework.Lang.T("settings.shareModeCopyHint") : Framework.Lang.T("nativePanel.referenceLiveDataHint"));
                    previewCount.Text = shareLevel == "bar" ? "" : "";
                    previewCount.Visibility = Visibility.Collapsed;
                    previewArea.Visibility = Visibility.Visible;

                    // 缓存给导入步骤
                    cachedCode       = code;
                    cachedShareLevel = shareLevel;
                    importBtn.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ImportShare] preview error: {ex.Message}");
                    ThemedMessageBox.Show(dialog, Framework.Lang.TF("nativePanel.queryFailed", ex.Message), Framework.Lang.T("common.error"));
                }
                finally
                {
                    lookupBtn.IsEnabled = true;
                    lookupBtn.Content = Framework.Lang.T("common.query");
                }
            };

            // ===== 步骤2：确认导入 =====
            importBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrEmpty(cachedCode)) return;
                importBtn.IsEnabled = false;
                importBtn.Content = Framework.Lang.T("nativePanel.importing");
                try
                {
                    await DoImportShareAsync(cachedCode, cachedShareLevel, dialog);
                }
                finally
                {
                    importBtn.IsEnabled = true;
                    importBtn.Content = Framework.Lang.T("nativePanel.confirmImport");
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>
        /// 执行导入：直接使用已知 shareLevel，读当前小屏 ServerId 并 POST 导入接口。
        /// </summary>
        private async Task DoImportShareAsync(string code, string shareLevel, Window dialog)
        {
            var app = (App)Application.Current;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var token = app.AppSettings.AccountToken;
            Debug.WriteLine($"[ImportShare] starting import, code={code}, shareLevel={shareLevel}, hasToken={!string.IsNullOrEmpty(token)}");
        
            // 构建导入请求（item 级将当前小屏的 ServerId 传入）
            string? targetBarServerId = null;
            if (shareLevel == "item" && _configManager != null && _dockState != null)
            {
                var activeId = _dockState.WindowIds.Count > _dockState.ActiveIndex
                    ? _dockState.WindowIds[_dockState.ActiveIndex]
                    : _dockState.WindowIds[0];
                targetBarServerId = _configManager.GetById(activeId)?.ServerId;
                Debug.WriteLine($"[ImportShare] item-level import, current bar ServerId={targetBarServerId ?? "(null)"}");
            }
        
            var importPayload = new System.Collections.Generic.Dictionary<string, object?> { ["shareCode"] = code };
            if (targetBarServerId != null)
                importPayload["targetBarServerId"] = targetBarServerId;
            // 预生成本地组件 GUID 随导入请求上报，服务器记录在 share_import.local_id
            string importLocalId = Guid.NewGuid().ToString();
            importPayload["localId"] = importLocalId;
            Debug.WriteLine($"[ImportShare] local component GUID localId={importLocalId}");
        
            var importJson = Newtonsoft.Json.JsonConvert.SerializeObject(importPayload);
            Debug.WriteLine($"[ImportShare] import request POST /sidebar/share/import, payload={importJson}");
            using var importContent = new System.Net.Http.StringContent(importJson, System.Text.Encoding.UTF8, "application/json");
            using var importReq = new HttpRequestMessage(HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/import") { Content = importContent };
            importReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var importResp = await Services.HttpLog.SendAsync(http, importReq);
            string importRespJson = await importResp.Content.ReadAsStringAsync();
            Debug.WriteLine($"[ImportShare] import response HTTP {(int)importResp.StatusCode}: {importRespJson}");
            var importResult = Newtonsoft.Json.Linq.JObject.Parse(importRespJson);
        
            if (importResult["code"]?.ToObject<int>() != 200)
            {
                string errMsg = importResult["msg"]?.ToString() ?? Framework.Lang.T("settings.importFailed");
                Debug.WriteLine($"[ImportShare] import failed: {errMsg}");
                ThemedMessageBox.Show(this, errMsg, Framework.Lang.T(Framework.Lang.T("settings.importFailed")));
                return;
            }
        
            var data = importResult["data"]!;
            string resultBarServerId = data["barServerId"]?.ToString() ?? "";
            string resultShareMode   = data["shareMode"]?.ToString() ?? "";
            string? sourceBarId      = data["sourceBarId"]?.ToString();
            string? resultShareId    = data["shareId"]?.ToString();
            string? resultShareCode  = data["shareCode"]?.ToString();
            // 权限快照（服务端导入时固化）：引用模式下客户端据此拦截编辑/删除
            bool resultAllowEdit     = data["allowEdit"]?.ToObject<bool>() ?? true;
            bool resultAllowDelete   = data["allowDelete"]?.ToObject<bool>() ?? true;
            bool resultAllowReShare  = data["allowReShare"]?.ToObject<bool>() ?? false;
            string  resultItemTitle  = data["itemTitle"]?.ToString() ?? "";
            string  resultItemType   = data["itemType"]?.ToString() ?? "";
            string  resultItemData   = data["itemData"]?.ToString() ?? "";
            Debug.WriteLine($"[ImportShare] import ok: shareLevel={shareLevel}, barServerId={resultBarServerId}, shareMode={resultShareMode}");

            if (shareLevel == "item")
            {
                // === Item 级导入：在当前窗口创建本地 PanelItem ===
                string itemServerId = data["itemServerId"]?.ToString() ?? "";
                Debug.WriteLine($"[ImportShare] item-level: itemServerId={itemServerId}, itemType={resultItemType}");

                if (!string.IsNullOrEmpty(itemServerId) && _configManager != null && _dockState != null)
                {
                    // 解析 itemType → PanelType
                    PanelType panelType = PanelType.StickyNotes;
                    if (System.Enum.TryParse<PanelType>(resultItemType, true, out var parsed))
                        panelType = parsed;

                    // 创建 PanelItem（Id 使用导入前预生成并上报给服务器的 GUID）
                    var newPanel = new PanelItem
                    {
                        Id = importLocalId,
                        ServerId = itemServerId,
                        Type = panelType,
                        Title = !string.IsNullOrEmpty(resultItemTitle) ? resultItemTitle : null,
                    };

                    // 应用 itemData 中的模块设置（如有）
                    if (!string.IsNullOrEmpty(resultItemData))
                    {
                        try
                        {
                            var itemDataObj = Newtonsoft.Json.Linq.JObject.Parse(resultItemData);
                            foreach (var prop in itemDataObj.Properties())
                            {
                                var targetProp = newPanel.GetType().GetProperty(prop.Name);
                                if (targetProp != null && targetProp.CanWrite && prop.Value.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                                {
                                    try { targetProp.SetValue(newPanel, prop.Value.ToObject(targetProp.PropertyType)); }
                                    catch { /* 忽略单个字段解析失败 */ }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ImportShare] failed to parse itemData: {ex.Message}");
                        }
                    }

                    // 记录分享来源（itemData 应用之后赋值，避免被覆盖）：
                    // ShareId 可用于后续查分享详情/对账，ShareMode 用于区分副本与真共享
                    newPanel.ShareId = resultShareId;
                    newPanel.ShareCode = resultShareCode;
                    newPanel.ShareMode = resultShareMode;
                    newPanel.AllowEdit = resultAllowEdit;
                    newPanel.AllowDelete = resultAllowDelete;
                    newPanel.AllowReShare = resultAllowReShare;

                    // 添加到当前窗口
                    var activeId = _dockState.WindowIds.Count > _dockState.ActiveIndex
                        ? _dockState.WindowIds[_dockState.ActiveIndex]
                        : _dockState.WindowIds[0];
                    var activeWin = _configManager.GetById(activeId);
                    if (activeWin != null)
                    {
                        activeWin.Panels.Add(newPanel);
                        _configManager.Save();

                        // reference/copy 模式：本地为空，先全量拉取数据再渲染（增量 since 拉不到旧行；
                        // copy 模式服务端已复制源模块行数据，同样需要全量拉取才能看到副本内容）
                        if (resultShareMode == "reference" || resultShareMode == "copy")
                        {
                            bool pulled = await CloudSyncService.Instance.PullItemFullAsync(newPanel);
                            Debug.WriteLine($"[ImportShare] {resultShareMode} full pull {(pulled ? "ok" : "no data/failed")}");
                            if (pulled) _configManager.Save();
                        }

                        RefreshNativePanels();
                        Debug.WriteLine($"[ImportShare] local PanelItem created (Type={panelType}, ServerId={itemServerId}, Mode={resultShareMode}), panel refreshed");
                    }
                }

                dialog.Close();
                _ = CloudSyncService.Instance.SyncNowAsync(silent: true);
                ThemedMessageBox.Show(this,
                    Framework.Lang.TF("share.moduleImportCompleted", resultShareMode == "copy" ? Framework.Lang.T("share.copyIndependent") : Framework.Lang.T("share.referenceRealtime")),
                    Framework.Lang.T("common.importSucceeded"));
            }
            else
            {
                // === Bar 级导入：新建小屏并切换 ===
                dialog.Close();
                Debug.WriteLine($"[ImportShare] bar-level import completed, new bar created");
                string barTitleResult = data["barTitle"]?.ToString() ?? Framework.Lang.T("common.importWindow");
                var newWin = new WindowConfig
                {
                    Id = importLocalId,
                    Name = barTitleResult,
                    ServerId = resultBarServerId,
                    ShareMode = resultShareMode,
                    SourceBarId = sourceBarId,
                    ShareId = resultShareId,
                    ShareCode = resultShareCode,
                    AllowEdit = resultAllowEdit,
                    AllowDelete = resultAllowDelete,
                    AllowReShare = resultAllowReShare,
                    OwnerType = "personal"
                };
                _configManager?.AddToAllDocks(newWin);
                ((App)Application.Current).RefreshAllWindowTabs();
                WindowTab_Click(_dockState!.WindowIds.Count - 1);

                ThemedMessageBox.Show(this,
                    Framework.Lang.TF("share.screenImportSucceeded", barTitleResult, resultShareMode == "copy" ? Framework.Lang.T("share.copyIndependent") : Framework.Lang.T("share.referenceRealtime")),
                    Framework.Lang.T("common.importSucceeded"));
            }
        }

        private void CreateNewWindow()
        {
            var dialog = new AddWindowDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                // 新小屏同时加入左右两侧 dock，并广播刷新所有窗口的顶部标签
                _configManager!.AddToAllDocks(dialog.Result);
                ((App)Application.Current).RefreshAllWindowTabs();
                WindowTab_Click(_dockState!.WindowIds.Count - 1);

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

        /// <summary>
        /// 属主取消分享：从当前 dock 移除指定导入窗口（窗口配置由调用方统一处理）。
        /// </summary>
        internal void RemoveSharedWindowById(string windowId)
        {
            if (_configManager == null || _dockState == null) return;
            int idx = _dockState.WindowIds.IndexOf(windowId);
            if (idx < 0) return;

            _dockState.WindowIds.RemoveAt(idx);
            if (_dockState.WindowIds.Count > 0 && _dockState.ActiveIndex >= _dockState.WindowIds.Count)
                _dockState.ActiveIndex = _dockState.WindowIds.Count - 1;

            _configManager.Save();
            RefreshWindowTabs();
            if (_dockState.WindowIds.Count > 0)
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
                    Debug.WriteLine($"[Drop] file: {file}");
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
                Debug.WriteLine($"[Drop] text: {text}");
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

        /// <summary>显示通知（带标签，可通过 DismissNotificationByTag 定向关闭）</summary>
        public void ShowNotification(string text, Brush background, Action clickAction, string tag)
        {
            var item = CreateNotificationItem(text, clickAction, background);
            item.Tag = tag;
            notificationStack.Children.Add(item);
            notificationBar.Visibility = Visibility.Visible;
        }

        /// <summary>隐藏通知栏（清空全部通知）</summary>
        public void HideNotification()
        {
            notificationStack.Children.Clear();
            notificationBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>关闭指定标签的通知；栏空时隐藏容器</summary>
        public void DismissNotificationByTag(string tag)
        {
            for (int i = notificationStack.Children.Count - 1; i >= 0; i--)
            {
                if (notificationStack.Children[i] is Border b && Equals(b.Tag, tag))
                    notificationStack.Children.RemoveAt(i);
            }
            if (notificationStack.Children.Count == 0)
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

        /// <summary>构建单条通知 UI：主题色块 + 文字（点击触发动作）+ 悬浮关闭按钮（覆盖层，不占布局）</summary>
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

            // 关闭按钮：悬浮在右上角（与文字同一单元格、不参与布局测量），
            // 显隐只切透明度，文字始终占据全部可用宽度，布局不受任何影响。
            // 背景用不透明的通知条同色（主题悬停色是半透明的，会透出底下文字，故不用 GhostButtonStyle）；
            // 悬停色 = 在 opaqueBg 上预合成一层半透明黑遮罩，保证任何状态下都不透明
            SolidColorBrush opaqueBg;
            if (bg is SolidColorBrush sc)
            {
                var c = sc.Color;
                opaqueBg = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            }
            else
            {
                opaqueBg = new SolidColorBrush(Colors.Black);
            }
            opaqueBg.Freeze();
            var hoverBrush = new SolidColorBrush(BlendOver(Color.FromArgb(0x22, 0, 0, 0), opaqueBg.Color));
            hoverBrush.Freeze();

            // 自绘模板：只画 Background（不透明）+ 居中内容，无系统默认外观。
            // 注意：必须先完整搭建 FrameworkElementFactory 树再赋给 ControlTemplate，
            // 模板一经使用即被密封，之后再对 Factory 调用 SetValue 会抛 InvalidOperationException
            var tplRoot = new FrameworkElementFactory(typeof(Border));
            tplRoot.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            tplRoot.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            tplRoot.AppendChild(presenter);

            var closeBtn = new Button
            {
                Content = "✕",
                FontSize = 11,
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Background = opaqueBg,
                Foreground = (Brush)FindResource("BrushAccentText"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("notification.dismissNotification"),
                Opacity = 0,
                IsHitTestVisible = false,
                Template = new ControlTemplate
                {
                    TargetType = typeof(Button),
                    VisualTree = tplRoot
                }
            };

            // 悬停/按下反馈：只切不透明背景色，始终盖得住底下文字
            closeBtn.MouseEnter += (s, e) => closeBtn.Background = hoverBrush;
            closeBtn.MouseLeave += (s, e) => closeBtn.Background = opaqueBg;
            closeBtn.PreviewMouseLeftButtonDown += (s, e) => closeBtn.Background = opaqueBg;
            closeBtn.PreviewMouseLeftButtonUp += (s, e) => closeBtn.Background = hoverBrush;

            var grid = new Grid();
            grid.Children.Add(textBlock);
            grid.Children.Add(closeBtn); // 同单元格叠加，不定义列、不挤占文字宽度

            var item = new Border
            {
                Background = bg,
                Padding = new Thickness(8, 4, 4, 4), // 右侧收窄到 4px，让文字尽量铺满窄栏
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

            // 鼠标悬停时显示关闭按钮：按钮为覆盖层不占布局，只切透明度，显隐不引起任何布局变化
            item.MouseEnter += (s, e) => { closeBtn.Opacity = 1; closeBtn.IsHitTestVisible = true; };
            item.MouseLeave += (s, e) => { closeBtn.Opacity = 0; closeBtn.IsHitTestVisible = false; };
            closeBtn.Click += (s, e) => RemoveNotificationItem(item);

            return item;
        }

        /// <summary>将 top 半透明色叠加到 bottom 底色上（alpha 混合），返回完全不透明的结果色</summary>
        private static Color BlendOver(Color top, Color bottom)
        {
            double a = top.A / 255.0;
            byte r = (byte)(top.R * a + bottom.R * (1 - a));
            byte g = (byte)(top.G * a + bottom.G * (1 - a));
            byte b = (byte)(top.B * a + bottom.B * (1 - a));
            return Color.FromArgb(255, r, g, b);
        }

        /// <summary>默认通知详情弹窗（主题化）</summary>
        private void ShowNotificationDetail(string text)
        {
            var (dialog, content) = CreateThemedDialog(Framework.Lang.T("nativePanel.notificationDetails"), 400, 220);

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
                Content = Framework.Lang.T("common.close"),
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)FindResource("PrimaryButtonStyle") // 高度自适应（文字+样式内边距），避免固定高度裁掉文字下部
            };
            closeBtn.Click += (s, ev) => dialog.Close();
            content.Children.Add(closeBtn);

            dialog.ShowDialog();
        }

        /// <summary>显示好友请求通知（顶部通知栏 + 右侧通知条色块）</summary>
        public void ShowFriendRequestNotification(string fromName, string requestId)
        {
            // 顶部通知栏
            ShowNotification(Framework.Lang.TF("nativePanel.friendRequestNotification", fromName), () =>
            {
                ShowFriendRequestDialog(fromName, requestId);
            });

            // 右侧通知条添加色块（绿色，24px 高，悬停提示 + 点击打开好友请求弹窗）
            AddStripNotification(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), 24,
                Framework.Lang.TF("nativePanel.friendRequestFromNamed", fromName),
                () => ShowFriendRequestDialog(fromName, requestId));
        }

        /// <summary>好友请求处理弹窗（同意/拒绝，主题化）</summary>
        private void ShowFriendRequestDialog(string fromName, string requestId)
        {
            var (dialog, content) = CreateThemedDialog(Framework.Lang.T("common.friendRequest"), 320, 170);

            content.Children.Add(new TextBlock
            {
                Text = Framework.Lang.TF("nativePanel.friendRequestQuoted", fromName),
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var acceptBtn = new Button
            {
                Content = Framework.Lang.T("common.agree"),
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("PrimaryButtonStyle"), // 高度自适应，避免固定高度裁掉文字下部
                Cursor = Cursors.Hand
            };
            acceptBtn.Click += async (s, ev) =>
            {
                var (ok, msg) = await Services.FriendService.Instance.HandleRequestAsync(requestId, "accept");
                ThemedMessageBox.Show(dialog, ok ? Framework.Lang.T(Framework.Lang.T("nativePanel.friendAdded")) : (string.IsNullOrEmpty(msg) ? Framework.Lang.T("common.operationFailed") : msg), Framework.Lang.T("common.notice"));
                dialog.Close();
            };
            btnPanel.Children.Add(acceptBtn);

            var rejectBtn = new Button
            {
                Content = Framework.Lang.T("common.decline"),
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
                // GhostButtonStyle 默认 Padding=16,10 会把 26px 按钮的内容区压没，必须置 0
                Padding = new Thickness(0),
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

        #region 富通知卡片（WebSocket 推送通知）

        /// <summary>富通知卡片引用保持器（用于分组更新和定时器管理）</summary>
        private class RichCardHolder
        {
            public NotificationPayload Payload = null!;
            public Border Card = null!;
            public ProgressBar? ProgressBarRef;
            public Emoji.Wpf.TextBlock? BodyTextRef;
            public DispatcherTimer? Timer;
        }

        /// <summary>当前显示的富通知卡片（key: 通知 Id）</summary>
        private readonly Dictionary<string, RichCardHolder> _richCards = new();

        /// <summary>显示富通知（banner 模式：在通知栏渲染卡片；alert 模式：弹出模态对话框）</summary>
        public void ShowRichNotification(NotificationPayload n)
        {
            if (n.DisplayMode == NotificationDisplayMode.Alert)
            {
                ShowAlertNotification(n);
                return;
            }

            var holder = CreateRichCard(n);
            _richCards[n.Id] = holder;
            notificationStack.Children.Add(holder.Card);
            notificationBar.Visibility = Visibility.Visible;

            // 超时自动消失（timeout=0 为常驻）
            if (n.Timeout > 0)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(n.Timeout) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    DismissRichNotification(holder);
                };
                holder.Timer = timer;

                // 鼠标悬停时暂停计时
                holder.Card.MouseEnter += (s, e) => timer.Stop();
                holder.Card.MouseLeave += (s, e) => { if (holder.Card.IsLoaded) timer.Start(); };

                timer.Start();
            }
        }

        /// <summary>原地更新已显示的同组通知（进度/内容变更）</summary>
        public void UpdateRichNotification(NotificationPayload update)
        {
            if (string.IsNullOrEmpty(update.GroupId)) return;

            RichCardHolder? target = null;
            foreach (var kv in _richCards)
            {
                if (kv.Value.Payload.GroupId == update.GroupId)
                { target = kv.Value; break; }
            }
            if (target == null) return;

            // 更新进度条
            if (update.Progress.HasValue && target.ProgressBarRef != null)
            {
                target.ProgressBarRef.Value = update.Progress.Value;
                target.Payload.Progress = update.Progress;
            }

            // 更新正文
            if (update.Body != null && target.BodyTextRef != null)
            {
                target.BodyTextRef.Text = update.Body;
                target.Payload.Body = update.Body;
            }

            // 更新标题
            if (!string.IsNullOrEmpty(update.Title) && update.Title != target.Payload.Title)
            {
                target.Payload.Title = update.Title;
                // 重建卡片内容
                var index = notificationStack.Children.IndexOf(target.Card);
                if (index >= 0)
                {
                    var timer = target.Timer;
                    timer?.Stop();
                    notificationStack.Children.RemoveAt(index);
                    _richCards.Remove(target.Payload.Id);

                    target.Payload.Id = update.Id;
                    var newHolder = CreateRichCard(target.Payload);
                    newHolder.Timer = timer;
                    _richCards[target.Payload.Id] = newHolder;
                    notificationStack.Children.Insert(index, newHolder.Card);

                    // 重新绑定悬停暂停计时
                    if (timer != null)
                    {
                        newHolder.Card.MouseEnter += (s, e) => timer.Stop();
                        newHolder.Card.MouseLeave += (s, e) => { if (newHolder.Card.IsLoaded) timer.Start(); };
                        timer.Start();
                    }
                }
            }
        }

        /// <summary>关闭富通知卡片（淡出动画 → 移除 → 释放槽位）</summary>
        private void DismissRichNotification(RichCardHolder holder)
        {
            // 若该 holder 已被分组更新重建替换，使用最新的 holder（Payload 对象共享，Id 始终为最新）
            if (_richCards.TryGetValue(holder.Payload.Id, out var current))
                holder = current;

            holder.Timer?.Stop();

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            fadeOut.Completed += (_, _) =>
            {
                notificationStack.Children.Remove(holder.Card);
                if (notificationStack.Children.Count == 0)
                    notificationBar.Visibility = Visibility.Collapsed;
            };
            holder.Card.BeginAnimation(OpacityProperty, fadeOut);

            _richCards.Remove(holder.Payload.Id);
            NotificationService.Instance.OnNotificationDismissed(holder.Payload);
        }

        /// <summary>构建富通知卡片 UI</summary>
        private RichCardHolder CreateRichCard(NotificationPayload n)
        {
            var holder = new RichCardHolder { Payload = n };

            // 颜色解析
            Brush bg = ParseColor(n.BackgroundColor) ?? (Brush)FindResource("BrushAccent");
            Brush fg = ParseColor(n.TextColor) ?? (Brush)FindResource("BrushAccentText");
            var fgMuted = new SolidColorBrush(((SolidColorBrush)fg).Color) { Opacity = 0.75 };

            var stack = new StackPanel();

            // 大图（可选）
            if (!string.IsNullOrEmpty(n.LargeImage))
            {
                var image = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    MaxHeight = 80,
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var clip = new RectangleGeometry { Rect = new Rect(0, 0, 400, 80), RadiusX = 4, RadiusY = 4 };
                image.Clip = clip;
                LoadNotificationImage(image, n.LargeImage);
                stack.Children.Add(image);
            }

            // 标题行：标题 + 时间（关闭按钮悬浮叠加在卡片右上角，不占布局）
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = n.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetColumn(titleText, 0);
            titleRow.Children.Add(titleText);

            var timeText = new TextBlock
            {
                Text = n.Timestamp.ToString("HH:mm"),
                FontSize = 9,
                Foreground = fgMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                IsHitTestVisible = false
            };
            Grid.SetColumn(timeText, 1);
            titleRow.Children.Add(timeText);

            stack.Children.Add(titleRow);

            // 正文（可选，最多 2 行）
            if (!string.IsNullOrEmpty(n.Body))
            {
                var bodyText = new Emoji.Wpf.TextBlock
                {
                    Text = n.Body,
                    FontSize = 10.5,
                    Foreground = fgMuted,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 30,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                    IsHitTestVisible = false
                };
                holder.BodyTextRef = bodyText;
                stack.Children.Add(bodyText);
            }

            // 进度条（可选）
            if (n.Progress.HasValue)
            {
                var progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = n.Progress.Value,
                    Height = 4,
                    Margin = new Thickness(0, 5, 0, 0),
                    Background = new SolidColorBrush(Colors.White) { Opacity = 0.2 },
                    Foreground = fg,
                    BorderThickness = new Thickness(0),
                    IsHitTestVisible = false
                };
                holder.ProgressBarRef = progressBar;
                stack.Children.Add(progressBar);
            }

            // 操作按钮（可选）
            if (n.Buttons != null && n.Buttons.Count > 0)
            {
                var btnPanel = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                foreach (var action in n.Buttons)
                {
                    var btn = new Button
                    {
                        Content = action.Label ?? action.Type,
                        FontSize = 10,
                        Padding = new Thickness(8, 3, 8, 3),
                        Margin = new Thickness(0, 0, 6, 0),
                        Background = new SolidColorBrush(Colors.White) { Opacity = 0.15 },
                        Foreground = fg,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand
                    };
                    var capturedAction = action;
                    btn.Click += async (s, e) =>
                    {
                        RecordNoticeDismissal(n);
                        DismissRichNotification(holder);
                        await NotificationActionService.ExecuteAsync(capturedAction, n);
                    };
                    btnPanel.Children.Add(btn);
                }

                stack.Children.Add(btnPanel);
            }

            // 关闭按钮：与内容叠加在同一 Grid 单元格，悬浮右上角，不占布局（文字始终铺满）
            Button? closeBtn = null;
            if (n.AllowClose)
            {
                closeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = bg,
                    Foreground = fg,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = Framework.Lang.T("notification.dismissNotification"),
                    Visibility = Visibility.Collapsed
                };
                closeBtn.Click += (s, e) => DismissRichNotification(holder);
            }

            var overlay = new Grid();
            overlay.Children.Add(stack);
            if (closeBtn != null) overlay.Children.Add(closeBtn);

            // 外层卡片
            var card = new Border
            {
                Background = bg,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Child = overlay,
                Cursor = n.ClickMain != null ? Cursors.Hand : Cursors.Arrow,
                Tag = n.Id
            };
            holder.Card = card;

            // 悬停显示/隐藏关闭按钮
            if (closeBtn != null)
            {
                var closeRef = closeBtn;
                card.MouseEnter += (s, e) => closeRef.Visibility = Visibility.Visible;
                card.MouseLeave += (s, e) => closeRef.Visibility = Visibility.Collapsed;
            }

            // 点击通知主体 → 执行 clickMain
            card.MouseLeftButtonUp += async (s, e) =>
            {
                if (e.OriginalSource is Button) return; // 按钮点击不触发主体动作
                if (n.ClickMain != null)
                {
                    RecordNoticeDismissal(n);
                    DismissRichNotification(holder);
                    await NotificationActionService.ExecuteAsync(n.ClickMain, n);
                }
                else
                {
                    ShowNotificationDetail($"{n.Title}\n{n.Body}");
                }
            };

            return holder;
        }

        /// <summary>记录通知免打扰标识（通知被点击时调用；带 NoticeKey 的通知点击后不再重复出现）</summary>
        private void RecordNoticeDismissal(NotificationPayload n)
        {
            if (string.IsNullOrEmpty(n.NoticeKey)) return;
            var app = (App)Application.Current;
            app.AppSettings.DismissNotice(n.NoticeKey);
            app.AppSettings.Save();
        }

        /// <summary>解析颜色字符串（#RRGGBB / #AARRGGBB），失败返回 null</summary>
        private static Brush? ParseColor(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return null;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(color);
                return new SolidColorBrush(c);
            }
            catch { return null; }
        }

        /// <summary>加载通知大图（URL 异步下载缓存到临时目录；本地路径直接加载）</summary>
        private static void LoadNotificationImage(Image image, string source)
        {
            if (source.StartsWith("http://") || source.StartsWith("https://"))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cacheDir = Path.Combine(Path.GetTempPath(), "SideDesk", "notif-images");
                        Directory.CreateDirectory(cacheDir);
                        var fileName = $"{source.GetHashCode():X8}{Path.GetExtension(new Uri(source).AbsolutePath)}";
                        var cachePath = Path.Combine(cacheDir, fileName);

                        if (!File.Exists(cachePath))
                        {
                            using var http = new HttpClient();
                            var bytes = await http.GetByteArrayAsync(source);
                            await File.WriteAllBytesAsync(cachePath, bytes);
                        }

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(cachePath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();

                        image.Dispatcher.Invoke(() => image.Source = bmp);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Notification] failed to load image: {ex.Message}");
                    }
                });
            }
            else if (File.Exists(source))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(source);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    image.Source = bmp;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Notification] failed to load local image: {ex.Message}");
                }
            }
        }

        /// <summary>Alert 模式：弹出主题化模态对话框（内容同卡片 + 按钮）</summary>
        private void ShowAlertNotification(NotificationPayload n)
        {
            var (dialog, content) = CreateThemedDialog(n.Title, 380, 200);

            if (!string.IsNullOrEmpty(n.Body))
            {
                content.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = n.Body,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 16)
                });
            }

            // 进度条
            if (n.Progress.HasValue)
            {
                content.Children.Add(new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = n.Progress.Value,
                    Height = 6,
                    Margin = new Thickness(0, 0, 0, 16)
                });
            }

            // 按钮区
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            if (n.Buttons != null)
            {
                foreach (var action in n.Buttons)
                {
                    var btn = new Button
                    {
                        Content = action.Label ?? action.Type,
                        MinWidth = 70,
                        Margin = new Thickness(8, 0, 0, 0),
                        Style = (Style)FindResource("StandardButtonStyle")
                    };
                    var capturedAction = action;
                    btn.Click += async (s, e) =>
                    {
                        dialog.Close(); // Closed 事件统一释放槽位
                        await NotificationActionService.ExecuteAsync(capturedAction, n);
                    };
                    btnPanel.Children.Add(btn);
                }
            }

            // 无按钮时添加默认关闭按钮
            if (n.Buttons == null || n.Buttons.Count == 0)
            {
                var okBtn = new Button
                {
                    Content = Framework.Lang.T("common.ok"),
                    MinWidth = 70,
                    Style = (Style)FindResource("PrimaryButtonStyle")
                };
                okBtn.Click += (s, e) => dialog.Close();
                btnPanel.Children.Add(okBtn);
            }

            content.Children.Add(btnPanel);

            // 点击主体执行 clickMain
            if (n.ClickMain != null)
            {
                content.MouseLeftButtonUp += async (s, e) =>
                {
                    if (e.OriginalSource is Button) return;
                    dialog.Close();
                    await NotificationActionService.ExecuteAsync(n.ClickMain, n);
                };
            }

            // 统一在关闭时释放显示槽位（无论按钮/✕/clickMain 关闭）
            dialog.Closed += (s, e) => NotificationService.Instance.OnNotificationDismissed(n);
            dialog.ShowDialog();
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