using System;
using System.Collections.Concurrent;
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
        public LayoutMode CurrentLayout => AppSettings.GetCurrentMonitor(0, GetCurrentDesktopId()).Layout;
        public Settings AppSettings { get; private set; } = new();
        public WindowConfigManager WindowConfigs { get; private set; } = new();
        public ProgramRegistryManager ProgramRegistry { get; private set; } = new();
        public ExtensionManager Extensions { get; private set; } = new();
        public Services.VirtualDesktopService? VirtualDesktop { get; private set; }

        public string ScreenAUrl { get; set; } = "https://www.baidu.com";
        public string ScreenBUrl { get; set; } = "https://www.bing.com";

        private readonly System.Collections.Generic.List<MainWindow> _windows = new();
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        /// <summary>记住每个(桌面ID, 显示器索引, 停靠边)对应的 ScreenType，切换虚拟桌面后恢复</summary>
        private readonly ConcurrentDictionary<(Guid desktopId, int monitor, DockEdge side), ScreenType> _screenMemory = new();

        /// <summary>主题变更事件</summary>
        public static event Action? ThemeChanged;

        /// <summary>fetchResult 回调注册表：requestId → 回调委托</summary>
        private readonly ConcurrentDictionary<string, Action<bool, string, string>> _fetchCallbacks = new();

        /// <summary>
        /// 注册一个一次性 fetchResult 回调，匹配指定 requestId。
        /// 回调参数：(success, value, error)
        /// </summary>
        public void RegisterFetchCallback(string requestId, Action<bool, string, string> callback)
        {
            _fetchCallbacks[requestId] = callback;
        }

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

            // 首次启动（无任何显示器配置）：默认主显示器左侧停靠，保持旧默认行为
            if (AppSettings.Monitors.Count == 0)
                AppSettings.GetMonitor(0).Layout = LayoutMode.Left;

            // 初始化虚拟桌面服务
            try
            {
                VirtualDesktop = new Services.VirtualDesktopService();
                if (VirtualDesktop.IsAvailable)
                    VirtualDesktop.DesktopSwitched += OnVirtualDesktopSwitched;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] 初始化异常: {ex.Message}");
            }

            ApplyLayout(save: false);

            // 初始化系统托盘图标
            InitNotifyIcon();

            // 启动本地 WebSocket 服务端 (127.0.0.1:19191)
            try
            {
                WebSocketServerService.Instance.MessageReceived += OnWebSocketMessageReceived;
                WebSocketServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocketServer] 启动异常: {ex.Message}");
            }

            // 启动 MCP Server (127.0.0.1:19192)，仅在开启时
            try
            {
                if (AppSettings.EnableMcpServer)
                    Services.McpServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpServer] 启动异常: {ex.Message}");
            }

            // 启动全局窗口切换热键（Ctrl+1~9），仅在开启时
            try
            {
                if (AppSettings.EnableWindowSwitchHotkey)
                    GlobalHotkeyService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] 启动异常: {ex.Message}");
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

            // 启动云同步（仅在已登录且已开启时）
            try
            {
                if (AppSettings.EnableCloudSync && !string.IsNullOrEmpty(AppSettings.AccountToken))
                    Services.CloudSyncService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 启动异常: {ex.Message}");
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
        /// 全局布局切换（菜单入口）：作用于主显示器（下标 0）。
        /// </summary>
        public void SwitchLayout(LayoutMode mode)
        {
            var desktopId = GetCurrentDesktopId();
            AppSettings.GetCurrentMonitor(0, desktopId).Layout = mode;
            ApplyLayout(save: true);
        }

        /// <summary>
        /// 指定显示器的布局切换（供单个窗口菜单使用）。
        /// </summary>
        public void SwitchLayoutForMonitor(int monitorIndex, LayoutMode mode)
        {
            var desktopId = GetCurrentDesktopId();
            AppSettings.GetCurrentMonitor(monitorIndex, desktopId).Layout = mode;
            ApplyLayout(save: true);
        }

        /// <summary>
        /// 获取当前活动虚拟桌面 ID（未启用或未可用时返回 Guid.Empty）
        /// </summary>
        public Guid GetCurrentDesktopId()
        {
            if (!AppSettings.EnablePerVirtualDesktop || VirtualDesktop == null || !VirtualDesktop.IsAvailable)
                return Guid.Empty;
            return VirtualDesktop.GetCurrentDesktopId();
        }

        /// <summary>
        /// 根据每个显示器的停靠配置重建所有停靠窗口。
        /// 支持按当前虚拟桌面选择对应的停靠配置。
        /// 切换虚拟桌面时会记住并恢复每个桌面的 ScreenType 选择。
        /// </summary>
        public void ApplyLayout(bool save)
        {
            var desktopId = GetCurrentDesktopId();

            // 关闭前记住每个窗口当前的 ScreenType
            foreach (var w in _windows)
            {
                var key = (desktopId, w.ScreenIndex, w.DockSide);
                _screenMemory[key] = w.CurrentScreen;
            }

            // 关闭并释放现有窗口
            foreach (var w in _windows.ToList())
                CloseWindow(w);
            _windows.Clear();

            int monitorCount = MonitorEnumerator.GetMonitorsPrimaryFirst().Count;
            if (monitorCount <= 0) monitorCount = 1;

            for (int i = 0; i < monitorCount; i++)
            {
                var cfg = AppSettings.GetCurrentMonitor(i, desktopId);
                switch (cfg.Layout)
                {
                    case LayoutMode.Left:
                        _windows.Add(CreatePanel(DockEdge.Left, i, desktopId));
                        break;
                    case LayoutMode.Right:
                        _windows.Add(CreatePanel(DockEdge.Right, i, desktopId));
                        break;
                    case LayoutMode.Both:
                        _windows.Add(CreatePanel(DockEdge.Left, i, desktopId));
                        _windows.Add(CreatePanel(DockEdge.Right, i, desktopId));
                        break;
                    case LayoutMode.None:
                    default:
                        break;
                }
            }

            if (save) SaveState();
        }

        /// <summary>
        /// 虚拟桌面切换回调：重新应用当前桌面的停靠布局。
        /// 仅在开启"虚拟桌面独立配置"时才调整窗口，否则保持现有窗口不变。
        /// </summary>
        private void OnVirtualDesktopSwitched(Guid newDesktopId)
        {
            if (!AppSettings.EnablePerVirtualDesktop) return;

            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[App] 虚拟桌面切换，重新应用布局: {newDesktopId}");
                ApplyLayout(save: false);
            });
        }

        /// <summary>
        /// 保存当前状态
        /// </summary>
        public void SaveState()
        {
            AppSettings.Save();
        }

        /// <summary>
        /// 记住指定窗口的 ScreenType，用于虚拟桌面切换后恢复
        /// </summary>
        public void RememberScreen(int monitorIndex, DockEdge side, ScreenType screen)
        {
            var desktopId = GetCurrentDesktopId();
            _screenMemory[(desktopId, monitorIndex, side)] = screen;
        }

        /// <summary>重新渲染所有停靠窗口的原生面板（数据变更后调用）。</summary>
        public void RefreshAllNativePanels()
        {
            foreach (var w in _windows)
                w.RefreshNativePanels();
        }

        public void CloseAll()
        {
            SaveState();
            WindowConfigs.Save();
            foreach (var w in _windows.ToList())
                CloseWindow(w);
            _windows.Clear();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 释放托盘图标
            try { _notifyIcon?.Dispose(); } catch { }

            // 停止本地 WebSocket 服务端
            try
            {
                WebSocketServerService.Instance.MessageReceived -= OnWebSocketMessageReceived;
                WebSocketServerService.Instance.Stop();
            }
            catch { }

            // 停止 MCP Server
            try { Services.McpServerService.Instance.Stop(); } catch { }

            // 停止全局窗口切换热键
            try { GlobalHotkeyService.Instance.Stop(); } catch { }

            // 停止虚拟桌面服务
            try { VirtualDesktop?.Dispose(); } catch { }

            // 退出前执行一次云同步备份（仅在开启且已登录时；放到线程池避免阻塞 UI 线程）
            try
            {
                if (AppSettings.EnableCloudSync && !string.IsNullOrEmpty(AppSettings.AccountToken))
                {
                    System.Threading.Tasks.Task.Run(() =>
                        Services.CloudSyncService.Instance.SyncNowAsync(silent: true))
                        .Wait(TimeSpan.FromSeconds(8));
                }
            }
            catch { }

            // 【已禁用】CDP浏览器服务 - 2026-06-09 禁用，解决重复启动问题
            // try { Services.CdpBrowserService.Instance.Dispose(); } catch { }
            base.OnExit(e);
        }

        /// <summary>
        /// 收到浏览器扩展通过 WebSocket 回传的消息。
        /// fetchResult 类型通过回调更新数据看板 UI，register 类型更新客户端元数据，其他类型弹窗提示。
        /// </summary>
        private void OnWebSocketMessageReceived(Guid clientId, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[App] ★★★ 收到WS消息[{clientId}]: {message}");
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(message);
                    var msgType = (string?)obj["type"];

                    if (msgType == "fetchResult")
                    {
                        var success = (bool?)obj["success"] ?? false;
                        var label = (string?)obj["label"] ?? "";
                        var value = (string?)obj["value"] ?? "";
                        var url = (string?)obj["url"] ?? "";
                        var requestId = (string?)obj["requestId"] ?? "";
                        var error = (string?)obj["error"] ?? "";

                        System.Diagnostics.Debug.WriteLine($"[App] fetchResult: success={success}, label={label}, value={value}, requestId={requestId}");

                        // 回调给注册的 UI 组件（更新数据看板值）
                        if (!string.IsNullOrEmpty(requestId) && _fetchCallbacks.TryRemove(requestId, out var cb))
                        {
                            try { cb(success, value, error); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] fetchCallback异常: {ex.Message}"); }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] 未找到requestId对应的回调: {requestId}");
                        }

                        return; // fetchResult 不弹窗，由回调更新UI
                    }

                    if (msgType == "register")
                    {
                        var uniqId = (string?)obj["uniqid"] ?? "";
                        var title = (string?)obj["title"] ?? "";
                        var url = (string?)obj["url"] ?? "";
                        int? tabId = obj["tabId"] != null ? (int?)obj["tabId"] : null;
                        Services.WebSocketServerService.Instance.UpdateClientInfo(clientId, uniqId, title, url, tabId);
                        System.Diagnostics.Debug.WriteLine($"[App] register: uniqid={uniqId}, title={title}, url={url}, tabId={tabId}");
                        return; // register 不弹窗
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] WS消息解析异常: {ex.Message}"); }

                // 非 fetchResult/register 消息弹窗提示
                MessageBox.Show(message, "收到扩展消息", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>
        /// 向指定 WebSocket 客户端发送关闭标签页命令
        /// </summary>
        public async void CloseWsClientTab(Guid clientId)
        {
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = "closeTab" });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, payload);
        }

        /// <summary>
        /// 全局热键触发：切换当前活动 MainWindow 的窗口标签到指定序号。
        /// 优先前台的 MainWindow，其次左侧，其次右侧。
        /// </summary>
        public void SwitchWindowTabByHotkey(int index)
        {
            MainWindow? target = null;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            foreach (Window w in Windows)
            {
                if (w is MainWindow mw)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
                    if (h == fg) { target = mw; break; }
                }
            }
            target ??= _windows.FirstOrDefault();
            target?.SwitchTabByHotkey(index);
        }

        private MainWindow CreatePanel(DockEdge side, int monitorIndex, Guid desktopId)
        {
            // 恢复记忆的 ScreenType，如果之前在该桌面+显示器+侧边选择过则恢复，否则默认 A
            var key = (desktopId, monitorIndex, side);
            var screen = _screenMemory.GetValueOrDefault(key, ScreenType.A);

            var window = new MainWindow(side, screen, monitorIndex);
            // 按停靠边取该显示器各自的停靠宽度：左侧用 LeftPanelWidth，右侧用 RightPanelWidth
            var cfg = AppSettings.GetCurrentMonitor(monitorIndex, desktopId);
            window.AppBarWidth = side == DockEdge.Left
                ? cfg.LeftPanelWidth
                : cfg.RightPanelWidth;
            window.SetWindowConfigManager(WindowConfigs);
            window.Show();
            return window;
        }

        private void CloseWindow(MainWindow? window)
        {
            if (window != null)
            {
                window.UnregisterAppBar();
                window.Close();
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
        /// 应用各显示器每侧的停靠宽度。
        /// 直接更新现有窗口的 AppBarWidth 并重新定位，避免销毁重建窗口（
        /// 重建会连带关闭作为设置窗口 Owner 的主窗口，导致宽度不生效）。
        /// </summary>
        public void ApplyPanelWidths()
        {
            var desktopId = GetCurrentDesktopId();
            foreach (var w in _windows)
            {
                var cfg = AppSettings.GetCurrentMonitor(w.ScreenIndex, desktopId);
                w.AppBarWidth = w.DockSide == DockEdge.Left
                    ? cfg.LeftPanelWidth
                    : cfg.RightPanelWidth;
                w.PositionAppBar();
            }
        }

        /// <summary>
        /// 热重载悬浮配置：遍历现存所有停靠窗口，各自重新读取悬浮设置
        /// </summary>
        public void ReloadHoverSettings()
        {
            foreach (var w in _windows)
                w.ReloadHoverConfig();
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

            // 查找并移除当前主题字典（只匹配 DarkTheme.xaml 或 LightTheme.xaml）
            var existingThemeDict = mergedDicts.FirstOrDefault(d =>
                d.Source != null && (
                    d.Source.OriginalString.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                    d.Source.OriginalString.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                ));

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

            // 通知主题已变更
            ThemeChanged?.Invoke();
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

        /// <summary>
        /// 打开设置窗口（供托盘右键菜单使用）。
        /// 若已有打开的设置窗口则激活；无停靠窗口时也能独立弹出。
        /// </summary>
        public void OpenSettings()
        {
            foreach (Window w in Windows)
            {
                if (w is SettingsWindow existing)
                {
                    existing.Show();
                    existing.Activate();
                    return;
                }
            }

            var settings = new SettingsWindow();
            settings.Show();
            settings.Activate();
        }

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
                contextMenu.Items.Add("设置", null, (s, e) => OpenSettings());
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("退出 元桌小屏", null, (s, e) => CloseAll());

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = appIcon ?? System.Drawing.SystemIcons.Application,
                    Text = "元桌小屏",
                    Visible = true,
                    ContextMenuStrip = contextMenu
                };

                // 双击托盘图标激活主窗口
                _notifyIcon.DoubleClick += (s, e) =>
                {
                    _windows.FirstOrDefault()?.Activate();
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
