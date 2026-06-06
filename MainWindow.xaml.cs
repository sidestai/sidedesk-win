using System;
using System.Windows;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using SideDesk.Controls;

namespace SideDesk
{
    public partial class MainWindow : AppBarWindow
    {
        private ScreenType _currentScreen;

        public ScreenType CurrentScreen => _currentScreen;

        /// <summary>
        /// 当前显示模式
        /// </summary>
        public DisplayMode CurrentDisplayMode => sidebarPanel.CurrentMode;

        /// <summary>
        /// 保存显示模式到设置
        /// </summary>
        public void SaveDisplayMode()
        {
            // TODO: 保存到 Settings
            Debug.WriteLine($"[MainWindow] 显示模式: {sidebarPanel.CurrentMode}");
        }

        public MainWindow(DockEdge side, ScreenType screen)
        {
            InitializeComponent();

            _currentScreen = screen;

            // 配置 AppBar
            DockSide = side;
            AppBarWidth = 205;
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).CloseAll();
        }

        private void ToggleMode_Click(object sender, RoutedEventArgs e)
        {
            if (sidebarPanel.CurrentMode == DisplayMode.WebView)
            {
                sidebarPanel.SwitchToNativeMode();
            }
            else
            {
                sidebarPanel.SwitchToWebViewMode();
            }
            SaveDisplayMode();
        }

        #region 屏幕切换

        private void SwitchToScreenA(object sender, RoutedEventArgs e)
        {
            _currentScreen = ScreenType.A;
            UpdateTitle();
            NavigateToCurrentScreen();
            ((App)Application.Current).SaveState();
        }

        private void SwitchToScreenB(object sender, RoutedEventArgs e)
        {
            _currentScreen = ScreenType.B;
            UpdateTitle();
            NavigateToCurrentScreen();
            ((App)Application.Current).SaveState();
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