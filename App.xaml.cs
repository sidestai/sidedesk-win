using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SideDesk.Models;
using SideDesk.Services;
using Velopack;

namespace SideDesk
{
    public enum ScreenType { A, B }

    public partial class App : Application
    {
        public LayoutMode CurrentLayout { get; private set; } = LayoutMode.Left;
        public Settings AppSettings { get; private set; } = new();
        public WindowConfigManager WindowConfigs { get; private set; } = new();
        public ProgramRegistryManager ProgramRegistry { get; private set; } = new();
        public ExtensionManager Extensions { get; private set; } = new();

        public string ScreenAUrl { get; set; } = "https://www.baidu.com";
        public string ScreenBUrl { get; set; } = "https://www.bing.com";

        private MainWindow? _leftWindow;
        private MainWindow? _rightWindow;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 注册全局异常处理
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            // Velopack 必须最先执行，拦截安装/更新/卸载命令行钩子
            try { System.IO.Directory.SetCurrentDirectory(System.IO.Path.GetTempPath()); } catch { }
            VelopackApp.Build().Run();

            base.OnStartup(e);

            // 清理残留 AppBar：杀掉其他 SideDesk 进程
            CleanupStaleAppBar();

            // 加载上次保存的设置
            AppSettings = Settings.Load();

            // 加载窗口配置
            WindowConfigs = WindowConfigManager.Load();
            WindowConfigs.EnsureDefault();

            // 加载程序注册表
            ProgramRegistry = ProgramRegistryManager.Load();

            // 加载扩展管理器
            Extensions = ExtensionManager.Load();

            // 应用主题
            ApplyTheme(AppSettings.ThemeMode);

            SwitchLayout(AppSettings.Layout, AppSettings.LeftScreen, AppSettings.RightScreen, save: false);

            // 初始化系统托盘图标
            InitNotifyIcon();

            // 启动本地 WebSocket 服务端 (127.0.0.1:19191)
            try
            {
                WebSocketServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocketServer] 启动异常: {ex.Message}");
            }

            // 启动时恢复上次保存的标题锁定规则（仅在功能开启时）
            try
            {
                if (AppSettings.EnableTitleLock)
                {
                    int restored = TaskbarOverlayManager.RestoreLocksFromRules();
                    System.Diagnostics.Debug.WriteLine($"[TitleLock] 启动恢复锁定规则，命中 {restored} 个窗口");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TitleLock] 启动恢复锁定规则失败: {ex.Message}");
            }

            // 【已禁用】启动 5 秒后静默检查更新 - 2026-06-09 禁用，解决程序自动重启问题
            // 如果以后需要启用，请取消下面的注释
            //_ = Task.Delay(5000).ContinueWith(async _ =>
            //{
            //    try
            //    {
            //        await VelopackUpdateService.Instance.StartSilentUpdateCheckAndDownloadAsync();
            //    }
            //    catch (Exception ex)
            //    {
            //        System.Diagnostics.Debug.WriteLine($"Silent update check error: {ex.Message}");
            //    }
            //}, TaskScheduler.Default);
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

        protected override void OnExit(ExitEventArgs e)
        {
            // 释放托盘图标
            try { _notifyIcon?.Dispose(); } catch { }

            // 停止本地 WebSocket 服务端
            try { WebSocketServerService.Instance.Stop(); } catch { }

            // 【已禁用】CDP浏览器服务 - 2026-06-09 禁用，解决重复启动问题
            // try { Services.CdpBrowserService.Instance.Dispose(); } catch { }
            base.OnExit(e);
        }

        private MainWindow CreatePanel(DockEdge side, ScreenType screen)
        {
            var window = new MainWindow(side, screen);
            // 按停靠边取各自的停靠宽度：左侧用 LeftPanelWidth，右侧用 RightPanelWidth
            window.AppBarWidth = side == DockEdge.Left
                ? AppSettings.LeftPanelWidth
                : AppSettings.RightPanelWidth;
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
        /// 清理崩溃残留的 AppBar 区域：终止其他同名进程 + 重置工作区
        /// </summary>
        private static void CleanupStaleAppBar()
        {
            try
            {
                var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var staleProcesses = System.Diagnostics.Process.GetProcessesByName("SideDesk")
                    .Where(p => p.Id != currentPid)
                    .ToList();

                if (staleProcesses.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppBar] 检测到 {staleProcesses.Count} 个残留 SideDesk 进程，正在清理...");
                    foreach (var p in staleProcesses)
                    {
                        try { p.Kill(); p.WaitForExit(3000); }
                        catch { }
                        finally { p.Dispose(); }
                    }

                    // 等待系统释放 AppBar 区域
                    System.Threading.Thread.Sleep(500);

                    // 重置所有显示器的工作区为完整区域
                    ResetWorkArea();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppBar] 清理残留失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置显示器工作区为完整屏幕区域（恢复被残留 AppBar 占用的空间）
        /// </summary>
        private static void ResetWorkArea()
        {
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData) =>
                    {
                        var monitorInfo = new MONITORINFO();
                        monitorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>();
                        if (NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
                        {
                            var fullRect = monitorInfo.rcMonitor;
                            NativeMethods.SystemParametersInfo(
                                NativeMethods.SPI_SETWORKAREA, 0, ref fullRect, NativeMethods.SPIF_SENDCHANGE);
                            System.Diagnostics.Debug.WriteLine(
                                $"[AppBar] 重置工作区: L={fullRect.Left} T={fullRect.Top} R={fullRect.Right} B={fullRect.Bottom}");
                        }
                        return true;
                    }, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppBar] 重置工作区失败: {ex.Message}");
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

        /// <summary>
        /// 应用左右两侧各自的停靠宽度。
        /// 直接更新现有窗口的 AppBarWidth 并重新定位，避免销毁重建窗口（
        /// 重建会连带关闭作为设置窗口 Owner 的主窗口，导致宽度不生效）。
        /// </summary>
        public void ApplyPanelWidths()
        {
            if (_leftWindow != null)
            {
                _leftWindow.AppBarWidth = AppSettings.LeftPanelWidth;
                _leftWindow.PositionAppBar();
            }
            if (_rightWindow != null)
            {
                _rightWindow.AppBarWidth = AppSettings.RightPanelWidth;
                _rightWindow.PositionAppBar();
            }
        }

        /// <summary>
        /// 热重载悬浮配置：遍历现存左右窗口，各自重新读取悬浮设置
        /// </summary>
        public void ReloadHoverSettings()
        {
            _leftWindow?.ReloadHoverConfig();
            _rightWindow?.ReloadHoverConfig();
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
        
        #region 全局异常处理
        
        /// <summary>
        /// 未观察到的任务异常
        /// </summary>
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("TaskScheduler_UnobservedTaskException", e.Exception);
            e.SetObserved(); // 阻止程序崩溃
        }
        
        /// <summary>
        /// UI线程未处理异常
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandledException", e.Exception);
            e.Handled = true; // 阻止程序崩溃
            
            MessageBox.Show(
                $"发生错误：{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "程序异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        
        /// <summary>
        /// 全局未处理异常（会导致程序退出）
        /// </summary>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogCrash("UnhandledException", ex);
                
                // 记录到崩溃日志
                string crashLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk",
                    $"crash-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
                
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
                    File.WriteAllText(crashLog, $"""
                        崩溃时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                        异常类型: {ex.GetType().FullName}
                        异常消息: {ex.Message}
                        堆栈跟踪:
                        {ex.StackTrace}
                        
                        InnerException: {ex.InnerException?.ToString() ?? "无"}
                        
                        是否终止: {e.IsTerminating}
                        """);
                }
                catch { }
            }
        }
        
        /// <summary>
        /// 记录崩溃信息
        /// </summary>
        private void LogCrash(string source, Exception ex)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logLine = $"""
                [{timestamp}] [CRASH] {source}
                类型: {ex.GetType().FullName}
                消息: {ex.Message}
                堆栈: {ex.StackTrace}
                
                """;
            
            System.Diagnostics.Debug.WriteLine(logLine);
            
            // 写入日志文件
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk");
                Directory.CreateDirectory(logDir);
                
                string crashLog = Path.Combine(logDir, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(crashLog, logLine);
            }
            catch { }
        }
        
        #endregion

        #region 系统托盘图标

        /// <summary>初始化系统托盘图标（右键菜单含退出选项）</summary>
        private void InitNotifyIcon()
        {
            try
            {
                // 获取应用程序图标
                System.Drawing.Icon? appIcon = null;
                try
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
                catch { }

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("退出 SideDesk", null, (s, e) => CloseAll());

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = appIcon ?? System.Drawing.SystemIcons.Application,
                    Text = "SideDesk",
                    Visible = true,
                    ContextMenuStrip = contextMenu
                };

                // 双击托盘图标激活主窗口
                _notifyIcon.DoubleClick += (s, e) =>
                {
                    if (_leftWindow != null)
                        _leftWindow.Activate();
                    else if (_rightWindow != null)
                        _rightWindow.Activate();
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotifyIcon] 初始化失败: {ex.Message}");
            }
        }

        #endregion
    }
}
