using System;
using System.Linq;
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

            // 应用主题
            ApplyTheme(AppSettings.ThemeMode);

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

        #region 主题切换

        private static string _currentThemeMode = "Dark";

        /// <summary>
        /// 应用主题模式切换（Dark/Light/System）
        /// </summary>
        public static void ApplyTheme(string themeMode)
        {
            _currentThemeMode = themeMode;

            bool useLightTheme = false;
            if (string.Equals(themeMode, "System", StringComparison.OrdinalIgnoreCase))
            {
                useLightTheme = IsSystemLightTheme();
            }
            else if (string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase))
            {
                useLightTheme = true;
            }

            string themeUri = useLightTheme
                ? "Themes/LightTheme.xaml"
                : "Themes/DarkTheme.xaml";

            var mergedDicts = Current.Resources.MergedDictionaries;

            // 查找并移除当前主题字典
            var existingThemeDict = mergedDicts.FirstOrDefault(d =>
                d.Source != null && d.Source.OriginalString.Contains("Theme.xaml"));

            if (existingThemeDict != null)
            {
                if (existingThemeDict.Source.OriginalString == themeUri)
                    return; // 已经是目标主题

                mergedDicts.Remove(existingThemeDict);
            }

            mergedDicts.Add(new ResourceDictionary { Source = new Uri(themeUri, UriKind.Relative) });

            // 同步更新 AppSettings 颜色属性（桥接旧绑定）
            SyncThemeToAppSettings(useLightTheme);

            // 更新所有窗口 DWM 标题栏主题
            UpdateAllWindowDwmThemes(useLightTheme);
        }

        /// <summary>
        /// 检测系统是否为浅色主题
        /// </summary>
        private static bool IsSystemLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return value is int i && i == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 更新所有窗口的 DWM 深色/浅色标题栏属性
        /// </summary>
        private static void UpdateAllWindowDwmThemes(bool useLightTheme)
        {
            foreach (Window window in Current.Windows)
            {
                UpdateWindowDwmTheme(window, useLightTheme);
            }
        }

        /// <summary>
        /// 获取当前主题是否为浅色模式（考虑 System 模式）
        /// </summary>
        public static bool IsCurrentThemeLight()
        {
            if (string.Equals(_currentThemeMode, "System", StringComparison.OrdinalIgnoreCase))
                return IsSystemLightTheme();
            return string.Equals(_currentThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 使用当前主题设置窗口 DWM 标题栏
        /// </summary>
        public static void UpdateWindowDwmTheme(Window window)
        {
            UpdateWindowDwmTheme(window, IsCurrentThemeLight());
        }

        /// <summary>
        /// 设置单个窗口的 DWM 标题栏深色模式属性
        /// </summary>
        public static void UpdateWindowDwmTheme(Window window, bool useLightTheme)
        {
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                int useDarkMode = useLightTheme ? 0 : 1;
                NativeMethods.DwmSetWindowAttribute(handle,
                    NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref useDarkMode, sizeof(int));

                // 强制刷新窗口帧以立即应用标题栏颜色
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE |
                    SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_FRAMECHANGED);
            }
            catch { /* ignore DWM failures */ }
        }

        /// <summary>
        /// 将当前主题色同步到 AppSettings.Instance 供旧绑定使用
        /// </summary>
        private static void SyncThemeToAppSettings(bool useLightTheme)
        {
            try
            {
                var settings = Framework.AppSettings.Instance;
                if (useLightTheme)
                {
                    settings.BGColor = "#F9FAFB";
                    settings.FontColor = "#111827";
                    settings.GroupBGColor = "#F3F4F6";
                }
                else
                {
                    settings.BGColor = "#161616";
                    settings.FontColor = "#FFFFFF";
                    settings.GroupBGColor = "#252525";
                }
            }
            catch { /* 如果 AppSettings 不存在属性则忽略 */ }
        }

        #endregion
    }
}
