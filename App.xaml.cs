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
        public Services.VirtualDesktopService? VirtualDesktop { get; private set; }

        /// <summary>启动时 Token 验证失败的提示信息（非空表示登录已过期，设置页显示提示）</summary>
        public static string? AccountTokenExpiredMessage { get; set; }

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

            // 旧日志文件迁移到统一的 Logs 目录（需在各类日志器写入前执行）
            MigrateLogsToLogsFolder();

            // 网络请求日志开关（默认开启；日志写入 %LocalAppData%\SideDesk\Logs\http.log）
            Services.HttpLog.Enabled = AppSettings.HttpLogEnabled;

            // 后台预计算设备唯一 ID（CPU ID + MAC 指纹）：WMI 查询较慢，放线程池避免阻塞启动
            _ = Task.Run(() => { var _ = Services.DeviceIdService.DeviceId; });

            // 加载窗口配置
            WindowConfigs = WindowConfigManager.Load();
            WindowConfigs.EnsureDefault();

            // 初始化数据轮询服务（中央数据源）
            Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);

            // 加载程序注册表
            ProgramRegistry = ProgramRegistryManager.Load();

            // 加载外部插件
            try
            {
                Services.Plugins.PluginLoaderService.Instance.LoadAllPlugins(AppSettings.DisabledPlugins);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PluginLoader] 启动加载异常: {ex.Message}");
            }

            // 应用主题
            ApplyTheme(AppSettings.ThemeMode);

            // 恢复用户上次选择的 JSON 自定义主题（若配置了且文件有效）
            if (!string.IsNullOrWhiteSpace(Framework.AppSettings.Instance.CurrentTheme))
            {
                Services.ThemeManager.RestoreFromName(Framework.AppSettings.Instance.CurrentTheme);
            }

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

            // 启动本地 WebSocket 服务端 (127.0.0.1:19191)，仅在浏览器插件模式下启动
            try
            {
                WebSocketServerService.Instance.MessageReceived += OnWebSocketMessageReceived;
                if (AppSettings.DataConnectionMode == "extension")
                    WebSocketServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocketServer] 启动异常: {ex.Message}");
            }

            // 订阅后端 WebSocket 消息（好友请求等通知），无论哪条登录路径都生效
            try
            {
                Services.BackendWebSocketService.Instance.MessageReceived -= OnBackendWsMessage;
                Services.BackendWebSocketService.Instance.MessageReceived += OnBackendWsMessage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackendWS] 订阅消息异常: {ex.Message}");
            }

            // 订阅通知服务事件：队列就绪的通知转发到所有已加载的主窗口渲染
            try
            {
                Services.NotificationService.Instance.NotificationReady -= OnNotificationReady;
                Services.NotificationService.Instance.NotificationReady += OnNotificationReady;
                Services.NotificationService.Instance.NotificationUpdated -= OnNotificationUpdated;
                Services.NotificationService.Instance.NotificationUpdated += OnNotificationUpdated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Notification] 订阅通知服务异常: {ex.Message}");
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

            // 同步服务端 MCP 工具注册表（登录态生效；未登录自动回退内置工具）
            try { _ = Services.McpToolRegistryService.Instance.SyncAsync(); }
            catch { }

            // 启动内置 HTTP Server (127.0.0.1:HttpServerPort)，默认开启
            try
            {
                if (AppSettings.EnableHttpServer)
                    Services.HttpServerService.Instance.Start(AppSettings.HttpServerPort);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpServer] 启动异常: {ex.Message}");
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

            // 启动窗口使用时长采集（仅在开启“使用时长分析”时）
            try
            {
                if (SideDesk.Framework.AppSettings.Instance.ShowUsageStats)
                    Services.WindowUsageService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowUsage] 启动异常: {ex.Message}");
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

            // 启动云同步（仅在已登录且已开启时；未开启时不做任何行级/结构同步）
            try
            {
                if (AppSettings.EnableCloudSync && !string.IsNullOrEmpty(AppSettings.AccountToken))
                    Services.CloudSyncService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 启动异常: {ex.Message}");
            }

            // 启动时验证 Token 有效性（延迟 2 秒，避免阻塞启动流程）
            _ = ValidateTokenOnStartupAsync();

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

        /// <summary>启动时验证 Token 有效性：调用后端 /auth/userinfo 接口，若服务器明确拒绝则清除本地 Token 并记录提示。</summary>
        private async Task ValidateTokenOnStartupAsync()
        {
            try
            {
                // 延迟 2 秒，避免与其他启动任务争抢网络
                await Task.Delay(2000);

                var token = AppSettings.AccountToken;
                if (string.IsNullOrEmpty(token))
                {
                    // 无 token 但曾经登录过（会员用户掉线状态）→ 显示橙色登录提示
                    if (AppSettings.HasLoggedInBefore)
                        ShowLoggedOutNotification();

                    // 未登录 → 以访客（设备）身份建立后端 WebSocket 连接，使本设备可接收“按设备/未登录用户”维度的通知
                    try { Services.BackendWebSocketService.Instance.Start(null); }
                    catch (Exception wsEx) { System.Diagnostics.Debug.WriteLine($"[BackendWS] 访客启动异常: {wsEx.Message}"); }
                    return;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, "http://localhost:3007/api/auth/userinfo");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = await Services.HttpLog.SendAsync(http, req);
                string body = await resp.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Auth] Token 验证响应 ({(int)resp.StatusCode}): {body}");

                // 后端统一返回 HTTP 200，真实状态码在 JSON 的 code 字段中
                int bizCode = 0;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    bizCode = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                }
                catch { /* JSON 解析失败时 bizCode 保持 0 */ }

                if (bizCode == 200)
                {
                    // Token 有效，清除过期提示，更新最后登录时间
                    AccountTokenExpiredMessage = null;
                    AppSettings.AccountLoginTime = DateTime.Now;
                    AppSettings.HasLoggedInBefore = true; // 确认会员身份
                    AppSettings.Save();

                    // 拉取用户详情（昵称、头像等）
                    await FetchAndSaveUserInfoAsync(token);

                    // Token 有效 → 启动后端 WebSocket 长连接（消息订阅已在 OnStartup 统一建立）
                    try { Services.BackendWebSocketService.Instance.Start(token); }
                    catch (Exception wsEx) { System.Diagnostics.Debug.WriteLine($"[BackendWS] 启动异常: {wsEx.Message}"); }

                    // 登录态有效 → 同步服务端 MCP 工具注册表
                    try { _ = Services.McpToolRegistryService.Instance.SyncAsync(); } catch { }
                    return;
                }

                // code=401/403 表示 Token 明确无效（过期/被踢）
                if (bizCode == 401 || bizCode == 403)
                {
                    System.Diagnostics.Debug.WriteLine($"[Auth] Token 验证失败 (code={bizCode})，清除本地登录状态");
                    AccountTokenExpiredMessage = "登录已过期，请重新登录";

                    // 清除本地登录状态
                    AppSettings.AccountToken = null;
                    AppSettings.AccountName = null;
                    AppSettings.AccountLoginTime = null;
                    AppSettings.EnableCloudSync = false;
                    AppSettings.SelectedCorpId = null;
                    AppSettings.SelectedCorpName = null;
                    AppSettings.Save();

                    // 停止云同步
                    try { Services.CloudSyncService.Instance.Stop(); } catch { }

                    // 登录态失效 → 注册表回退内置工具（隐私模式）
                    try { _ = Services.McpToolRegistryService.Instance.SyncAsync(); } catch { }

                    // 后端 WebSocket 降级为访客连接（设备仍未登录，保持可接收设备维度通知）
                    try { Services.BackendWebSocketService.Instance.Start(null); } catch { }

                    // 显示橙色掉线通知
                    ShowLoggedOutNotification();
                }
                // 其他 code（如 500、0=解析失败）不做处理，避免服务器临时故障时误清除
            }
            catch (Exception ex)
            {
                // 网络不可达等异常：不做任何处理（服务器可能未启动）
                System.Diagnostics.Debug.WriteLine($"[Auth] 启动时 Token 验证跳过: {ex.Message}");
            }
        }

        /// <summary>在所有主窗口上显示橙色掉线通知，点击后打开设置→账户页登录</summary>
        private void ShowLoggedOutNotification()
        {
            Dispatcher.Invoke(() =>
            {
                var orange = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E67E22"));
                orange.Freeze();
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.ShowNotification("⚠️ 您已掉线，点击这里登录", orange, () =>
                            OpenSettings(sw => sw.SwitchToAccountPage()), "logged-out");
                }
            });
        }

        /// <summary>关闭所有主窗口上的掉线登录提示（登录成功后调用）</summary>
        public void DismissLoggedOutNotification()
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.DismissNotificationByTag("logged-out");
                }
            });
        }

        /// <summary>调用后端 /auth/userinfo 接口获取用户详情（昵称、头像等）并保存到本地配置</summary>
        public async Task FetchAndSaveUserInfoAsync(string token)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, "http://localhost:3007/api/auth/userinfo");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = await Services.HttpLog.SendAsync(http, req);
                string body = await resp.Content.ReadAsStringAsync();

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                if (code != 200) return;
                if (!root.TryGetProperty("data", out var data)) return;

                bool changed = false;

                // 昵称
                string? nickName = data.TryGetProperty("nickName", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(nickName) && nickName != AppSettings.AccountName)
                {
                    AppSettings.AccountName = nickName;
                    changed = true;
                }

                // 头像（URL → 下载转 base64；base64 → 直接使用）
                string? avatarUrl = data.TryGetProperty("avatar", out var a) ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    if (avatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var imgBytes = await http.GetByteArrayAsync(avatarUrl);
                            if (imgBytes.Length > 0)
                            {
                                AppSettings.AccountAvatar = Convert.ToBase64String(imgBytes);
                                changed = true;
                            }
                        }
                        catch (Exception avatarEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Auth] 头像下载失败: {avatarEx.Message}");
                        }
                    }
                    else
                    {
                        // 兼容 base64 直存的头像
                        AppSettings.AccountAvatar = avatarUrl;
                        changed = true;
                    }
                }

                if (changed) AppSettings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] 获取用户详情失败: {ex.Message}");
            }
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
            DisposeAllWebView2(); // 先释放 WebView2，避免其事件循环阻止进程退出
            foreach (var w in _windows.ToList())
                CloseWindow(w);
            _windows.Clear();
            Shutdown();
        }

        /// <summary>释放所有打开窗口中的 WebView2 控件（必须在退出前调用，否则 WebView2 运行时会阻止进程结束）</summary>
        private void DisposeAllWebView2()
        {
            try
            {
                foreach (Window w in Windows)
                    DisposeWebView2InVisualTree(w);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 释放 WebView2 异常: {ex.Message}");
            }
        }

        private void DisposeWebView2InVisualTree(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is Microsoft.Web.WebView2.Wpf.WebView2 wv2)
                {
                    try { wv2.Dispose(); } catch { }
                }
                DisposeWebView2InVisualTree(child);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 兜底：确保所有 AppBar 窗口注销（正常路径 CloseAll 已处理，此处防御非正常退出路径）
            try
            {
                foreach (var w in _windows.ToList())
                    w.UnregisterAppBar();
                _windows.Clear();
            }
            catch { }

            // 释放托盘图标
            try { _notifyIcon?.Dispose(); } catch { }

            // 停止后端 WebSocket 客户端连接
            try { Services.BackendWebSocketService.Instance.Stop(); } catch { }

            // 停止本地 WebSocket 服务端
            try
            {
                WebSocketServerService.Instance.MessageReceived -= OnWebSocketMessageReceived;
                WebSocketServerService.Instance.Stop();
            }
            catch { }

            // 停止 MCP Server
            try { Services.McpServerService.Instance.Stop(); } catch { }

            // 停止内置 HTTP Server
            try { Services.HttpServerService.Instance.Stop(); } catch { }

            // 停止全局窗口切换热键
            try { GlobalHotkeyService.Instance.Stop(); } catch { }

            // 停止窗口使用时长采集
            try { Services.WindowUsageService.Instance.Stop(); } catch { }

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

            // 退出前恢复工作区：ABM_REMOVE 后 shell 的 WM_SETTINGCHANGE 广播是异步的，
            // 下方 Environment.Exit 会硬杀进程导致广播未完成，需显式刷新工作区
            try { RestoreWorkAreaOnExit(); } catch { }

            base.OnExit(e);

            // 强制结束进程：防止残留的前台线程 / WebView2 原生事件循环导致“僵尸进程”无法退出
            Environment.Exit(0);
        }

        /// <summary>
        /// 系统关机/注销时清理 AppBar，避免残留区域
        /// </summary>
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try
            {
                foreach (var w in _windows.ToList())
                    w.UnregisterAppBar();
                _windows.Clear();
                RestoreWorkAreaOnExit();
            }
            catch { }
            base.OnSessionEnding(e);
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

                    // ========== 插件编辑器桥接消息 ==========
                    if (msgType == "pluginBuild")
                    {
                        HandlePluginBuild(clientId, obj);
                        return;
                    }
                    if (msgType == "pluginList")
                    {
                        HandlePluginList(clientId);
                        return;
                    }
                    if (msgType == "pluginLoad")
                    {
                        HandlePluginLoad(clientId, obj);
                        return;
                    }
                    if (msgType == "pluginUnload")
                    {
                        HandlePluginUnload(clientId, obj);
                        return;
                    }
                    if (msgType == "pluginTest")
                    {
                        HandlePluginTest(obj);
                        return;
                    }
                    if (msgType == "pluginSave")
                    {
                        HandlePluginSave(clientId, obj);
                        return;
                    }
                    if (msgType == "pluginDevList")
                    {
                        HandlePluginDevList(clientId);
                        return;
                    }
                    if (msgType == "pluginDevLoad")
                    {
                        HandlePluginDevLoad(clientId, obj);
                        return;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] WS消息解析异常: {ex.Message}"); }

                // 非 fetchResult/register 消息弹窗提示
                MessageBox.Show(message, "收到扩展消息", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>
        /// 收到后端 WebSocket 推送的消息（好友请求、系统通知等）
        /// </summary>
        private void OnBackendWsMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[App] 后端WS消息: {message}");
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(message);
                var evt = (string?)obj["event"];
                var data = obj["data"] as Newtonsoft.Json.Linq.JObject;

                if (evt == "friend_request" && data != null)
                {
                    var fromName = (string?)data["fromName"] ?? "未知用户";
                    var requestId = (string?)data["requestId"] ?? "";

                    Dispatcher.Invoke(() =>
                    {
                        // 逐窗口隔离：某个停靠栏面板构建通知 UI 失败时不影响其它面板与设置页刷新
                        bool anyShown = false;
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            try
                            {
                                mw.ShowFriendRequestNotification(fromName, requestId);
                                anyShown = true;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] 主窗口显示好友请求通知失败: {ex}");
                                // 降级为简单气泡通知（不走富通知卡的模板/ToolTip 构建路径）
                                try { mw.ShowBubbleNotification($"👤 {fromName} 请求添加您为好友"); anyShown = true; } catch { }
                            }
                        }
                        // 设置窗口打开时同步刷新好友请求列表（避免只弹主窗口通知、设置页看不到的问题）
                        foreach (Window w in Windows)
                        {
                            try
                            {
                                if (w is SettingsWindow sw && sw.IsLoaded)
                                    sw.OnFriendRequestReceived();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] 设置窗口刷新好友请求失败: {ex}");
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"[BackendWS] friend_request 通知处理完成 anyShown={anyShown}");
                    });
                }
                else if (evt == "friend_online" && data != null)
                {
                    var userId = (string?)data["userId"] ?? "";
                    var userName = (string?)data["userName"] ?? "好友";
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            // 更新好友模块在线状态（按 encrypt2 密文匹配缓存项）
                            if (!string.IsNullOrEmpty(userId))
                                mw.UpdateFriendOnlineStatus(userId, true);
                            mw.ShowBubbleNotification($"🟢 {userName} 上线了");
                        }
                        // 设置窗口打开时同步刷新好友页的在线状态点
                        foreach (Window w in Windows)
                        {
                            try
                            {
                                if (w is SettingsWindow sw && sw.IsLoaded)
                                    sw.OnFriendStatusChanged();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] 设置窗口刷新好友在线状态失败: {ex}");
                            }
                        }
                    });
                }
                else if (evt == "friend_offline" && data != null)
                {
                    var userId = (string?)data["userId"] ?? "";
                    var userName = (string?)data["userName"] ?? "好友";
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            if (!string.IsNullOrEmpty(userId))
                                mw.UpdateFriendOnlineStatus(userId, false);
                            mw.ShowBubbleNotification($"⚪ {userName} 下线了");
                        }
                        foreach (Window w in Windows)
                        {
                            try
                            {
                                if (w is SettingsWindow sw && sw.IsLoaded)
                                    sw.OnFriendStatusChanged();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] 设置窗口刷新好友在线状态失败: {ex}");
                            }
                        }
                    });
                }
                else if (evt == "friend_profile_updated" && data != null)
                {
                    // 好友修改了资料/头像：更新本地好友缓存中的昵称与头像并刷新好友组件
                    var userId = (string?)data["userId"] ?? "";
                    var nickName = (string?)data["nickName"];
                    var avatar = (string?)data["avatar"];
                    if (!string.IsNullOrEmpty(userId))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            foreach (var mw in _windows)
                            {
                                if (mw.IsLoaded)
                                    mw.UpdateFriendProfile(userId, nickName, avatar);
                            }
                        });
                    }
                }
                else if (evt == "notification" && data != null)
                {
                    // 富通知推送：解析载荷并入队（NotificationService 负责限流/分组/显示）
                    var payload = Models.NotificationPayload.FromJson(data);
                    Services.NotificationService.Instance.Enqueue(payload);
                }
                else if (evt == "notification_progress" && data != null)
                {
                    // 进度更新快捷事件: { group_id, progress, body? }
                    var groupId = (string?)data["group_id"] ?? "";
                    var progress = (int?)data["progress"];
                    var body = (string?)data["body"];
                    Services.NotificationService.Instance.UpdateProgress(groupId, progress, body);
                }
                else if (evt == "team_row_updated" && data != null)
                {
                    // 团队模块行数据更新通知：拉取受影响模块的最新行并刷新 UI
                    var itemServerId = (string?)data["itemServerId"];
                    bool forceFull = data["forceFull"]?.ToObject<bool>() == true;
                    if (!string.IsNullOrEmpty(itemServerId))
                        _ = HandleTeamRowUpdatedAsync(itemServerId!, forceFull);
                }
                else if (evt == "share_cancelled" && data != null)
                {
                    // 属主取消分享：移除本地导入的组件（item 级）或导入的窗口（bar 级）
                    HandleShareCancelled(data);
                }
                else if (evt == "structure_changed")
                {
                    // 窗口/模块结构变更（同账号其他设备的新建/删除等操作）：拉取对账补齐本地；
                    // pushLocal=false 只拉不推，避免多设备间循环触发
                    _ = Services.CloudSyncService.Instance.ReconcileAsync(pushLocal: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 后端WS消息解析异常: {ex.Message}");
            }
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
        /// 处理团队行更新通知：拉取受影响模块的最新行数据并刷新 UI。
        /// forceFull=true（如分享取消）：全量拉取并整体替换本地行，让不可见的行从本地消失。
        /// </summary>
        private async System.Threading.Tasks.Task HandleTeamRowUpdatedAsync(string itemServerId, bool forceFull = false)
        {
            try
            {
                var settings = AppSettings;
                if (string.IsNullOrEmpty(settings.AccountToken)) return;

                // 找到本地引用该 serverId 的模块
                Models.PanelItem? target = null;
                foreach (var win in WindowConfigs.Windows)
                {
                    target = FindPanelByServerId(win.Panels, itemServerId);
                    if (target != null) break;
                }
                if (target == null) return;

                // 强制全量：走 CloudSyncService 的全量拉取（整体替换本地行）后刷新 UI
                if (forceFull)
                {
                    bool changed = await Services.CloudSyncService.Instance.PullItemFullAsync(target);
                    System.Diagnostics.Debug.WriteLine($"[App] 强制全量刷新 {(changed ? "有变更" : "无变更")}: {target.Type}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (mw.IsLoaded)
                                mw.RefreshNativePanels();
                        }
                    });
                    return;
                }

                // 合并语义全量拉取：只增改、通过墓碑删除，不会整体删除未返回的本地行；
                // 绑定同一 ServerId 的其他视图由 CloudSyncService 内部镜像同步
                bool mergeChanged = await Services.CloudSyncService.Instance.PullItemMergeFullAsync(target);
                System.Diagnostics.Debug.WriteLine($"[App] 团队行更新已应用: {target.Type}, {(mergeChanged ? "有变更" : "无变更")}");
                if (mergeChanged)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (mw.IsLoaded)
                                mw.RefreshNativePanels();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 处理团队行更新失败: {ex.Message}");
            }
        }

        /// <summary>刷新所有已加载主窗口的面板渲染（数据同步后调用）</summary>
        public void RefreshAllMainWindows()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.RefreshNativePanels();
                }
            }));
        }

        /// <summary>分享失效清理：从所有已加载主窗口的 dock 移除指定导入窗口的标签</summary>
        public void RemoveSharedWindowsByIds(System.Collections.Generic.IEnumerable<string> windowIds)
        {
            var ids = windowIds.ToList();
            if (ids.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (!mw.IsLoaded) continue;
                    foreach (var id in ids)
                        mw.RemoveSharedWindowById(id);
                }
            }));
        }

        /// <summary>
        /// 处理分享取消通知：移除本地导入的组件（item 级）或导入的窗口（bar 级）。
        /// localId 为导入时上报的本地组件 GUID；为空时回退按 ServerId 匹配（仅对 reference 模块有效）。
        /// </summary>
        private void HandleShareCancelled(Newtonsoft.Json.Linq.JObject data)
        {
            try
            {
                string shareLevel = (string?)data["shareLevel"] ?? "item";
                string itemServerId = (string?)data["itemServerId"] ?? "";
                string sourceBarId = (string?)data["sourceBarId"] ?? "";
                string localId = (string?)data["localId"] ?? "";

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (shareLevel == "bar")
                    {
                        // 优先按导入时上报的 localId 匹配；旧导入记录无 localId 时回退按 SourceBarId 匹配
                        Models.WindowConfig? win = null;
                        if (!string.IsNullOrEmpty(localId))
                            win = WindowConfigs.Windows.FirstOrDefault(w => w.Id == localId);
                        if (win == null && !string.IsNullOrEmpty(sourceBarId))
                            win = WindowConfigs.Windows.FirstOrDefault(w => w.SourceBarId == sourceBarId);
                        if (win == null) return;
                        WindowConfigs.Windows.Remove(win);
                        WindowConfigs.Save();
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RemoveSharedWindowById(win.Id);
                        System.Diagnostics.Debug.WriteLine($"[App] 分享已取消: 移除导入窗口 localId={win.Id}");
                    }
                    else
                    {
                        bool removed = false;
                        foreach (var w in WindowConfigs.Windows)
                            removed |= RemoveSharedPanels(w.Panels, localId, itemServerId);
                        if (!removed) return;
                        WindowConfigs.Save();
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RefreshNativePanels();
                        System.Diagnostics.Debug.WriteLine($"[App] 分享已取消: 移除导入组件 localId={localId}, ServerId={itemServerId}");
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] 处理分享取消通知失败: {ex.Message}");
            }
        }

        /// <summary>递归移除匹配的导入面板：优先按 localId 精确匹配；无 localId 时按 ServerId 匹配</summary>
        private static bool RemoveSharedPanels(System.Collections.Generic.List<Models.PanelItem> panels, string localId, string itemServerId)
        {
            bool removed = false;
            for (int i = panels.Count - 1; i >= 0; i--)
            {
                var p = panels[i];
                bool match = !string.IsNullOrEmpty(localId)
                    ? p.Id == localId
                    : (!string.IsNullOrEmpty(itemServerId) && p.ServerId == itemServerId);
                if (match)
                {
                    panels.RemoveAt(i);
                    removed = true;
                    continue;
                }
                if (p.ChildPanels != null)
                    removed |= RemoveSharedPanels(p.ChildPanels, localId, itemServerId);
            }
            return removed;
        }

        private static Models.PanelItem? FindPanelByServerId(System.Collections.Generic.List<Models.PanelItem> panels, string serverId)
        {
            foreach (var p in panels)
            {
                if (p.ServerId == serverId) return p;
                if (p.ChildPanels != null)
                {
                    var found = FindPanelByServerId(p.ChildPanels, serverId);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 通知队列就绪事件：在 UI 线程将通知转发到所有已加载的主窗口渲染。
        /// 使用 BeginInvoke 避免阻塞 WS 接收线程（Alert 模式 ShowDialog 会长时间阻塞）。
        /// </summary>
        private void OnNotificationReady(Models.NotificationPayload payload)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.ShowRichNotification(payload);
                }
            }));
        }

        /// <summary>
        /// 同组通知更新事件：在 UI 线程通知主窗口原地更新卡片
        /// </summary>
        private void OnNotificationUpdated(Models.NotificationPayload payload)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.UpdateRichNotification(payload);
                }
            }));
        }

        #region 插件编辑器桥接

        /// <summary>处理插件编译请求</summary>
        private async void HandlePluginBuild(Guid clientId, Newtonsoft.Json.Linq.JObject obj)
        {
            var request = new Services.Plugins.PluginBuildRequest
            {
                PluginId = (string?)obj["pluginId"] ?? "",
                PluginName = (string?)obj["pluginName"] ?? "",
                Code = (string?)obj["code"] ?? "",
                Xaml = (string?)obj["xaml"],
                ConfigJson = (string?)obj["configJson"],
                PluginClass = (string?)obj["pluginClass"] ?? ""
            };

            // 编译前先卸载同名插件（必须在 UI 线程执行，因为要从可视化树移除插件 UI）
            await Dispatcher.InvokeAsync(() =>
            {
                Services.Plugins.PluginLoaderService.Instance.UnloadPlugin(request.PluginId);
            });
            // Unload 是异步的，触发 GC 并等待文件锁释放
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(300);

            // 在线程池执行编译，避免阻塞 UI
            var result = await Task.Run(() => Services.Plugins.PluginBuildService.Instance.Build(request));

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginBuildResult",
                success = result.Success,
                outputPath = result.OutputPath,
                errors = result.Errors,
                warnings = result.Warnings
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);

            // 编译成功后从临时目录加载插件（供测试运行）
            if (result.Success && result.OutputPath != null)
            {
                var pluginDir = System.IO.Path.GetDirectoryName(result.OutputPath)!;
                await Dispatcher.InvokeAsync(() =>
                {
                    Services.Plugins.PluginLoaderService.Instance.LoadDevPlugin(pluginDir);
                });
            }
        }

        /// <summary>处理插件列表请求</summary>
        private async void HandlePluginList(Guid clientId)
        {
            var plugins = Services.Plugins.PluginLoaderService.Instance.Plugins
                .Select(p => new
                {
                    id = p.Config.Id,
                    name = p.Config.Name,
                    version = p.Config.Version,
                    description = p.Config.Description,
                    author = p.Config.Author,
                    icon = p.Config.Icon,
                    enabled = p.Config.Enabled,
                    directory = p.PluginDirectory
                }).ToList();

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginListResult",
                plugins
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);
        }

        /// <summary>处理插件加载请求</summary>
        private async void HandlePluginLoad(Guid clientId, Newtonsoft.Json.Linq.JObject obj)
        {
            var pluginId = (string?)obj["pluginId"] ?? "";
            Services.Plugins.PluginLoaderService.Instance.LoadAllPlugins(AppSettings.DisabledPlugins);
            var plugin = Services.Plugins.PluginLoaderService.Instance.GetPlugin(pluginId);

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginLoadResult",
                success = plugin != null,
                pluginId,
                message = plugin != null ? $"插件 {plugin.Config.Name} 已加载" : $"插件 {pluginId} 加载失败"
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);
        }

        /// <summary>处理插件卸载请求</summary>
        private async void HandlePluginUnload(Guid clientId, Newtonsoft.Json.Linq.JObject obj)
        {
            var pluginId = (string?)obj["pluginId"] ?? "";
            // 当前简化处理：重新加载所有插件（待实现单独卸载）
            Services.Plugins.PluginLoaderService.Instance.LoadAllPlugins(AppSettings.DisabledPlugins);

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginUnloadResult",
                success = true,
                pluginId,
                message = $"插件 {pluginId} 已卸载"
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);
        }

        /// <summary>处理插件测试运行请求：弹出独立窗口展示插件 UI</summary>
        private void HandlePluginTest(Newtonsoft.Json.Linq.JObject obj)
        {
            var pluginId = (string?)obj["pluginId"] ?? "";
            if (string.IsNullOrEmpty(pluginId)) return;

            Dispatcher.InvokeAsync(() =>
            {
                var loader = Services.Plugins.PluginLoaderService.Instance;

                // 插件未加载时（如测试窗口关闭后 DLL 已卸载），自动从 plugin_dev 目录重新加载
                if (loader.GetPlugin(pluginId) == null)
                {
                    var assemblyName = pluginId.Replace("-", "_").Replace(" ", "_");
                    var devDir = System.IO.Path.Combine(
                        Services.Plugins.PluginBuildService.Instance.PluginsOutputDir, assemblyName);
                    if (System.IO.Directory.Exists(devDir))
                        loader.LoadDevPlugin(devDir);
                }

                var window = new Windows.PluginTestWindow(pluginId);
                window.Show();
            });
        }

        /// <summary>处理插件保存请求：将临时目录中的插件打包为 zip 导出</summary>
        private async void HandlePluginSave(Guid clientId, Newtonsoft.Json.Linq.JObject obj)
        {
            var pluginId = (string?)obj["pluginId"] ?? "";
            if (string.IsNullOrEmpty(pluginId)) return;

            var assemblyName = pluginId.Replace("-", "_").Replace(" ", "_");
            var pluginDir = System.IO.Path.Combine(
                Services.Plugins.PluginBuildService.Instance.PluginsOutputDir, assemblyName);

            if (!System.IO.Directory.Exists(pluginDir))
            {
                var errResponse = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "pluginSaveResult",
                    success = false,
                    message = "插件尚未编译，请先编译构建"
                });
                await Services.WebSocketServerService.Instance.SendAsync(clientId, errResponse);
                return;
            }

            // 在 UI 线程弹出保存对话框
            string? savePath = null;
            await Dispatcher.InvokeAsync(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存插件",
                    Filter = "插件包 (*.zip)|*.zip",
                    FileName = $"{assemblyName}.zip",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (dlg.ShowDialog() == true)
                    savePath = dlg.FileName;
            });

            if (string.IsNullOrEmpty(savePath))
            {
                var cancelResponse = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "pluginSaveResult",
                    success = false,
                    message = "已取消保存"
                });
                await Services.WebSocketServerService.Instance.SendAsync(clientId, cancelResponse);
                return;
            }

            try
            {
                // 打包为 zip
                if (System.IO.File.Exists(savePath))
                    System.IO.File.Delete(savePath);
                System.IO.Compression.ZipFile.CreateFromDirectory(pluginDir, savePath);

                var okResponse = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "pluginSaveResult",
                    success = true,
                    message = $"插件已保存: {savePath}",
                    path = savePath
                });
                await Services.WebSocketServerService.Instance.SendAsync(clientId, okResponse);
            }
            catch (Exception ex)
            {
                var failResponse = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "pluginSaveResult",
                    success = false,
                    message = $"保存失败: {ex.Message}"
                });
                await Services.WebSocketServerService.Instance.SendAsync(clientId, failResponse);
            }
        }

        /// <summary>处理开发项目列表请求：扫描 plugin_dev 目录</summary>
        private async void HandlePluginDevList(Guid clientId)
        {
            var devDir = Services.Plugins.PluginBuildService.Instance.PluginsOutputDir;
            var projects = new System.Collections.Generic.List<object>();

            if (System.IO.Directory.Exists(devDir))
            {
                foreach (var dir in System.IO.Directory.GetDirectories(devDir))
                {
                    var configPath = System.IO.Path.Combine(dir, "plugin.json");
                    if (!System.IO.File.Exists(configPath)) continue;
                    try
                    {
                        var json = System.IO.File.ReadAllText(configPath);
                        var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Services.Plugins.PluginConfig>(json);
                        if (cfg == null || string.IsNullOrEmpty(cfg.Id)) continue;
                        projects.Add(new
                        {
                            id = cfg.Id,
                            name = cfg.Name,
                            version = cfg.Version,
                            description = cfg.Description,
                            icon = cfg.Icon,
                            dirName = System.IO.Path.GetFileName(dir)
                        });
                    }
                    catch { /* 忽略解析失败的项目 */ }
                }
            }

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginDevListResult",
                projects
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);
        }

        /// <summary>处理加载开发项目请求：读取源码文件返回给前端</summary>
        private async void HandlePluginDevLoad(Guid clientId, Newtonsoft.Json.Linq.JObject obj)
        {
            var dirName = (string?)obj["dirName"] ?? "";
            if (string.IsNullOrEmpty(dirName)) return;

            var devDir = Services.Plugins.PluginBuildService.Instance.PluginsOutputDir;
            var projectDir = System.IO.Path.Combine(devDir, dirName);

            if (!System.IO.Directory.Exists(projectDir))
            {
                var errResp = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    type = "pluginDevLoadResult",
                    success = false,
                    message = "项目目录不存在"
                });
                await Services.WebSocketServerService.Instance.SendAsync(clientId, errResp);
                return;
            }

            // 读取 plugin.json
            string? configJson = null;
            var configPath = System.IO.Path.Combine(projectDir, "plugin.json");
            if (System.IO.File.Exists(configPath))
                configJson = System.IO.File.ReadAllText(configPath);

            // 读取 C# 源码（{dirName}.cs）
            string? code = null;
            var csPath = System.IO.Path.Combine(projectDir, $"{dirName}.cs");
            if (System.IO.File.Exists(csPath))
                code = System.IO.File.ReadAllText(csPath);

            // 读取 XAML（从 config 解析文件名，默认 ui.xaml）
            string? xaml = null;
            var xamlFileName = "ui.xaml";
            if (configJson != null)
            {
                try
                {
                    var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Services.Plugins.PluginConfig>(configJson);
                    if (!string.IsNullOrEmpty(cfg?.Xaml)) xamlFileName = cfg.Xaml;
                }
                catch { }
            }
            var xamlPath = System.IO.Path.Combine(projectDir, xamlFileName);
            if (System.IO.File.Exists(xamlPath))
                xaml = System.IO.File.ReadAllText(xamlPath);

            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "pluginDevLoadResult",
                success = true,
                configJson,
                code,
                xaml
            });
            await Services.WebSocketServerService.Instance.SendAsync(clientId, response);
        }

        #endregion

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
        /// 重置显示器工作区：恢复被残留 AppBar 占用的空间（排除任务栏区域）。
        /// 放到后台线程执行并设整体超时：SystemParametersInfo/EnumDisplayMonitors
        /// 可能因 shell 锁争用或挂起窗口长时间阻塞，绝不能卡住调用线程（尤其退出流程）。
        /// </summary>
        private static void ResetWorkArea(int maxWaitMs = 3000)
        {
            var task = System.Threading.Tasks.Task.Run(ResetWorkAreaCore);
            if (!task.Wait(TimeSpan.FromMilliseconds(maxWaitMs)))
                System.Diagnostics.Debug.WriteLine("[AppBar] 重置工作区超时，放弃等待（后台继续执行）");
        }

        private static void ResetWorkAreaCore()
        {
            try
            {
                // 获取任务栏位置，避免工作区覆盖任务栏
                RECT taskbarRect = default;
                bool hasVisibleTaskbar = false;
                var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero && NativeMethods.IsWindowVisible(taskbar))
                {
                    NativeMethods.GetWindowRect(taskbar, out taskbarRect);
                    hasVisibleTaskbar = taskbarRect.Right - taskbarRect.Left > 0
                                       && taskbarRect.Bottom - taskbarRect.Top > 0;
                }

                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData) =>
                    {
                        var monitorInfo = new MONITORINFO();
                        monitorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>();
                        if (NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
                        {
                            var workRect = monitorInfo.rcMonitor;

                            // 从全屏区域中扣除任务栏占用的边缘
                            if (hasVisibleTaskbar)
                            {
                                if (taskbarRect.Top <= monitorInfo.rcMonitor.Top &&
                                    taskbarRect.Bottom > monitorInfo.rcMonitor.Top &&
                                    taskbarRect.Left <= monitorInfo.rcMonitor.Left &&
                                    taskbarRect.Right >= monitorInfo.rcMonitor.Right)
                                    workRect.Top = taskbarRect.Bottom; // 任务栏在顶部
                                else if (taskbarRect.Bottom >= monitorInfo.rcMonitor.Bottom &&
                                    taskbarRect.Top < monitorInfo.rcMonitor.Bottom &&
                                    taskbarRect.Left <= monitorInfo.rcMonitor.Left &&
                                    taskbarRect.Right >= monitorInfo.rcMonitor.Right)
                                    workRect.Bottom = taskbarRect.Top; // 任务栏在底部
                                else if (taskbarRect.Left <= monitorInfo.rcMonitor.Left &&
                                    taskbarRect.Right > monitorInfo.rcMonitor.Left &&
                                    taskbarRect.Top <= monitorInfo.rcMonitor.Top &&
                                    taskbarRect.Bottom >= monitorInfo.rcMonitor.Bottom)
                                    workRect.Left = taskbarRect.Right; // 任务栏在左侧
                                else if (taskbarRect.Right >= monitorInfo.rcMonitor.Right &&
                                    taskbarRect.Left < monitorInfo.rcMonitor.Right &&
                                    taskbarRect.Top <= monitorInfo.rcMonitor.Top &&
                                    taskbarRect.Bottom >= monitorInfo.rcMonitor.Bottom)
                                    workRect.Right = taskbarRect.Left; // 任务栏在右侧
                            }

                            // 注意：fWinIni 必须传 0，不能带 SPIF_SENDCHANGE——
                            // 带该标志时系统会在当前线程同步向所有顶层窗口广播 WM_SETTINGCHANGE，
                            // 任一窗口挂起（如 WebView2 进程窗口）都会导致本调用无限期阻塞，卡死退出流程。
                            // 窗口通知由下方带超时保护的 BroadcastSettingChange 负责。
                            NativeMethods.SystemParametersInfo(
                                NativeMethods.SPI_SETWORKAREA, 0, ref workRect, 0);
                            System.Diagnostics.Debug.WriteLine(
                                $"[AppBar] 重置工作区: L={workRect.Left} T={workRect.Top} R={workRect.Right} B={workRect.Bottom}");
                        }
                        return true;
                    }, 0);

                // 广播 WM_SETTINGCHANGE，确保所有窗口立即感知工作区变化
                BroadcastSettingChange();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppBar] 重置工作区失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 广播 WM_SETTINGCHANGE（通知所有顶层窗口工作区已变化）。
        /// SendMessageTimeout 的超时按“每个窗口”单独计算，系统里若有挂起/慢响应的窗口
        /// （如 WebView2 浏览器进程窗口），同步广播会累积等待甚至卡死退出流程；
        /// 故放到后台线程执行并设置整体超时上限，确保调用线程永不被长时间阻塞。
        /// </summary>
        private static void BroadcastSettingChange(int maxWaitMs = 1500)
        {
            var broadcast = System.Threading.Tasks.Task.Run(() =>
                NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 1000, out _));
            broadcast.Wait(TimeSpan.FromMilliseconds(maxWaitMs));
        }

        /// <summary>
        /// 退出时恢复工作区：检查 AppBar 移除后工作区是否已还原，未还原则强制重置
        /// </summary>
        private static void RestoreWorkAreaOnExit()
        {
            // 先广播一次 WM_SETTINGCHANGE，让 shell 完成挂起的工作区重新计算
            BroadcastSettingChange();

            // 短暂等待系统处理
            System.Threading.Thread.Sleep(100);

            // 检查主显示器工作区是否仍被残留 AppBar 占用：
            // 如果工作区左右两侧被扣除了不属于任务栏的区域，则强制重置
            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            bool needReset = false;

            var monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>();
            var hPrimary = NativeMethods.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (NativeMethods.GetMonitorInfo(hPrimary, ref monitorInfo))
            {
                int leftGap = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left;
                int rightGap = monitorInfo.rcMonitor.Right - monitorInfo.rcWork.Right;

                // 任务栏在左/右时会有正常间隙，其他情况工作区不应有左右间隙
                bool taskbarOnLeft = false, taskbarOnRight = false;
                if (taskbar != IntPtr.Zero && NativeMethods.IsWindowVisible(taskbar))
                {
                    NativeMethods.GetWindowRect(taskbar, out var tbRect);
                    taskbarOnLeft = tbRect.Left <= monitorInfo.rcMonitor.Left && tbRect.Right < monitorInfo.rcMonitor.Right;
                    taskbarOnRight = tbRect.Right >= monitorInfo.rcMonitor.Right && tbRect.Left > monitorInfo.rcMonitor.Left;
                }

                if ((leftGap > 0 && !taskbarOnLeft) || (rightGap > 0 && !taskbarOnRight))
                    needReset = true;

                System.Diagnostics.Debug.WriteLine(
                    $"[AppBar] 退出检查: leftGap={leftGap}, rightGap={rightGap}, needReset={needReset}");
            }

            if (needReset)
                ResetWorkArea();
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
        /// 获取当前主题是否为浅色模式（考虑 System 模式与 JSON 自定义主题覆盖）
        /// </summary>
        public static bool IsCurrentThemeLight()
        {
            // JSON 自定义主题优先：其背景明暗决定标题栏深浅
            if (_customThemeLight.HasValue) return _customThemeLight.Value;

            if (string.Equals(_currentThemeMode, "System", StringComparison.OrdinalIgnoreCase))
                return IsSystemLightTheme();
            return string.Equals(_currentThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>JSON 自定义主题的明暗覆盖（null 表示未启用自定义主题）</summary>
        private static bool? _customThemeLight = null;

        /// <summary>
        /// 设置自定义主题的明暗覆盖并刷新所有窗口的 DWM 标题栏。
        /// 传入 null 表示清除覆盖（回到由 ThemeMode 决定）。
        /// </summary>
        public static void SetCustomThemeLightOverride(bool? isLight)
        {
            _customThemeLight = isLight;
            foreach (Window w in Current.Windows)
                UpdateWindowDwmTheme(w, IsCurrentThemeLight());
        }

        /// <summary>手动触发主题变更通知（供应用/清除 JSON 自定义主题后调用，驱动各面板重绘）</summary>
        public static void RaiseThemeChanged() => ThemeChanged?.Invoke();

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
            // 堆栈在 finalizer 重抛时已丢失，额外记录展平后的内部异常便于定位泄漏源
            string? detail = null;
            if (e.Exception is AggregateException agg)
            {
                var sb = new System.Text.StringBuilder();
                int idx = 1;
                foreach (var inner in agg.Flatten().InnerExceptions)
                    sb.AppendLine($"  内部异常{idx++}: {inner.GetType().FullName}: {inner.Message} @ {inner.StackTrace?.Split('\n')[0].Trim()}");
                detail = sb.ToString();
            }
            LogCrash("TaskScheduler_UnobservedTaskException", e.Exception, detail);
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
            // 进程即将终止时尽力释放 AppBar 区域，避免工作区残留
            if (e.IsTerminating)
            {
                try
                {
                    foreach (var w in _windows.ToList())
                        w.UnregisterAppBar();
                    RestoreWorkAreaOnExit();
                }
                catch { }
            }

            if (e.ExceptionObject is Exception ex)
            {
                LogCrash("UnhandledException", ex);
                
                // 记录到崩溃日志
                string crashLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk",
                    "Logs",
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
        /// 一次性迁移：把旧位置的日志（根目录散落的 *.log 与 WindowLogs/ScreenCaptureLogs 子目录）
        /// 并入统一的 %LocalAppData%\SideDesk\Logs 目录，并清理空的旧子目录。
        /// </summary>
        private static void MigrateLogsToLogsFolder()
        {
            try
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
                if (!Directory.Exists(baseDir)) return;

                string logsDir = Path.Combine(baseDir, "Logs");
                Directory.CreateDirectory(logsDir);

                void MoveFile(string src)
                {
                    if (!File.Exists(src)) return;
                    string dst = Path.Combine(logsDir, Path.GetFileName(src));
                    if (File.Exists(dst)) { File.Delete(src); return; } // 新位置已有同名文件（更新）：删除旧文件
                    File.Move(src, dst);
                }

                // 根目录散落的日志：http.log / http.old.log / crash-*.log / window-*.log / keyboard-*.log
                foreach (var f in Directory.GetFiles(baseDir, "*.log"))
                    MoveFile(f);

                // 旧子目录：WindowLogs / ScreenCaptureLogs → Logs（迁移完成后删除空目录）
                foreach (var oldSub in new[] { "WindowLogs", "ScreenCaptureLogs" })
                {
                    string oldDir = Path.Combine(baseDir, oldSub);
                    if (!Directory.Exists(oldDir)) continue;
                    foreach (var f in Directory.GetFiles(oldDir))
                        MoveFile(f);
                    if (Directory.GetFiles(oldDir).Length == 0 && Directory.GetDirectories(oldDir).Length == 0)
                        Directory.Delete(oldDir);
                }
            }
            catch { }
        }

        /// <summary>
        /// 记录崩溃信息
        /// </summary>
        private void LogCrash(string source, Exception ex, string? extra = null)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logLine = $"""
                [{timestamp}] [CRASH] {source}
                类型: {ex.GetType().FullName}
                消息: {ex.Message}
                堆栈: {ex.StackTrace}
                {(extra != null ? $"内部异常:\n{extra}" : "")}
                """;
            
            System.Diagnostics.Debug.WriteLine(logLine);
            
            // 写入日志文件
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk",
                    "Logs");
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
        /// <param name="afterOpen">窗口显示后执行的回调（如导航到指定页面）</param>
        public void OpenSettings(Action<SettingsWindow>? afterOpen = null)
        {
            foreach (Window w in Windows)
            {
                if (w is SettingsWindow existing)
                {
                    existing.Show();
                    existing.Activate();
                    afterOpen?.Invoke(existing);
                    return;
                }
            }

            var settings = new SettingsWindow();
            settings.Show();
            settings.Activate();
            afterOpen?.Invoke(settings);
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
