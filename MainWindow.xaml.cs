using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        public ScreenType CurrentScreen => _currentScreen;


        public MainWindow(DockEdge side, ScreenType screen)
        {
            InitializeComponent();

            _currentScreen = screen;

            // 配置 AppBar
            DockSide = side;
            AppBarWidth = 105;
            ScreenIndex = 0;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterAppBar();
            UpdateTitle();

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

                // 当前dock只有1个窗口时隐藏"关闭窗口"及其前面的分隔符
                var showClose = _dockState.WindowIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                closeWindowMenu.Visibility = showClose;
                closeWindowSeparator.Visibility = showClose;
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow();
            settingsWin.Owner = this;
            settingsWin.ShowDialog();
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_dockState == null) return;
            RemoveWindow(_dockState.ActiveIndex);
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

            for (int i = 0; i < _dockState.WindowIds.Count; i++)
            {
                var winId = _dockState.WindowIds[i];
                var config = _configManager.GetById(winId);
                if (config == null) continue;

                var index = i;
                var btn = new Button
                {
                    Content = $"[{i + 1}]",
                    ToolTip = config.Name,
                    Tag = index,
                    Margin = new Thickness(1, 0, 1, 0),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = (i == _dockState.ActiveIndex)
                        ? new SolidColorBrush(Color.FromRgb(80, 120, 200))
                        : Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0)
                };
                btn.Click += (s, e) => WindowTab_Click(index);

                // 右键删除
                var ctx = new ContextMenu();
                var deleteItem = new MenuItem { Header = "删除此窗口" };
                deleteItem.Click += (s, e) => RemoveWindow(index);
                ctx.Items.Add(deleteItem);
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
            RefreshWindowTabs();
        }

        private void AddWindow_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show($"拖入了 {files.Length} 个文件:\n{string.Join("\n", files)}",
                    "SideDesk", MessageBoxButton.OK, MessageBoxImage.Information);
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