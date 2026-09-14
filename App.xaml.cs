using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    public partial class App : Application
    {
        public LayoutMode CurrentLayout => AppSettings.GetCurrentMonitor(0, GetCurrentDesktopId()).Layout;
        public Settings AppSettings { get; private set; } = new();
        public WindowConfigManager WindowConfigs { get; private set; } = new();
        public ProgramRegistryManager ProgramRegistry { get; private set; } = new();
        public Services.VirtualDesktopService? VirtualDesktop { get; private set; }

        /// <summary>启动时 Token 验证失败的提示信息（非空表示登录已过期，设置页显示提示）</summary>
        public static string? AccountTokenExpiredMessage { get; set; }

        /// <summary>运行时离线标记：auth 接口请求失败、无法确认登录态，账户页视为未登录（不清本地 Token，恢复后重新验证）</summary>
        public static bool AccountOffline { get; private set; }

        /// <summary>本次会话登录态是否已经接口确认（启动校验 bizCode=200 或登录窗口成功）；未确认前账户页不得仅凭 Token 宣称已登录</summary>
        public static bool AccountVerified { get; private set; }

        /// <summary>标记登录态已经接口确认</summary>
        public static void MarkAccountVerified() => AccountVerified = true;

        /// <summary>设置离线标记并同步 UI：离线→账户页视为未登录 + 橙色离线通知；恢复→关闭通知并刷新账户页</summary>
        public static void MarkAccountOffline(bool offline)
        {
            if (AccountOffline == offline) return;
            AccountOffline = offline;
            var app = Current as App;
            if (app == null) return;
            if (offline) app.ShowLoggedOutNotification();
            else app.DismissLoggedOutNotification();
            app.Dispatcher.Invoke(() =>
            {
                foreach (Window w in Current.Windows)
                    if (w is SettingsWindow sw) sw.RefreshAccountPage();
            });
        }

        /// <summary>
        /// 账号作用域变更后在进程内切换窗口数据（登录/登出/登录态失效均走此路径，不再要求重启）：
        /// 按新作用域重新加载 WindowConfigManager 并重新注入各主窗口，面板与标签栏随即呈现目标作用域的数据。
        /// 调用前提：作用域晋升（AdoptScope）已落库 —— 否则新作用域加载会缺刚晋升的数据，
        /// 随后一次保存会把它们当作「已删除」打墓碑上报服务端。
        /// 重载后 mgr.LoadedScope 与 CurrentScope 一致，SaveManager / 结构上传的作用域守卫随之放行。
        /// </summary>
        public void ReloadWindowConfigsForNewScope()
        {
            void Reload()
            {
                WindowConfigs = WindowConfigManager.Load();
                WindowConfigs.EnsureDefault();
                WindowConfigs.EnsureAtLeastOneBar();
                foreach (var mw in _windows)
                {
                    // 已加载：重设 mgr 并重切活动窗口（面板渲染依赖当前窗口实例，仅重绘不够）；
                    // 未加载：只注入 mgr，其 MainWindow_Loaded 会自行按新 dock 切换活动窗口
                    if (mw.IsLoaded) mw.ReloadForScopeSwitch(WindowConfigs);
                    else mw.SetWindowConfigManager(WindowConfigs);
                }
                foreach (Window w in Current.Windows)
                    if (w is SettingsWindow sw) sw.RefreshAfterScopeSwitch();
            }
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) Reload(); else dispatcher.Invoke(Reload);

            // 作用域已对齐：补一次云同步对账。登录瞬间那次 Start() 因 MustRejectStructureUpload
            // （持 Token 但内存仍属 local）被拦下，此刻内存已属新作用域，才是与服务端对齐的正确时机。
            // 登出/登录态失效路径已清 Token 并关闭云同步，条件为假不会误触发。
            if (AppSettings.EnableCloudSync && !string.IsNullOrEmpty(AppSettings.AccountToken))
            {
                try { _ = Services.CloudSyncService.Instance.ReconcileAsync(pushLocal: true); } catch { }
            }
        }

        /// <summary>
        /// 内存结构发生「bar 增删」级变更后免重启重绑定 UI：仅重绘（RefreshAllMainWindows）不够 ——
        /// 面板 _currentWindowConfig 与 MainWindow._dockState 仍指向被删除的旧 bar 实例
        /// （典型：云同步上传前折叠把正在显示的离线 bar 并入已同步 bar 后删除），
        /// 必须像 ReloadWindowConfigsForNewScope 那样对已加载主窗口重设 mgr + 重切活动窗口。
        /// </summary>
        public void ReloadAllWindowsUiFromCurrentConfigs()
        {
            void Reload()
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded) mw.ReloadForScopeSwitch(WindowConfigs);
                    else mw.SetWindowConfigManager(WindowConfigs);
                }
            }
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) Reload(); else dispatcher.Invoke(Reload);
        }

        private readonly System.Collections.Generic.List<MainWindow> _windows = new();
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

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
            // 注册全局异常处理（Velopack 钩子已在 Program.Main 第一行拦截，不会走到这里）
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);

            // 清理残留 AppBar：杀掉其他 SideDesk 进程
            CleanupStaleAppBar();

            // 加载上次保存的设置
            AppSettings = Settings.Load();

            // 渲染管线回退：必须在任何窗口创建前设置（支持 --software-rendering 启动参数、设置项、虚拟/远程主显示自动检测）
            ApplyRenderingModeFallback(e.Args);

            // 开机自启：按设置同步注册表 Run 项（每次启动同步，可自动修正 exe 路径变化）
            Services.AutoStartService.Apply(Framework.AppSettings.Instance.RunAtStartup);

            // 多语言：按已保存的语言偏好初始化（auto/zh-CN/en）
            Framework.Lang.Init(AppSettings.Language ?? Framework.Lang.Auto);

            // 旧日志文件迁移到统一的 Logs 目录（需在各类日志器写入前执行）
            MigrateLogsToLogsFolder();

            // 每次启动清空上一次运行留下的调试日志（日志已改为固定名、不再按天分文件）
            // 例外：windowlog_*.txt 是「使用时长/屏幕时长」功能的历史数据，保留不清
            ClearDebugLogsOnStartup();

            // 窗口使用时长原始日志 windowlog_*.txt 仅保留最近 7 天，清理更早的（数据已聚合入库，与开关状态无关）
            Services.ScreenTimeHourlyService.PruneOldWindowLogs();

            // 网络请求日志开关（默认开启；日志写入 %LocalAppData%\SideDesk\Logs\http.log）
            Services.HttpLog.Enabled = AppSettings.HttpLogEnabled;

            // 后台预计算设备唯一 ID（CPU ID + MAC 指纹）：WMI 查询较慢，放线程池避免阻塞启动
            _ = Task.Run(() => { var _ = Services.DeviceIdService.DeviceId; });

            // 加载窗口配置
            WindowConfigs = WindowConfigManager.Load();
            WindowConfigs.EnsureDefault();
            // 兜底：老用户曾删光所有 bar（EnsureDefault 已置标不再播种）或数据异常为空时补一个空 bar，避免侧边栏空白且右键失效
            WindowConfigs.EnsureAtLeastOneBar();

            // 初始化数据轮询服务（中央数据源）
            Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);

            // 初始化变量集服务（多变量聚合生成本地新变量，依赖中央数据源的运行中变量）
            Services.VariableSetService.Instance.Init(WindowConfigs);
            // 拉取云端登记的变量集（个人 + 已加入团队）并入聚合计算；未登录/后端不可达时静默跳过
            _ = Services.VariableSetService.Instance.PullRemoteAsync();

            // 加载程序注册表
            ProgramRegistry = ProgramRegistryManager.Load();

            // 加载外部插件
            try
            {
                Services.Plugins.PluginLoaderService.Instance.LoadAllPlugins(AppSettings.DisabledPlugins);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PluginLoader] startup loading error: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] init error: {ex.Message}");
            }

            ApplyLayout(save: false);

            // 监听系统显示设置变化（分辨率/缩放/显示器增删），防抖后重新布局 AppBar
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // 初始化系统托盘图标
            InitNotifyIcon();

            // 启动本地 WebSocket 服务端 (127.0.0.1:19191)，无条件启动：
            // 除浏览器插件外，插件市场 / 插件编辑器也依赖此桥与宿主通信
            try
            {
                WebSocketServerService.Instance.MessageReceived += OnWebSocketMessageReceived;
                WebSocketServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebSocketServer] start error: {ex.Message}");
            }

            // 订阅后端 WebSocket 消息（好友请求等通知），无论哪条登录路径都生效
            try
            {
                Services.BackendWebSocketService.Instance.MessageReceived -= OnBackendWsMessage;
                Services.BackendWebSocketService.Instance.MessageReceived += OnBackendWsMessage;
                // 连接建立（登录 / 断线重连）后补拉待处理的团队申请与邀请，离线期间错过的也能进通知中心
                Services.BackendWebSocketService.Instance.Connected -= OnBackendWsConnected;
                Services.BackendWebSocketService.Instance.Connected += OnBackendWsConnected;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackendWS] message subscription error: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[Notification] notification service subscription error: {ex.Message}");
            }

            // 启动 MCP Server (127.0.0.1:19192)，仅在开启时
            try
            {
                if (AppSettings.EnableMcpServer)
                    Services.McpServerService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpServer] start error: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[HttpServer] start error: {ex.Message}");
            }

            // 启动全局热键钩子（窗口切换热键 / 语音输入热键，任一开启即安装）
            try
            {
                GlobalHotkeyService.RefreshState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] start error: {ex.Message}");
            }

            // 启动窗口使用时长采集（仅在开启“使用时长分析”时）
            try
            {
                if (SideDesk.Framework.AppSettings.Instance.ShowUsageStats)
                    Services.WindowUsageService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowUsage] start error: {ex.Message}");
            }

            // 启动时恢复上次保存的标题锁定规则（仅在功能开启时）
            try
            {
                if (AppSettings.EnableTitleLock)
                {
                    int restored = TaskbarOverlayManager.RestoreLocksFromRules();
                    System.Diagnostics.Debug.WriteLine($"[TitleLock] restored lock rules on startup, matched {restored} windows");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TitleLock] failed to restore lock rules on startup: {ex.Message}");
            }

            // 启动云同步（仅在已登录且已开启时；未开启时不做任何行级/结构同步）
            try
            {
                if (AppSettings.EnableCloudSync && !string.IsNullOrEmpty(AppSettings.AccountToken))
                    Services.CloudSyncService.Instance.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] start error: {ex.Message}");
            }

            // 启动时验证 Token 有效性（延迟 2 秒，避免阻塞启动流程）
            _ = ValidateTokenOnStartupAsync();

            // 启动 5 秒后静默检查并后台下载更新（尊重设置里的「自动更新」开关）。
            // 注意：静默流程只检查 + 下载，绝不自动应用/重启（2026-06-09 曾因自动重启禁用，
            // 现恢复为“下载完成后通知条提示，由用户在设置→关于里手动重启更新”）。
            Services.VelopackUpdateService.Instance.UpdateReadyChanged += OnUpdateReadyNotify;
            _ = Task.Delay(5000).ContinueWith(async _ =>
            {
                try
                {
                    await Services.VelopackUpdateService.Instance.StartSilentUpdateCheckAndDownloadAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Silent update check error: {ex.Message}");
                }
            }, TaskScheduler.Default);
        }

        /// <summary>静默下载完成后触发：在主窗口通知条提示新版本就绪，点击打开设置→关于页手动应用重启</summary>
        private void OnUpdateReadyNotify()
        {
            var version = Services.VelopackUpdateService.Instance.ReadyUpdateInfo?.TargetFullRelease.Version.ToString() ?? "";
            Dispatcher.Invoke(() => ShowUpdateReadyNotification(version));
        }

        /// <summary>在所有主窗口上显示蓝色更新就绪通知，点击后打开设置→关于页（内含「应用更新并重启」按钮）</summary>
        private void ShowUpdateReadyNotification(string version)
        {
            var blue = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2980B9"));
            blue.Freeze();
            foreach (var mw in _windows)
            {
                if (mw.IsLoaded)
                {
                    mw.DismissNotificationByTag("update-ready"); // 先清旧条目，避免重复检查时堆叠
                    mw.ShowNotification(Framework.Lang.TF("update.readyNotification", version), blue, () =>
                        OpenSettings(sw => sw.SwitchToAboutPage()), "update-ready");
                }
            }
        }

        /// <summary>启动时验证 Token 有效性：延迟 2 秒后执行实际验证，避免阻塞启动流程</summary>
        private async Task ValidateTokenOnStartupAsync()
        {
            // 延迟 2 秒，避免与其他启动任务争抢网络
            await Task.Delay(2000);
            await ValidateAccountTokenAsync();
        }

        /// <summary>
        /// 立即验证本地 Token（调用后端 /auth/userinfo）：有效→清除离线标记恢复登录态；
        /// 明确无效(401/403)→清除本地 Token；不可达→标记离线（不清 Token）。
        /// 供账户页离线状态下点击「登录」手动重连复用。
        /// </summary>
        public async Task ValidateAccountTokenAsync()
        {
            try
            {
                var token = AppSettings.AccountToken;
                if (string.IsNullOrEmpty(token))
                {
                    // 无 token 但曾经登录过（会员用户掉线状态）→ 显示橙色登录提示
                    if (AppSettings.HasLoggedInBefore)
                        ShowLoggedOutNotification();

                    // 未登录 → 以访客（设备）身份建立后端 WebSocket 连接，使本设备可接收“按设备/未登录用户”维度的通知
                    try { Services.BackendWebSocketService.Instance.Start(null); }
                    catch (Exception wsEx) { System.Diagnostics.Debug.WriteLine($"[BackendWS] guest start error: {wsEx.Message}"); }
                    return;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, Constants.URLs.ServerBaseUrl + "/api/auth/userinfo");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = await Services.HttpLog.SendAsync(http, req);
                string body = await resp.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Auth] token verification response ({(int)resp.StatusCode}): {body}");

                // HTTP 层失败（502/504/证书错误等）→ 视为离线，不清 Token
                if (!resp.IsSuccessStatusCode)
                {
                    MarkAccountOffline(true);
                    return;
                }

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
                    MarkAccountVerified(); // 先置已确认，再触发 UI 刷新
                    MarkAccountOffline(false);
                    AppSettings.AccountLoginTime = DateTime.Now;
                    AppSettings.HasLoggedInBefore = true; // 确认会员身份
                    // 登录态有效：默认开启云同步（除非用户主动关闭过 CloudSyncUserOptOut）
                    bool justEnabledCloudSync = false;
                    if (!AppSettings.CloudSyncUserOptOut && !AppSettings.EnableCloudSync)
                    {
                        AppSettings.EnableCloudSync = true;
                        justEnabledCloudSync = true;
                    }
                    AppSettings.Save();

                    // 拉取用户详情（昵称、头像等）
                    await FetchAndSaveUserInfoAsync(token);

                    // Token 有效 → 启动后端 WebSocket 长连接（消息订阅已在 OnStartup 统一建立）
                    try { Services.BackendWebSocketService.Instance.Start(token); }
                    catch (Exception wsEx) { System.Diagnostics.Debug.WriteLine($"[BackendWS] start error: {wsEx.Message}"); }

                    // 登录态有效 → 同步服务端 MCP 工具注册表
                    try { _ = Services.McpToolRegistryService.Instance.SyncAsync(); } catch { }
                    // 本次刚默认开启云同步 → 立即对账一次（OnStartup 时尚未开启、未触发同步；已开启时不重复启动）
                    if (justEnabledCloudSync)
                    {
                        try { Services.CloudSyncService.Instance.Start(); } catch { }
                    }
                    return;
                }

                // code=401/403 表示 Token 明确无效（过期/被踢）
                if (bizCode == 401 || bizCode == 403)
                {
                    System.Diagnostics.Debug.WriteLine($"[Auth] token verification failed (code={bizCode}), clearing local login state");
                    AccountTokenExpiredMessage = Framework.Lang.T("settings.loginExpired");

                    // 作用域即将从 u:A 回退到未归属 local：先记录是否曾登录（首次启动就失效时无需提示重启）
                    bool wasLoggedIn = !string.IsNullOrEmpty(AppSettings.AccountUserId);

                    // 清除本地登录状态
                    AppSettings.AccountToken = null;
                    AppSettings.AccountUserId = null;
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

                    // 作用域已回退：进程内切换到未登录（本地）数据，无需重启打断用户
                    if (wasLoggedIn)
                        ReloadWindowConfigsForNewScope();

                    // 401/403 是服务器的明确应答（Token 无效），网络可达 → 不算离线，直接结束
                    return;
                }
                // 其他 code（如 500、0=解析失败）：服务器未确认登录态 → 视为离线（不清 Token，避免临时故障误清除）
                MarkAccountOffline(true);
            }
            catch (Exception ex)
            {
                // 网络不可达等异常：视为离线，账户页显示未登录 + 离线通知
                System.Diagnostics.Debug.WriteLine($"[Auth] token verification skipped on startup: {ex.Message}");
                if (!string.IsNullOrEmpty(AppSettings.AccountToken))
                    MarkAccountOffline(true);
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
                        mw.ShowNotification(Framework.Lang.T("nativePanel.offlineLoginHint"), orange, () =>
                            OpenSettings(sw => sw.SwitchToAccountPage()), "logged-out");
                }
            });
        }

        /// <summary>
        /// 登录成功后的核心持久化（与具体 UI 无关，可供任意登录入口调用）：
        /// 保存 token/账户信息、登录后默认开启云同步（除非用户主动关闭过）、启动长连接与云同步对账。
        /// token 为空时回退读取 <see cref="LoginWindow.LastToken"/>（WebView2 内嵌登录兜底路径拦截到的 token）。
        /// 幂等：token 为空或已应用过则直接返回 false，避免多入口重复启动。
        /// </summary>
        public bool ApplyLoginSuccessCore(string? token = null)
        {
            token ??= LoginWindow.LastToken;
            if (string.IsNullOrEmpty(token)) return false;

            var s = AppSettings;
            if (s.AccountToken == token) return false; // 已应用过（如多个入口同时订阅登录成功事件），不重复处理

            s.AccountToken = token;
            s.AccountName = LoginWindow.LastUserName;
            s.AccountLoginTime = DateTime.Now;
            s.HasLoggedInBefore = true; // 记录会员身份：曾经登录过
            // 团队选择为空则默认切到"个人"(corpId='')
            if (string.IsNullOrWhiteSpace(s.SelectedCorpId))
            {
                s.SelectedCorpId = null;
                s.SelectedCorpName = null;
            }
            // 登录后默认开启云同步（除非用户曾主动关闭过 → CloudSyncUserOptOut）
            if (!s.CloudSyncUserOptOut)
                s.EnableCloudSync = true;
            s.Save();

            AccountTokenExpiredMessage = null; // 清除过期提示
            MarkAccountVerified();             // 登录接口本身即接口确认
            MarkAccountOffline(false);         // 清除离线标记（内部亦会关闭掉线通知）
            DismissLoggedOutNotification();    // 关闭主窗口上的掉线登录提示

            try { Services.BackendWebSocketService.Instance.Start(token!); } catch { } // 启动后端 WebSocket 长连接
            _ = Services.McpToolRegistryService.Instance.SyncAsync(); // 立即拉取服务端 MCP 工具注册表
            // 云同步已开启 → 立即对账一次（上传本地结构并回写 ServerId，分享等入口随之可用）
            if (s.EnableCloudSync)
            {
                try { Services.CloudSyncService.Instance.Start(); } catch { }
            }
            return true;
        }

        /// <summary>WebView2 兜底登录窗的一次性「登录成功」回调（由 <see cref="StartLoginAsync"/> 统一管理，避免重复订阅）</summary>
        private Action? _loginSucceededHandler;

        /// <summary>
        /// 统一登录入口：默认打开 WebView2 内嵌登录窗（保留原有一次性 LoginSucceeded 回调）。
        /// 系统浏览器 OAuth2 登录改由登录页「用浏览器打开」入口触发（LoginWindow.OnWebMessageReceived），
        /// 其成功后同样经 LoginSucceeded 回到本入口回调，完成核心持久化。
        /// 登录核心持久化（保存 token、默认开启云同步、启动长连接与对账）在回调内完成；
        /// <paramref name="onSuccess"/> 于登录成功、核心持久化之后在 UI 线程回调，供各入口刷新自身账户 UI / 拉取用户详情。
        /// </summary>
        public Task StartLoginAsync(Action? onSuccess = null)
        {
            if (_loginSucceededHandler != null) LoginWindow.LoginSucceeded -= _loginSucceededHandler;
            _loginSucceededHandler = () =>
            {
                LoginWindow.LoginSucceeded -= _loginSucceededHandler;
                _loginSucceededHandler = null;
                ApplyLoginSuccessCore();
                onSuccess?.Invoke();
            };
            LoginWindow.LoginSucceeded += _loginSucceededHandler;
            LoginWindow.Open();
            return Task.CompletedTask;
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
                    System.Net.Http.HttpMethod.Get, Constants.URLs.ServerBaseUrl + "/api/auth/userinfo");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = await Services.HttpLog.SendAsync(http, req);
                string body = await resp.Content.ReadAsStringAsync();

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                if (code != 200) return;
                if (!root.TryGetProperty("data", out var data)) return;

                bool changed = false;

                // 用户 ID（encrypt2 密文）：本地数据按账号隔离的作用域 + 加密同步密钥派生 salt
                string? userIdEnc = data.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
                // 作用域变更即需重载窗口数据，包含「登出后重新登录」（旧值为空）这一档：
                // 内存里的 WindowConfigs 属于加载时的作用域，作用域一旦翻转就与当前账号脱节
                // （登出后只剩 EnsureAtLeastOneBar 兜底建出的空窗口）。旧逻辑只在「旧值非空」时处理，
                // 漏掉的 空→有值 这一档会让内存配置永久停留在 local 作用域，随后任意一次保存或结构上传
                // 就会把该账号本地与服务端的全部窗口清空。
                bool scopeChanged = !string.IsNullOrWhiteSpace(userIdEnc) && userIdEnc != AppSettings.AccountUserId;
                if (scopeChanged)
                {
                    AppSettings.AccountUserId = userIdEnc;
                    changed = true;
                }

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
                            System.Diagnostics.Debug.WriteLine($"[Auth] avatar download failed: {avatarEx.Message}");
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

                // 登录/切换账号后：把未归属（local）的本地变量数据晋升到当前用户并补传服务器（无残留时空查询，幂等）
                Task? adoptTask = null;
                if (!string.IsNullOrWhiteSpace(userIdEnc))
                {
                    adoptTask = Task.Run(() =>
                    {
                        try { Services.DataPollingService.Instance.AdoptPendingLocalData(); }
                        catch (Exception adoptEx) { System.Diagnostics.Debug.WriteLine($"[Auth] orphaned data promotion error: {adoptEx.Message}"); }
                    });
                }

                // 作用域变更：进程内重载窗口数据到新作用域（登录/切换账号后直接呈现该账号状态，无需重启）。
                // 须等晋升落库完成再重载 —— 否则新作用域加载会缺刚晋升进来的数据，
                // 随后一次保存就会把它们当作「已删除」打墓碑上报
                if (scopeChanged)
                {
                    if (adoptTask != null)
                    {
                        try { await adoptTask.ConfigureAwait(false); } catch { }
                    }
                    ReloadWindowConfigsForNewScope();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] failed to get user details: {ex.Message}");
            }
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
        /// </summary>
        public void ApplyLayout(bool save)
        {
            var desktopId = GetCurrentDesktopId();

            // 关闭并释放现有窗口
            foreach (var w in _windows.ToList())
                CloseWindow(w);
            _windows.Clear();

            int monitorCount = MonitorEnumerator.GetMonitorsPrimaryFirst().Count;
            if (monitorCount <= 0) monitorCount = 1;
            _lastMonitorCount = monitorCount;

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
        /// 记录上一次布局对应的显示器数量，用于显示设置变化时判断显示器拓扑是否改变。
        /// </summary>
        private int _lastMonitorCount;
        private System.Windows.Threading.DispatcherTimer? _displayChangeDebounceTimer;

        /// <summary>
        /// 系统显示设置变化回调（分辨率、缩放、显示器增删等）。
        /// 事件可能在非 UI 线程触发，且分辨率切换过程中可能连续触发多次，
        /// 统一调度到 Dispatcher 并用定时器防抖后再刷新布局。
        /// </summary>
        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_displayChangeDebounceTimer == null)
                {
                    _displayChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };
                    _displayChangeDebounceTimer.Tick += (_, _) =>
                    {
                        _displayChangeDebounceTimer.Stop();
                        RefreshAppBarLayoutOnDisplayChange();
                    };
                }
                _displayChangeDebounceTimer.Stop();
                _displayChangeDebounceTimer.Start();
            });
        }

        /// <summary>
        /// 显示设置变化后重新布局 AppBar：
        /// 显示器数量变化（插拔屏幕）→ 按新拓扑重建停靠窗口；
        /// 仅分辨率/缩放变化 → 现有窗口各自重读显示器信息并重新定位。
        /// </summary>
        private void RefreshAppBarLayoutOnDisplayChange()
        {
            int monitorCount = MonitorEnumerator.GetMonitorsPrimaryFirst().Count;
            if (monitorCount <= 0) monitorCount = 1;

            System.Diagnostics.Debug.WriteLine($"[App] display settings changed, monitors={monitorCount} (last={_lastMonitorCount})");

            if (monitorCount != _lastMonitorCount)
                ApplyLayout(save: false);
            else
                ApplyPanelWidths();
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
                System.Diagnostics.Debug.WriteLine($"[App] virtual desktop switched, reapplying layout: {newDesktopId}");
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
                System.Diagnostics.Debug.WriteLine($"[App] WebView2 dispose error: {ex.Message}");
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
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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

            // 释放数据轮询服务：关闭看板打开的浏览器窗口/进程，避免退出后残留
            try { Services.DataPollingService.Instance.Dispose(); } catch { }

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

            // 将本次运行中缺失的翻译键追加写入日志（%LocalAppData%\SideDesk\Logs\lang-missing.log）
            try { Framework.Lang.DumpMissingKeys(); } catch { }

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
                // 系统关机/注销时同样清理看板拉起的浏览器窗口/进程
                try { Services.DataPollingService.Instance.Dispose(); } catch { }
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
            System.Diagnostics.Debug.WriteLine($"[App] ★★★ WS message received [{clientId}]: {message}");
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
                            try { cb(success, value, error); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] fetchCallback error: {ex.Message}"); }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] no callback found for requestId: {requestId}");
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
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] WS message parse error: {ex.Message}"); }

                // 非 fetchResult/register 消息弹窗提示
                MessageBox.Show(message, Framework.Lang.T("nativePanel.extensionMessageReceived"), MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>
        /// 收到后端 WebSocket 推送的消息（好友请求、系统通知等）
        /// </summary>
        private void OnBackendWsMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[App] backend WS message: {message}");
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(message);
                var evt = (string?)obj["event"];
                var data = obj["data"] as Newtonsoft.Json.Linq.JObject;

                if (evt == "friend_request" && data != null)
                {
                    var fromName = (string?)data["fromName"] ?? Framework.Lang.T("nativePanel.unknownUser");
                    var requestId = (string?)data["requestId"] ?? "";
                    // 同步写一条进通知中心（参与服务端已读状态与红点，气泡只是瞬时提示）
                    Services.NotificationSyncService.Instance.EnqueueLiveSilent("friend_request", data);

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
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to show friend request notification on main window: {ex}");
                                // 降级为简单气泡通知（不走富通知卡的模板/ToolTip 构建路径）
                                try { mw.ShowBubbleNotification(Framework.Lang.TF("nativePanel.friendRequestEmoji", fromName)); anyShown = true; } catch { }
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
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to refresh friend requests on settings window: {ex}");
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"[BackendWS] friend_request notification handled, anyShown={anyShown}");
                    });
                }
                else if (evt == "friend_request_result" && data != null)
                {
                    // 好友请求审批结果（发起方收到）：通过/拒绝气泡；通过后好友关系变化需刷新好友列表
                    bool accepted = data["accepted"]?.ToObject<bool>() == true;
                    var friendName = (string?)data["friendName"] ?? Framework.Lang.T("nativePanel.unknownUser");
                    // 同步写进通知中心（未读亮红点，点过即已读并回传服务端）
                    Services.NotificationSyncService.Instance.EnqueueLiveSilent("friend_request_result", data);
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            try
                            {
                                mw.ShowBubbleNotification(accepted
                                    ? Framework.Lang.TF("nativePanel.friendRequestApproved", friendName)
                                    : Framework.Lang.TF("nativePanel.friendRequestDeclined", friendName));
                            }
                            catch { }
                        }
                        // 设置窗口打开时刷新好友页（通过后新好友入列表；拒绝后请求列表移除该条）
                        foreach (Window w in Windows)
                        {
                            try
                            {
                                if (w is SettingsWindow sw && sw.IsLoaded)
                                    sw.OnFriendStatusChanged();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to refresh friends on settings window: {ex}");
                            }
                        }
                    });
                }
                else if (evt == "friend_online" && data != null)
                {
                    var userId = (string?)data["userId"] ?? "";
                    var userName = (string?)data["userName"] ?? Framework.Lang.T("common.friends");
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            // 更新好友模块在线状态（按 encrypt2 密文匹配缓存项）
                            if (!string.IsNullOrEmpty(userId))
                                mw.UpdateFriendOnlineStatus(userId, true);
                            mw.ShowBubbleNotification(Framework.Lang.TF("nativePanel.isOnline", userName));
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
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to refresh friend online status on settings window: {ex}");
                            }
                        }
                    });
                }
                else if (evt == "friend_offline" && data != null)
                {
                    var userId = (string?)data["userId"] ?? "";
                    var userName = (string?)data["userName"] ?? Framework.Lang.T("common.friends");
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            if (!string.IsNullOrEmpty(userId))
                                mw.UpdateFriendOnlineStatus(userId, false);
                            mw.ShowBubbleNotification(Framework.Lang.TF("nativePanel.wentOffline", userName));
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
                                System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to refresh friend online status on settings window: {ex}");
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
                else if (evt == "share_item_meta_updated" && data != null)
                {
                    // reference 分享：源模块元数据变更（title/itemData/type/groupId）
                    var itemServerId = (string?)data["itemServerId"];
                    if (!string.IsNullOrEmpty(itemServerId))
                        _ = HandleShareItemMetaUpdatedAsync(itemServerId!);
                }
                else if (evt == "share_source_bar_updated" && data != null)
                {
                    // bar 级 reference 分享：源窗口结构变更（模块增删/窗口消失）
                    var sourceBarId = (string?)data["sourceBarId"];
                    var action = (string?)data["action"] ?? "items_changed";
                    if (!string.IsNullOrEmpty(sourceBarId))
                        _ = HandleShareSourceBarUpdatedAsync(sourceBarId!, action);
                }
                else if (evt == "team_join_request" && data != null)
                {
                    // 团队加入申请（管理员收到）：写入通知中心（未读红点 + 卡片上直接同意/拒绝，点击主体跳设置团队页）
                    Services.TeamNotificationService.Instance.EnqueueJoinRequest(data);
                    Dispatcher.Invoke(() => NotifySettingsTeamChanged(membershipChanged: false));
                }
                else if (evt == "team_application_result" && data != null)
                {
                    // 加入申请审批结果（申请人收到）：通过/拒绝气泡；通过时成员关系变化需刷新团队切换下拉
                    bool accepted = data["accepted"]?.ToObject<bool>() == true;
                    var teamName = (string?)data["teamName"] ?? "";
                    // 同步写进通知中心（未读亮红点，点过即已读并回传服务端）
                    Services.NotificationSyncService.Instance.EnqueueLiveSilent("team_application_result", data);
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            try
                            {
                                mw.ShowBubbleNotification(accepted
                                    ? Framework.Lang.TF("nativePanel.teamJoinApproved", teamName)
                                    : Framework.Lang.TF("nativePanel.teamJoinDeclined", teamName));
                            }
                            catch { }
                        }
                        NotifySettingsTeamChanged(membershipChanged: accepted);
                    });
                }
                else if (evt == "team_invite" && data != null)
                {
                    // 团队邀请（被邀请人收到）：同样进通知中心，卡片上可直接接受/拒绝
                    Services.TeamNotificationService.Instance.EnqueueInvitation(data);
                    Dispatcher.Invoke(() => NotifySettingsTeamChanged(membershipChanged: false));
                }
                else if (evt == "team_member_joined" && data != null)
                {
                    // 成员入团通知（管理员/邀请人收到）
                    var memberName = (string?)data["memberName"] ?? Framework.Lang.T("team.newMember");
                    var teamName = (string?)data["teamName"] ?? "";
                    // 同步写进通知中心（参与已读状态与红点）
                    Services.NotificationSyncService.Instance.EnqueueLiveSilent("team_member_joined", data);
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var mw in _windows)
                        {
                            if (!mw.IsLoaded) continue;
                            try { mw.ShowBubbleNotification(Framework.Lang.TF("nativePanel.joinedTeam", memberName, teamName)); } catch { }
                        }
                        NotifySettingsTeamChanged(membershipChanged: false);
                    });
                }
                else if (evt == "bar_meta_updated" && data != null)
                {
                    // Web 端修改微屏元数据（当前为改名）：按 ServerId 回写本地窗口名、落盘并刷新顶部标签栏
                    var barServerId = (string?)data["barServerId"];
                    var title = (string?)data["title"];
                    if (!string.IsNullOrEmpty(barServerId) && !string.IsNullOrEmpty(title))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            WindowConfig? target = null;
                            foreach (var w in WindowConfigs.Windows)
                                if (w.ServerId == barServerId) { target = w; break; }
                            if (target == null || target.Name == title) return;
                            target.Name = title!;
                            WindowConfigs.Save();
                            RefreshAllWindowTabs();
                        });
                    }
                }
                else if (evt == "team_membership_revoked" && data != null)
                {
                    // 被移出团队/退出团队/团队解散：即时清除该团队全部本地数据（窗口/卡片/云盘源/变量集/数据项）
                    var corpId = (string?)data["corpId"];
                    if (!string.IsNullOrEmpty(corpId))
                        Dispatcher.Invoke(() => PurgeTeamDataLocal(corpId!));
                }
                else if (evt == "team_item_removed" && data != null)
                {
                    // 团队卡片被作者删除或改归属其他团队：成员端即时从 bar 移除该卡片
                    var itemServerId = (string?)data["itemServerId"];
                    if (!string.IsNullOrEmpty(itemServerId))
                        Dispatcher.Invoke(() => RemoveTeamItemLocal(itemServerId!));
                }
                else if (evt == "team_bar_removed" && data != null)
                {
                    // 团队窗口被删除：成员端即时移除本地对应团队窗口
                    var teamBarServerId = (string?)data["barServerId"];
                    if (!string.IsNullOrEmpty(teamBarServerId))
                        Dispatcher.Invoke(() => RemoveTeamWindowLocal(teamBarServerId!));
                }
                else if (data != null && (evt == "team_membership_notice" || evt == "team_item_notice" || evt == "share_revoked_notice"))
                {
                    // 团队/分享类用户通知：成员操作告知 + 「内容被移除」告知
                    HandleTeamNotice(evt!, data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] backend WS message parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// 团队/分享类用户通知，写入通知中心（未读红点、可回溯、点过即已读并回传服务端）+ 弹一次气泡。
        /// 同一入口承载两类语义，措辞差异由 BuildTeamNoticeText 按事件与 reason 区分：
        /// - 成员操作（新增/改名/内容修改/移动/恢复/加入团队/行数据变更）：只客观陈述「谁做了什么」；
        /// - 内容被移除（被移出团队/团队解散、卡片·微屏被作者删除或改归属、引用分享被取消）：本地数据已由配套指令
        ///   （team_membership_revoked / team_item_removed / team_bar_removed / share_cancelled）即时清除，文案说明「为什么没了」。
        /// 实时气泡与通知中心条目共用同一份文案，保证重启后从列表同步回来的文字一致。
        /// </summary>
        private void HandleTeamNotice(string evt, Newtonsoft.Json.Linq.JObject data)
        {
            try
            {
                var text = Services.NotificationSyncService.BuildTeamNoticeText(evt, data);
                if (string.IsNullOrEmpty(text)) return;
                Services.NotificationSyncService.Instance.EnqueueLiveSilent(evt, data);
                Dispatcher.Invoke(() =>
                {
                    foreach (var mw in _windows)
                    {
                        if (!mw.IsLoaded) continue;
                        try { mw.ShowBubbleNotification(text!); } catch { }
                    }
                });
                System.Diagnostics.Debug.WriteLine($"[App] team notice shown: {evt} :: {text}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to handle team notice {evt}: {ex.Message}");
            }
        }

        /// <summary>后端 WS 连接建立：拉取服务端通知列表同步已读状态（服务端为权威源，重启后红点自动恢复；接口不可用时降级为只补团队待办）；
        /// 并对账一次团队身份，补上离线期间错过的 team_membership_revoked 推送</summary>
        private void OnBackendWsConnected()
        {
            _ = Services.NotificationSyncService.Instance.SyncAsync();
            _ = ReconcileTeamMembershipAsync();
        }

        /// <summary>
        /// 团队身份兜底对账：成员被移出时客户端可能离线/正在重启，错过 team_membership_revoked 推送后
        /// 本地团队数据会永久残留（拉取对账只补缺、不删多余），因此每次 WS 连接建立（含重连）按服务端
        /// 团队列表清理本机已无身份的团队数据。仅在列表成功取回时执行 —— 接口失败/未登录返回 null，
        /// 此时宁可保留数据也绝不误删。
        /// </summary>
        private async System.Threading.Tasks.Task ReconcileTeamMembershipAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(AppSettings.AccountToken)) return;
                var teams = await Services.TeamService.Instance.ListTeamsAsync().ConfigureAwait(false);
                if (teams == null) return;
                var joined = new System.Collections.Generic.HashSet<string>(
                    teams.Where(t => !string.IsNullOrEmpty(t.Id)).Select(t => t.Id), StringComparer.Ordinal);

                // 本机带团队归属的全部 corpId：团队窗口 / 团队卡片 / 后台数据项 / 变量集 / 云盘源 / 当前选中团队
                var local = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var w in WindowConfigs.Windows)
                    {
                        if (w.OwnerType == "team" && !string.IsNullOrEmpty(w.CorpId)) local.Add(w.CorpId!);
                        foreach (var p in w.Panels) CollectPanelTeamIds(p, local);
                    }
                    foreach (var d in WindowConfigs.BackgroundDashboardItems)
                        if (!string.IsNullOrEmpty(d.TeamId)) local.Add(d.TeamId!);
                    foreach (var v in WindowConfigs.VariableSets)
                        if (!string.IsNullOrEmpty(v.TeamId)) local.Add(v.TeamId!);
                    foreach (var s in S3SourceManager.GetAll())
                        if (!string.IsNullOrEmpty(s.CorpId)) local.Add(s.CorpId!);
                    foreach (var s in WebDavSourceManager.GetAll())
                        if (!string.IsNullOrEmpty(s.CorpId)) local.Add(s.CorpId!);
                    if (!string.IsNullOrEmpty(AppSettings.SelectedCorpId)) local.Add(AppSettings.SelectedCorpId!);
                });

                foreach (var corpId in local.Where(id => !joined.Contains(id)).ToList())
                {
                    System.Diagnostics.Debug.WriteLine($"[App] team membership lost (offline reconcile), purging corp={corpId}");
                    await Dispatcher.InvokeAsync(() => PurgeTeamDataLocal(corpId));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] team membership reconcile failed: {ex.Message}");
            }
        }

        /// <summary>递归收集面板树中归属团队的 corpId（团队卡片可能嵌在个人窗口的分组里）</summary>
        private static void CollectPanelTeamIds(Models.PanelItem p, System.Collections.Generic.HashSet<string> sink)
        {
            if (p.OwnerType == "team" && !string.IsNullOrEmpty(p.TeamId)) sink.Add(p.TeamId!);
            if (p.ChildPanels == null) return;
            foreach (var c in p.ChildPanels) CollectPanelTeamIds(c, sink);
        }

        /// <summary>团队相关事件发生时刷新设置窗口（若打开）的团队页</summary>
        internal void NotifySettingsTeamChanged(bool membershipChanged)
        {
            foreach (Window w in Windows)
            {
                try
                {
                    if (w is SettingsWindow sw && sw.IsLoaded)
                        sw.OnTeamEventReceived(membershipChanged);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BackendWS] failed to refresh team page on settings window: {ex}");
                }
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
                    System.Diagnostics.Debug.WriteLine($"[App] forced full refresh {(changed ? "changed" : "no change")}: {target.Type}");
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
                System.Diagnostics.Debug.WriteLine($"[App] team row update applied: {target.Type}, {(mergeChanged ? "changed" : "no change")}");
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
                System.Diagnostics.Debug.WriteLine($"[App] failed to handle team row update: {ex.Message}");
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

        /// <summary>
        /// 处理源模块元数据变更通知（reference 分享）：拉取最新元数据 + 行数据，刷新本地面板。
        /// 触发场景：属主修改模块 title/itemData，或导入者（allowEdit）修改后属主与其他导入者同步。
        /// </summary>
        private async System.Threading.Tasks.Task HandleShareItemMetaUpdatedAsync(string itemServerId)
        {
            try
            {
                if (string.IsNullOrEmpty(AppSettings.AccountToken)) return;
                Models.PanelItem? target = null;
                foreach (var win in WindowConfigs.Windows)
                {
                    target = FindPanelByServerId(win.Panels, itemServerId);
                    if (target != null) break;
                }
                if (target == null) return;  // 本地未绑定该模块（可能是行级分享或已移除）

                bool metaChanged = await Services.CloudSyncService.Instance.PullSharedItemMetaAsync(target);
                bool rowsChanged = await Services.CloudSyncService.Instance.PullItemMergeFullAsync(target);
                System.Diagnostics.Debug.WriteLine($"[App] share item meta update applied: {target.Type}, meta={metaChanged}, rows={rowsChanged}");
                if (metaChanged || rowsChanged)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RefreshNativePanels();
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to handle share item meta update: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理源窗口结构变更通知（bar 级 reference 分享）：按 SourceBarId 定位入口窗口，diff 更新或整体移除。
        /// action='items_changed'：源窗口下模块增删，重拉源 bar items diff 本地面板；
        /// action='bar_removed'：源窗口整体消失，移除本地入口窗口（等同 share_cancelled(bar)）。
        /// </summary>
        private async System.Threading.Tasks.Task HandleShareSourceBarUpdatedAsync(string sourceBarId, string action)
        {
            try
            {
                if (string.IsNullOrEmpty(AppSettings.AccountToken)) return;

                if (action == "bar_removed")
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var win = WindowConfigs.Windows.FirstOrDefault(w => w.SourceBarId == sourceBarId);
                        if (win == null) return;
                        // 与 share_cancelled(bar) 同语义：窗口下的卡片均为源模块引用 → 本地行硬删不留墓碑，
                        // 窗口自身保留墓碑让服务端软删这个入口空壳（否则下次结构拉取会把它补回来）
                        var refItemUids = new System.Collections.Generic.List<string>();
                        foreach (var p in win.Panels) CollectPanelUids(p, refItemUids);
                        WindowConfigs.Windows.Remove(win);
                        Services.LocalDbService.HardDeleteRows(null, refItemUids);
                        WindowConfigs.Save();
                        Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RemoveSharedWindowById(win.Id);
                        System.Diagnostics.Debug.WriteLine($"[App] share source bar removed: {win.Name}");
                    }));
                    return;
                }

                // items_changed：定位入口窗口 → 拉源 bar items → diff 更新
                Models.WindowConfig? entry = null;
                foreach (var w in WindowConfigs.Windows)
                    if (w.SourceBarId == sourceBarId) { entry = w; break; }
                if (entry == null) return;

                bool changed = await Services.CloudSyncService.Instance.PullSharedSourceBarAsync(entry);
                System.Diagnostics.Debug.WriteLine($"[App] share source bar items changed: {entry.Name}, {(changed ? "updated" : "no change")}");
                if (changed)
                {
                    WindowConfigs.Save();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RefreshNativePanels();
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to handle share source bar update: {ex.Message}");
            }
        }

        /// <summary>
        /// 某个 bar（微屏）的组件被增删改后，刷新其他正在显示同一 bar 的侧边栏，使其同步呈现变更。
        /// 各侧边栏共享同一 WindowConfig 实例，故仅需重渲染即可反映变更；except 为发起变更的窗口（已自行渲染，跳过）。
        /// </summary>
        public void RefreshWindowsShowingBar(string barId, MainWindow? except)
        {
            if (string.IsNullOrEmpty(barId)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (!mw.IsLoaded || ReferenceEquals(mw, except)) continue;
                    if (mw.IsShowingBar(barId))
                        mw.RefreshNativePanels();
                }
            }));
        }

        /// <summary>刷新所有已加载主窗口顶部的窗口标签栏（微屏增删/改名等结构变更后调用，保证多窗口同步）</summary>
        public void RefreshAllWindowTabs()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var mw in _windows)
                {
                    if (mw.IsLoaded)
                        mw.RefreshWindowTabs();
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
                        // 引用窗口下的卡片全部是「源模块引用」（ServerId 指向分享者的模块），本地行必须硬删：
                        // 若留下墓碑，下次结构上传会把删除意图报到源模块上，而源模块属团队且本机是其成员时
                        // 服务端会判定本机有删除权 → 真删源模块（分享者与其他成员一起丢数据）。
                        // 窗口自身保留墓碑：它是导入时服务端为本人新建的入口空壳，需让服务端一并软删，
                        // 否则下次结构拉取会把这个空窗口补回来（复活→再清理反复抖动，且服务端残留脏行）。
                        var refItemUids = new System.Collections.Generic.List<string>();
                        foreach (var p in win.Panels) CollectPanelUids(p, refItemUids);
                        WindowConfigs.Windows.Remove(win);
                        Services.LocalDbService.HardDeleteRows(null, refItemUids);
                        WindowConfigs.Save();
                        Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RemoveSharedWindowById(win.Id);
                        System.Diagnostics.Debug.WriteLine($"[App] share cancelled: removing imported window localId={win.Id}, refItems={refItemUids.Count}");
                    }
                    else
                    {
                        var refItemUids = new System.Collections.Generic.List<string>();
                        bool removed = false;
                        foreach (var w in WindowConfigs.Windows)
                            removed |= RemoveSharedPanels(w.Panels, localId, itemServerId, refItemUids);
                        if (!removed) return;
                        // 同上：引用卡片的 ServerId 指向分享者的源模块，硬删避免墓碑误伤源数据
                        Services.LocalDbService.HardDeleteRows(null, refItemUids);
                        WindowConfigs.Save();
                        Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                        foreach (var mw in _windows)
                            if (mw.IsLoaded) mw.RefreshNativePanels();
                        System.Diagnostics.Debug.WriteLine($"[App] share cancelled: removing imported component localId={localId}, ServerId={itemServerId}, panels={refItemUids.Count}");
                    }
                }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to handle share-cancel notification: {ex.Message}");
            }
        }

        /// <summary>递归移除匹配的导入面板：优先按 localId 精确匹配；无 localId 时按 ServerId 匹配。
        /// sink 收集被移除面板（含其子面板）的本地 uid，供调用方硬删本地库行（引用面板的 ServerId 指向源模块，不得上报删除意图）</summary>
        private static bool RemoveSharedPanels(System.Collections.Generic.List<Models.PanelItem> panels, string localId, string itemServerId, System.Collections.Generic.List<string> sink)
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
                    CollectPanelUids(p, sink);
                    panels.RemoveAt(i);
                    removed = true;
                    continue;
                }
                if (p.ChildPanels != null)
                    removed |= RemoveSharedPanels(p.ChildPanels, localId, itemServerId, sink);
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

        #region 团队数据即时清理（后端 WS 推送驱动）

        /// <summary>
        /// 团队成员身份失效（被管理员移出 / 主动退出 / 团队解散）：清除该团队在本机的全部数据 ——
        /// 团队窗口与其下全部卡片、个人窗口里归属该团队的卡片、后台数据项、变量集（本地 + 云端登记）、
        /// 团队云盘源（S3/WebDAV）、团队数据模板；若当前选中的正是该团队则回落到「个人」。
        /// 本地库走行级硬删（不打删除墓碑）：服务端数据仍归属团队/作者，本设备只是失去访问权，
        /// 若上报删除意图会软删其他成员仍在使用的团队数据。
        /// </summary>
        private void PurgeTeamDataLocal(string corpId)
        {
            try
            {
                var removedBarUids = new System.Collections.Generic.List<string>();
                var removedItemUids = new System.Collections.Generic.List<string>();

                // 1) 团队窗口：整个窗口消失，其下卡片（无论归属）一并移除
                var teamWindows = WindowConfigs.Windows.Where(w => w.OwnerType == "team" && w.CorpId == corpId).ToList();
                foreach (var win in teamWindows)
                {
                    removedBarUids.Add(win.Id);
                    foreach (var p in win.Panels) CollectPanelUids(p, removedItemUids);
                    WindowConfigs.Windows.Remove(win);
                }

                // 2) 个人窗口里归属该团队的卡片（团队卡片可被放进个人窗口的分组内）
                foreach (var win in WindowConfigs.Windows)
                    RemoveTeamPanels(win.Panels, corpId, removedItemUids);

                // 3) 后台数据项与变量集（团队作用域）
                int bgRemoved = WindowConfigs.BackgroundDashboardItems.RemoveAll(d => d.TeamId == corpId);
                int setRemoved = WindowConfigs.VariableSets.RemoveAll(v => v.TeamId == corpId);

                // 4) 当前选中团队即被清除的团队 → 回落个人，避免继续按该团队拉团队库/云盘
                bool selectionReset = AppSettings.SelectedCorpId == corpId;
                if (selectionReset)
                {
                    AppSettings.SelectedCorpId = null;
                    AppSettings.SelectedCorpName = null;
                    AppSettings.Save();
                }

                // 5) 团队云盘源（仅存本机，服务端无副本，直接删）
                int driveRemoved = 0;
                foreach (var s in S3SourceManager.GetAll().Where(x => x.CorpId == corpId).ToList())
                {
                    S3SourceManager.Delete(s.Id);
                    driveRemoved++;
                }
                foreach (var s in WebDavSourceManager.GetAll().Where(x => x.CorpId == corpId).ToList())
                {
                    WebDavSourceManager.Delete(s.Id);
                    driveRemoved++;
                }

                // 6) 团队数据模板（用户自建模板文件；内置模板无团队归属，不动）
                PurgeTeamUserTemplates(corpId);

                // 7) 落盘：先硬删本地库行（不生成墓碑），再全量保存，随后重建轮询与变量聚合。
                // 仅在窗口/卡片结构确实变动时才 Save：Save 是「以内存为准」的全量重写，
                // 只改了云盘源/模板/选中团队时没必要重写，也避开启动早期内存尚未就绪的误删风险。
                bool configChanged = removedBarUids.Count > 0 || removedItemUids.Count > 0 || bgRemoved > 0 || setRemoved > 0;
                if (configChanged)
                {
                    Services.LocalDbService.HardDeleteRows(removedBarUids, removedItemUids);
                    WindowConfigs.Save();
                    Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                }
                Services.VariableSetService.Instance.PurgeTeam(corpId);

                // 8) UI：摘掉已移除团队窗口的标签并切走、重绘面板；团队下拉与云盘窗口一并刷新
                foreach (var mw in _windows)
                {
                    if (!mw.IsLoaded) continue;
                    foreach (var win in teamWindows) mw.RemoveSharedWindowById(win.Id);
                    mw.RefreshNativePanels();
                }
                if (selectionReset)
                {
                    // 正展示该团队云盘的窗口已无访问权，直接关闭（标题栏团队名也已失效）
                    foreach (Window w in Windows)
                    {
                        try { if (w is CloudDriveWindow && w.IsLoaded) w.Close(); } catch { }
                    }
                }
                NotifySettingsTeamChanged(membershipChanged: true);

                System.Diagnostics.Debug.WriteLine(
                    $"[App] team data purged: corp={corpId}, windows={teamWindows.Count}, items={removedItemUids.Count}, " +
                    $"bgItems={bgRemoved}, varSets={setRemoved}, drives={driveRemoved}, selectionReset={selectionReset}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to purge team data: {ex.Message}");
            }
        }

        /// <summary>
        /// 团队卡片被作者删除或改归属到其他团队：从本机所有窗口移除该卡片（连同其子面板）。
        /// 服务端已由作者完成删除/改归属，本地同样硬删不留墓碑。
        /// </summary>
        private void RemoveTeamItemLocal(string itemServerId)
        {
            try
            {
                var removedItemUids = new System.Collections.Generic.List<string>();
                bool removed = false;
                foreach (var win in WindowConfigs.Windows)
                    removed |= RemovePanelsByServerId(win.Panels, itemServerId, removedItemUids);
                if (!removed) return;

                Services.LocalDbService.HardDeleteRows(null, removedItemUids);
                WindowConfigs.Save();
                Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                foreach (var mw in _windows)
                    if (mw.IsLoaded) mw.RefreshNativePanels();
                System.Diagnostics.Debug.WriteLine($"[App] team item removed: serverId={itemServerId}, panels={removedItemUids.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to remove team item: {ex.Message}");
            }
        }

        /// <summary>团队窗口被删除：成员端移除本地对应窗口（含其下全部卡片与 dock/标签占用）</summary>
        private void RemoveTeamWindowLocal(string barServerId)
        {
            try
            {
                var win = WindowConfigs.Windows.FirstOrDefault(w => w.ServerId == barServerId);
                if (win == null) return;
                var removedItemUids = new System.Collections.Generic.List<string>();
                foreach (var p in win.Panels) CollectPanelUids(p, removedItemUids);
                WindowConfigs.Windows.Remove(win);

                Services.LocalDbService.HardDeleteRows(new[] { win.Id }, removedItemUids);
                WindowConfigs.Save();
                Services.DataPollingService.Instance.SyncFromConfigs(WindowConfigs);
                foreach (var mw in _windows)
                    if (mw.IsLoaded) mw.RemoveSharedWindowById(win.Id);
                System.Diagnostics.Debug.WriteLine($"[App] team window removed: {win.Name} (serverId={barServerId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to remove team window: {ex.Message}");
            }
        }

        /// <summary>清除用户自建数据模板中归属该团队的模板（内置模板随版本部署、无团队归属，不动）</summary>
        private static void PurgeTeamUserTemplates(string corpId)
        {
            try
            {
                string path = Models.DataTemplateManager.UserTemplatesFilePath;
                if (!System.IO.File.Exists(path)) return;
                var list = Models.DataTemplateManager.LoadUserTemplates();
                int before = list.Count;
                list.RemoveAll(t => t.TeamId == corpId);
                if (list.Count == before) return;
                System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] failed to purge team data templates: {ex.Message}");
            }
        }

        /// <summary>收集面板及其全部子面板的本地 uid（供本地库硬删定位行）</summary>
        private static void CollectPanelUids(Models.PanelItem p, System.Collections.Generic.List<string> sink)
        {
            sink.Add(p.Id);
            if (p.ChildPanels == null) return;
            foreach (var c in p.ChildPanels) CollectPanelUids(c, sink);
        }

        /// <summary>递归移除归属指定团队的卡片（团队卡片可能嵌在个人窗口的分组里）</summary>
        private static void RemoveTeamPanels(System.Collections.Generic.List<Models.PanelItem> panels, string corpId, System.Collections.Generic.List<string> sink)
        {
            for (int i = panels.Count - 1; i >= 0; i--)
            {
                var p = panels[i];
                if (p.OwnerType == "team" && p.TeamId == corpId)
                {
                    CollectPanelUids(p, sink);
                    panels.RemoveAt(i);
                    continue;
                }
                if (p.ChildPanels != null) RemoveTeamPanels(p.ChildPanels, corpId, sink);
            }
        }

        /// <summary>按 ServerId 递归移除卡片（连同其子面板），返回是否移除了至少一个</summary>
        private static bool RemovePanelsByServerId(System.Collections.Generic.List<Models.PanelItem> panels, string serverId, System.Collections.Generic.List<string> sink)
        {
            bool removed = false;
            for (int i = panels.Count - 1; i >= 0; i--)
            {
                var p = panels[i];
                if (p.ServerId == serverId)
                {
                    CollectPanelUids(p, sink);
                    panels.RemoveAt(i);
                    removed = true;
                    continue;
                }
                if (p.ChildPanels != null) removed |= RemovePanelsByServerId(p.ChildPanels, serverId, sink);
            }
            return removed;
        }

        #endregion

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
                message = plugin != null ? Framework.Lang.TF("nativePanel.pluginLoaded", plugin.Config.Name) : Framework.Lang.TF("nativePanel.failedToLoadPlugin", pluginId)
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
                message = Framework.Lang.TF("nativePanel.pluginUnloaded", pluginId)
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
                    message = Framework.Lang.T("nativePanel.pluginNotCompiled")
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
                    Title = Framework.Lang.T("nativePanel.savePlugin"),
                    Filter = Framework.Lang.T("settings.pluginZipFilter"),
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
                    message = Framework.Lang.T("nativePanel.saveCancelled")
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
                    message = Framework.Lang.TF("nativePanel.pluginSaved", savePath),
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
                    message = Framework.Lang.TF("nativePanel.failedToSave", ex.Message)
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
                    message = Framework.Lang.T("nativePanel.theProjectDirectoryDoes")
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
            var window = new MainWindow(side, monitorIndex);
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
                    System.Diagnostics.Debug.WriteLine($"[AppBar] found {staleProcesses.Count} stale SideDesk processes, cleaning up...");
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
                System.Diagnostics.Debug.WriteLine($"[AppBar] failed to clean stale processes: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("[AppBar] workspace reset timed out, giving up waiting (continuing in background)");
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
                System.Diagnostics.Debug.WriteLine($"[AppBar] workspace reset failed: {ex.Message}");
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

        /// <summary>
        /// 传给前端内嵌页（市场 / 编辑器等 WebView2 页面）的主题标识（dark / light），
        /// 供页面按宿主当前主题渲染，取值已考虑 System 模式与 JSON 自定义主题覆盖。
        /// </summary>
        public static string FrontendThemeParam => IsCurrentThemeLight() ? "light" : "dark";

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
                Framework.Lang.TF("nativePanel.anErrorOccurred", e.Exception.Message, e.Exception.StackTrace),
                Framework.Lang.T("crash.programException"),
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
                    "crash.log");
                
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
                    File.AppendAllText(crashLog, $"""
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
        /// 每次启动清空上一次运行留下的调试日志：日志不再按天分文件（统一 window.log / crash.log /
        /// rendering.log / http.log / keyboard.log / lang-missing.log 等固定名），重开即清，只保留本次运行的记录。
        /// 例外：windowlog_*.txt 是「使用时长分析 / 屏幕时长」功能按天累积的历史数据源，必须保留、且仍按天分文件。
        /// </summary>
        private static void ClearDebugLogsOnStartup()
        {
            try
            {
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "Logs");
                if (!Directory.Exists(logsDir)) return;
                foreach (var f in Directory.GetFiles(logsDir))
                {
                    string name = Path.GetFileName(f);
                    // 保留窗口使用时长数据（windowlog_yyyyMMdd.txt），其余调试日志一律清掉
                    if (name.StartsWith("windowlog_", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
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
                
                string crashLog = Path.Combine(logDir, "crash.log");
                File.AppendAllText(crashLog, logLine);
            }
            catch { }
        }

        /// <summary>
        /// 渲染管线回退：WPF 默认走 DirectX 硬件加速，若显卡驱动有 Bug 导致“握手失败”，
        /// 弹窗内容会渲染成空白。此处支持命令行 --software-rendering 或设置项强制切换软件渲染，
        /// 并输出渲染层级 / DWM / 显卡驱动诊断信息到日志便于排查。
        /// </summary>
        private void ApplyRenderingModeFallback(string[] args)
        {
            bool cliForce = args != null && args.Any(a => string.Equals(a, "--software-rendering", StringComparison.OrdinalIgnoreCase));

            // 命令行参数优先，并持久化到设置（下次启动无需再带参数）
            if (cliForce && !AppSettings.ForceSoftwareRendering)
            {
                AppSettings.ForceSoftwareRendering = true;
                AppSettings.Save();
            }

            // 自动回退：主显示器为虚拟/远程显示驱动时，WPF 硬件加速易握手失败导致窗口空白
            string? primaryAdapter = GetPrimaryDisplayAdapter();
            bool autoSoftware = primaryAdapter != null && LooksLikeVirtualDisplayAdapter(primaryAdapter);

            bool useSoftware = AppSettings.ForceSoftwareRendering || autoSoftware;
            if (useSoftware)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            System.Diagnostics.Debug.WriteLine($"[Render] render mode: {(useSoftware ? "software" : "default (hardware acceleration)")} (forced={AppSettings.ForceSoftwareRendering} auto={autoSoftware} primaryAdapter={primaryAdapter ?? "unknown"})");

            // 异步诊断（WMI 查显卡驱动较慢），完成后写日志并按需弹窗提示
            Dispatcher.BeginInvoke(new Action(() => DiagnoseRendering(AppSettings.ForceSoftwareRendering, autoSoftware, primaryAdapter)));
        }

        /// <summary>枚举显示适配器，返回主显示器（PRIMARY_DEVICE）的适配器名称</summary>
        private static string? GetPrimaryDisplayAdapter()
        {
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var d = new NativeMethods.DISPLAY_DEVICE();
                    d.cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));
                    if (!NativeMethods.EnumDisplayDevices(null, i, ref d, 0)) break;
                    if ((d.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                        return d.DeviceString;
                }
            }
            catch { }
            return null;
        }

        /// <summary>判断适配器名称是否为虚拟/远程显示驱动（此类驱动下 WPF 硬件加速易空白）</summary>
        private static bool LooksLikeVirtualDisplayAdapter(string name)
        {
            foreach (var kw in new[] { "virtual", "todesk", "oray", "gameviewer", "remotefx", "citrix", "vmware", "virtualbox", "hyper-v", "idd", "remote display" })
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>收集渲染管线诊断信息（Tier / DWM / 显卡驱动），写入 Logs 并输出提示</summary>
        private void DiagnoseRendering(bool forcedSoftware, bool autoSoftware, string? primaryAdapter)
        {
            try
            {
                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                string tierText = tier switch
                {
                    0 => "0（无硬件加速，全部软件渲染）",
                    1 => "1（部分硬件加速）",
                    _ => "2（完全硬件加速）"
                };

                bool dwmEnabled = true;
                try
                {
                    if (NativeMethods.DwmIsCompositionEnabled(out bool enabled) == 0) dwmEnabled = enabled;
                }
                catch { }

                var lines = new System.Collections.Generic.List<string>
                {
                    $"渲染层级 Tier: {tierText}",
                    $"ProcessRenderMode: {System.Windows.Media.RenderOptions.ProcessRenderMode}",
                    $"DWM 桌面合成: {(dwmEnabled ? "已启用" : "已禁用")}",
                    $"强制软件渲染: {(forcedSoftware ? "是" : "否")}"
                };

                // 显卡与驱动信息：WMI 查询较慢，放线程池，完成后回 UI 线程写日志 + 提示
                _ = Task.Run(() =>
                {
                    string gpuInfo;
                    try
                    {
                        var items = new System.Collections.Generic.List<string>();
                        using var searcher = new System.Management.ManagementObjectSearcher(
                            "SELECT Name, DriverVersion FROM Win32_VideoController");
                        foreach (var mo in searcher.Get())
                            items.Add($"{mo["Name"]}（驱动 {mo["DriverVersion"]}）");
                        gpuInfo = items.Count > 0 ? string.Join("; ", items) : "未检测到";
                    }
                    catch (Exception ex) { gpuInfo = $"查询失败: {ex.Message}"; }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lines.Add($"显卡: {gpuInfo}");
                        WriteRenderingLog(lines);
                        foreach (var l in lines)
                            System.Diagnostics.Debug.WriteLine($"[Render] {l}");

                        if (forcedSoftware)
                        {
                            NotifyRenderingFallback(Framework.Lang.T("nativePanel.softwareRenderingNotice"));
                        }
                        else if (autoSoftware)
                        {
                            NotifyRenderingFallback(Framework.Lang.TF("nativePanel.virtualDisplayDetected", primaryAdapter ?? ""));
                        }
                        else if (tier < 2)
                        {
                            NotifyRenderingFallback(Framework.Lang.T("nativePanel.wpfAccelerationUnavailable"));
                        }
                    }));
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Render] diagnostics failed: {ex.Message}");
            }
        }

        /// <summary>渲染诊断信息追加写入 %LocalAppData%\SideDesk\Logs\rendering.log</summary>
        private static void WriteRenderingLog(System.Collections.Generic.List<string> lines)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "Logs");
                Directory.CreateDirectory(logDir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 渲染管线诊断");
                foreach (var l in lines) sb.AppendLine("  " + l);
                File.AppendAllText(Path.Combine(logDir, "rendering.log"), sb.ToString());
            }
            catch { }
        }

        /// <summary>渲染异常提示弹窗（优先以已打开窗口为 owner）</summary>
        private void NotifyRenderingFallback(string message)
        {
            try
            {
                Window? owner = null;
                foreach (Window w in Windows) { owner = w; break; }
                ThemedMessageBox.Show(owner, message, Framework.Lang.T("nativePanel.renderingModeNotice"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Render] failed to show prompt dialog: {ex.Message}");
            }
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
                contextMenu.Items.Add(Framework.Lang.T("common.settings"), null, (s, e) => OpenSettings());

                // 软件渲染模式开关（禁用 GPU 加速，重启应用后生效）
                var softwareRenderItem = new System.Windows.Forms.ToolStripMenuItem(Framework.Lang.T("settings.softwareRenderingMode"))
                {
                    CheckOnClick = true,
                    Checked = AppSettings.ForceSoftwareRendering
                };
                softwareRenderItem.Click += (s, e) =>
                {
                    AppSettings.ForceSoftwareRendering = softwareRenderItem.Checked;
                    AppSettings.Save();
                    MessageBox.Show(
                        Framework.Lang.T(softwareRenderItem.Checked
                            ? "settings.softwareRenderingEnabled"
                            : "settings.softwareRenderingDisabled"),
                        Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                };
                contextMenu.Items.Add(softwareRenderItem);

                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(Framework.Lang.T("nativePanel.exitMetadeskScreen"), null, (s, e) => CloseAll());

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = appIcon ?? System.Drawing.SystemIcons.Application,
                    Text = Framework.Lang.T("common.metadeskScreen"),
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
                System.Diagnostics.Debug.WriteLine($"[NotifyIcon] init failed: {ex.Message}");
            }
        }

        #endregion
    }
}
