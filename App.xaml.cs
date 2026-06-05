using System.Windows;

namespace SideDesk
{
    public enum ScreenType { A, B }

    public partial class App : Application
    {
        public LayoutMode CurrentLayout { get; private set; } = LayoutMode.Left;

        /// <summary>
        /// A屏网址（默认屏）
        /// </summary>
        public string ScreenAUrl { get; set; } = "https://www.baidu.com";

        /// <summary>
        /// B屏网址
        /// </summary>
        public string ScreenBUrl { get; set; } = "https://www.bing.com";

        private MainWindow? _leftWindow;
        private MainWindow? _rightWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SwitchLayout(LayoutMode.Left);
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
            CloseWindow(ref _leftWindow);
            CloseWindow(ref _rightWindow);

            CurrentLayout = mode;

            switch (mode)
            {
                case LayoutMode.Left:
                    _leftWindow = CreatePanel(DockEdge.Left, ScreenType.A);
                    break;

                case LayoutMode.Right:
                    _rightWindow = CreatePanel(DockEdge.Right, ScreenType.A);
                    break;

                case LayoutMode.Both:
                    _leftWindow = CreatePanel(DockEdge.Left, ScreenType.A);
                    _rightWindow = CreatePanel(DockEdge.Right, ScreenType.B);
                    break;
            }
        }

        public void CloseAll()
        {
            CloseWindow(ref _leftWindow);
            CloseWindow(ref _rightWindow);
            Shutdown();
        }

        private MainWindow CreatePanel(DockEdge side, ScreenType screen)
        {
            var window = new MainWindow(side, screen);
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
    }
}
