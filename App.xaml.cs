using System.Windows;
using SideDesk.Models;

namespace SideDesk
{
    public enum ScreenType { A, B }

    public partial class App : Application
    {
        public LayoutMode CurrentLayout { get; private set; } = LayoutMode.Left;
        public Settings AppSettings { get; private set; } = new();
        public WindowConfigManager WindowConfigs { get; private set; } = new();

        public string ScreenAUrl { get; set; } = "https://www.baidu.com";
        public string ScreenBUrl { get; set; } = "https://www.bing.com";

        private MainWindow? _leftWindow;
        private MainWindow? _rightWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 加载上次保存的设置
            AppSettings = Settings.Load();

            // 加载窗口配置
            WindowConfigs = WindowConfigManager.Load();
            WindowConfigs.EnsureDefault();

            SwitchLayout(AppSettings.Layout, AppSettings.LeftScreen, AppSettings.RightScreen, save: false);
        }

        public string GetUrlForScreen(ScreenType screen)
        {
            return screen == ScreenType.A ? ScreenAUrl : ScreenBUrl;
        }

        /// <summary>
        /// 切换布局模式
        /// </summary>
        public void SwitchLayout(LayoutMode mode)
        {
            // 默认屏幕分配：单窗口显示 A，双侧左A右B
            ScreenType left = ScreenType.A;
            ScreenType right = mode == LayoutMode.Both ? ScreenType.B : ScreenType.A;
            SwitchLayout(mode, left, right, save: true);
        }

        public void SwitchLayout(LayoutMode mode, ScreenType leftScreen, ScreenType rightScreen, bool save)
        {
            CloseWindow(ref _leftWindow);
            CloseWindow(ref _rightWindow);

            CurrentLayout = mode;

            switch (mode)
            {
                case LayoutMode.Left:
                    _leftWindow = CreatePanel(DockEdge.Left, leftScreen);
                    break;

                case LayoutMode.Right:
                    _rightWindow = CreatePanel(DockEdge.Right, rightScreen);
                    break;

                case LayoutMode.Both:
                    _leftWindow = CreatePanel(DockEdge.Left, leftScreen);
                    _rightWindow = CreatePanel(DockEdge.Right, rightScreen);
                    break;
            }

            if (save) SaveState();
        }

        /// <summary>
        /// 保存当前状态
        /// </summary>
        public void SaveState()
        {
            AppSettings.Layout = CurrentLayout;
            AppSettings.LeftScreen = _leftWindow?.CurrentScreen ?? ScreenType.A;
            AppSettings.RightScreen = _rightWindow?.CurrentScreen ?? ScreenType.B;
            AppSettings.Save();
        }

        public void CloseAll()
        {
            SaveState();
            WindowConfigs.Save();
            CloseWindow(ref _leftWindow);
            CloseWindow(ref _rightWindow);
            Shutdown();
        }

        private MainWindow CreatePanel(DockEdge side, ScreenType screen)
        {
            var window = new MainWindow(side, screen);
            window.AppBarWidth = AppSettings.PanelWidth;
            window.SetWindowConfigManager(WindowConfigs);
            window.Show();
            return window;
        }

        private void CloseWindow(ref MainWindow? window)
        {
            if (window != null)
            {
                window.UnregisterAppBar();
                window.Close();
                window = null;
            }
        }

        /// <summary>
        /// 应用新的面板宽度（重新注册 AppBar）
        /// </summary>
        public void ApplyPanelWidth(int width)
        {
            AppSettings.PanelWidth = width;
            // 重新切换布局以应用新宽度
            SwitchLayout(CurrentLayout, 
                _leftWindow?.CurrentScreen ?? ScreenType.A, 
                _rightWindow?.CurrentScreen ?? ScreenType.B, 
                save: true);
        }
    }
}
