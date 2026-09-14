using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ChromeProtocol.Domains;
using ChromeProtocol.Runtime.Messaging.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// 数据看板连接状态
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// 浏览器 CDP 连接（按 browserId+profileId 分组）
/// </summary>
public class BrowserConnection
{
    public string Key { get; set; } = "";                // "{browserId}_{profileId}" 或 "default"
    public DefaultProtocolClient? Client { get; set; }
    public Process? LaunchedProcess { get; set; }        // 自启动的隐藏浏览器进程（null=连接的用户浏览器）
    public int Port { get; set; }
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
    public bool IsSelfLaunched => LaunchedProcess != null;
}

/// <summary>
/// 数据轮询服务 - 中央数据源，统一管理所有数据看板项的定时抓取。
/// 面板卡片只负责展示，不发起请求。
/// </summary>
public class DataPollingService : IDisposable
{
    private static readonly Lazy<DataPollingService> _instance = new(() => new DataPollingService());
    public static DataPollingService Instance => _instance.Value;

    /// <summary>数据项更新事件 (itemId, newValue)</summary>
    public event Action<string, string>? DataUpdated;

    /// <summary>连接状态变化事件 (connectionKey, newState)</summary>
    public event Action<string, ConnectionState>? ConnectionStateChanged;

    // 所有数据项（按身份去重：同一看板模板/同一变量分布在多个窗口时只轮询一次）
    private readonly List<DataDashboardItem> _items = new();
    // 数据项分组：身份 key -> 所有重复实例（含轮询代表实例）。
    // 值更新后同步给全部重复实例并按各自 Id 发事件，保证多个微屏窗口的面板同步刷新。
    private readonly Dictionary<string, List<DataDashboardItem>> _itemGroups = new();
    private readonly object _itemsLock = new();

    // 每个去重后数据源一个定时器：相同看板（同一数据源）只起一个轮询实例，不同看板各跑各的
    private readonly Dictionary<string, DispatcherTimer> _timers = new();
    // “是否允许远程调试?”确认弹窗自动允许：开启数据卡片时每 5 秒扫一次，检测到就模拟点击“允许”；
    // _lastRemoteDebugNotified 限流气泡通知（15 秒内只提一次）
    private DispatcherTimer? _remoteDebugPromptTimer;
    private DateTime _lastRemoteDebugNotified = DateTime.MinValue;
    // 正在轮询中的数据项（防重叠：上一次抓取未完成时跳过本轮，避免重复建连/重复打开页面）
    private static readonly ConcurrentDictionary<string, byte> _pollingItems = new();

    // CDP 连接池: key = "{browserId}_{profileId}" 或 "default"
    private readonly Dictionary<string, BrowserConnection> _connections = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private int _nextAutoPort = 9300;

    // 全局建连计数器（用于诊断：每建立一条 CDP 连接就 +1，浏览器每收到一条新连接都会弹一次提示）
    private static int _totalConnections = 0;

    // 自动启动浏览器尝试记录：key = "{connectionKey}|{targetUrl}"（app 模式）或 "hiddenlaunch|{connectionKey}"（隐藏浏览器）。
    // value 存尝试次数：app 模式打开目标页每会话最多自动尝试 MaxAutoOpenAttempts 次（页面加载慢/重定向等
    // 首次等待超时的场景还有机会）；隐藏浏览器只自动尝试 1 次。预算用尽后安静显示 --，不再反复拉起浏览器；
    // 手动「立即刷新」重置该数据源预算。注意：SyncFromConfigs 不得清空该记录——新数据源的 key 本来就不在记录里、
    // 首次轮询自然允许尝试；若清空会导致每次增删改数据项后所有数据源重新拉起浏览器。
    private const byte MaxAutoOpenAttempts = 3;
    private static readonly ConcurrentDictionary<string, byte> _autoLaunchDone = new();
    // 防并发启动：已有启动流程在执行时，重叠的轮询跳过
    private static readonly ConcurrentDictionary<string, byte> _launchingItems = new();

    // 本服务拉起/打开的浏览器资源记录，关闭软件时统一清理：
    // - _launchedProcesses：自启动的浏览器进程（隐藏浏览器 / app 模式独立实例；委托给已运行浏览器而自行退出的进程自然跳过，不会误杀用户浏览器）
    // - _openedAppTargets：在浏览器中以 app 模式打开的页面 target（含网址，供点击时复用显示），退出时经 CDP 逐个关闭
    private static readonly ConcurrentBag<Process> _launchedProcesses = new();
    private static readonly List<(DefaultProtocolClient client, Target.TargetIDType targetId, string url)> _openedAppTargets = new();
    private static readonly object _openedAppTargetsLock = new();
    // 以 app 模式启动的窗口进程（含网址）：窗口属于我们启动的进程时直接用 Win32 激活显示，无需 CDP
    private static readonly List<(Process proc, string url)> _appModeWindows = new();
    private static readonly object _appModeWindowsLock = new();
    // app 模式隐藏新窗口（Win32）：--app 常委托给已运行浏览器、启动进程立即退出，拿不到进程窗口句柄；
    // 改为每次启动各自快照白名单（互不干扰），启动后短轮询把新出现的同浏览器窗口 ShowWindow(SW_HIDE)。
    // 记录被隐藏窗口（句柄 -> 网址）：点击对应数据项时按网址恢复显示
    private static readonly Dictionary<IntPtr, string> _appModeHiddenHwnds = new();
    // 经 CDP 窗口控制隐藏（最小化）的 app 窗口 target：委托给已运行浏览器的窗口无法用进程句柄隐藏，
    // 改用 CDP Browser.SetWindowBounds 最小化；点击数据项显示时先恢复窗口状态
    private static readonly ConcurrentDictionary<Target.TargetIDType, byte> _cdpHiddenTargets = new();

    // 手动值层：varKey -> value（外部程序经本地 HTTP 接口 / 后端推送设置）。
    // 手动值优先于轮询/聚合结果：设置后该变量不再被轮询覆盖，直到 ClearVariableValue 解除；持久化到本地库。
    private readonly Dictionary<string, string> _manualValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _manualLock = new();
    private bool _manualLoaded;
    /// <summary>已加载的手动值所属作用域；与当前作用域不一致时（切换登录/退出）重新加载</summary>
    private string? _manualScopeLoaded;
    private const string ManualValuesStoreKey = "manual_var_values";

    /// <summary>
    /// 当前数据作用域：登录后用 /auth/userinfo 返回的 encrypt2 密文 userId（"u:{密文}"）；
    /// 未登录（含 userinfo 未返回）为未归属作用域 "local"，登录后 local 数据自动晋升归属当前用户。
    /// 本地手动值按作用域隔离，防止切换登录账号后串号。
    /// </summary>
    private static string CurrentDataScope()
    {
        var settings = ((App)System.Windows.Application.Current)?.AppSettings;
        if (!string.IsNullOrEmpty(settings?.AccountUserId)) return "u:" + settings.AccountUserId;
        return "local";
    }

    private DataPollingService()
    {
        // 后端推送 data_variable_value_changed（外部程序经后端接口改了变量值）→ 本地立即生效
        BackendWebSocketService.Instance.MessageReceived += OnBackendMessage;
    }

    /// <summary>处理后端 WebSocket 推送的变量值变更消息</summary>
    private void OnBackendMessage(string text)
    {
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(text);
            if ((string?)obj["event"] != "data_variable_value_changed") return;
            var data = obj["data"];
            // 兼容新旧字段名：key/value（新）与 varKey/varValue（旧）
            var varKey = (string?)data?["key"] ?? (string?)data?["varKey"];
            if (string.IsNullOrWhiteSpace(varKey)) return;
            var varValue = (string?)data?["value"] ?? (string?)data?["varValue"];
            if (varValue == null) ClearVariableValue(varKey);
            else SetVariableValue(varKey, varValue);
            Debug.WriteLine($"[DataPolling] applied backend-pushed variable value: {varKey} = {varValue ?? "(manual value cleared)"}");
        }
        catch { /* 非 JSON 或其它消息：忽略 */ }
    }

    // ==================== 手动变量值（外部设置优先于轮询） ====================

    /// <summary>从本地库加载当前作用域的手动值（懒加载；作用域变化时清空重载，防止切换账号串号）</summary>
    private void EnsureManualLoaded(string scope)
    {
        List<(string VarKey, string VarValue, string? CorpId)>? adopted = null;
        lock (_manualLock)
        {
            if (_manualLoaded && _manualScopeLoaded == scope) return;

            // 未归属数据晋升：登录用户首次加载时，把未登录期间产生的 local 作用域数据归入本人作用域
            //（离线数据“跟人走”，同名 key 离线值更新故覆盖已有；加载完成后转发服务器）
            if (scope != "local")
            {
                try
                {
                    adopted = LocalDbService.LoadDataVariables("local");
                    foreach (var (k, v, c) in adopted)
                    {
                        if (string.IsNullOrWhiteSpace(k)) continue;
                        LocalDbService.UpsertDataVariable(scope, k, v, c);
                        LocalDbService.DeleteDataVariable("local", k);
                    }
                }
                catch (Exception ex)
                {
                    adopted = null;
                    Debug.WriteLine($"[DataPolling] failed to promote orphaned data: {ex.Message}");
                }
            }

            _manualLoaded = true;
            _manualScopeLoaded = scope;
            _manualValues.Clear();
            try
            {
                foreach (var (k, v, _) in LocalDbService.LoadDataVariables(scope))
                    if (!string.IsNullOrWhiteSpace(k)) _manualValues[k] = v;

                // 旧存储迁移：local_app_state 里的 JSON blob → 逐条写入新表（归入当前作用域）
                if (_manualValues.Count == 0)
                {
                    var json = LocalDbService.LoadBusinessData(ManualValuesStoreKey);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                        foreach (var p in jo.Properties())
                        {
                            if (string.IsNullOrWhiteSpace(p.Name) || p.Value == null) continue;
                            var val = p.Value.ToString();
                            _manualValues[p.Name] = val;
                            LocalDbService.UpsertDataVariable(scope, p.Name, val, null);
                        }
                        LocalDbService.DeleteBusinessData(ManualValuesStoreKey);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[DataPolling] failed to load manual variable values: {ex.Message}"); }
        }

        // 晋升的数据补传服务器（重放离线期间的写入；个人/团队作用域按随行 corpId 区分），fire-and-forget
        if (adopted != null && adopted.Count > 0)
            _ = ForwardAdoptedVariablesAsync(adopted);
    }

    /// <summary>
    /// 把晋升的未归属数据逐条转发后端（复用本地 HTTP 通道的带签转发；失败仅记日志，本地归属已完成不受影响）。
    /// </summary>
    private static async Task ForwardAdoptedVariablesAsync(List<(string VarKey, string VarValue, string? CorpId)> rows)
    {
        foreach (var (k, v, c) in rows)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            try
            {
                var (ok, msg) = await HttpServerService.ForwardVariableToBackendAsync(k, v, c).ConfigureAwait(false);
                if (ok) Debug.WriteLine($"[DataPolling] promoted variable backfilled: {k}");
                else Debug.WriteLine($"[DataPolling] promoted variable backfill failed {k}: {msg}");
            }
            catch (Exception ex) { Debug.WriteLine($"[DataPolling] promoted variable backfill error {k}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 登录成功/切换账号后调用：把未归属（local）数据晋升到当前用户作用域并补传服务器，同时把值立即应用到同名运行中变量。
    /// </summary>
    public void AdoptPendingLocalData()
    {
        var scope = CurrentDataScope();
        if (scope != "local")
        {
            // 布局与内容类数据（窗口/卡片/便签/待办/剪贴板收藏等）同步晋升：未归属 local 数据归入当前登录者
            try { LocalDbService.AdoptScope("local", scope); }
            catch (Exception ex) { Debug.WriteLine($"[DataPolling] local-db scoped promotion failed: {ex.Message}"); }
        }
        EnsureManualLoaded(scope);
        if (scope == "local") return;
        // 晋升的值立即生效到同名变量（须在锁外：UpdateItemValue 内部会取 _itemsLock）
        foreach (var item in GetAllItems())
        {
            if (string.IsNullOrEmpty(item.VarKey)) continue;
            var manual = GetManualValue(item.VarKey!);
            if (manual != null) UpdateItemValue(item, manual);
        }
    }

    /// <summary>
    /// 手动设置变量值（本地 HTTP 接口 / 后端推送调用）。
    /// 手动值优先：设置后轮询与变量集聚合都不再覆盖该变量，直到 ClearVariableValue 解除。
    /// corpId（团队密文，可空）随行存入 local_data_variable；返回是否找到同名运行中变量（未找到时值仍会记录，变量出现后自动生效）。
    /// </summary>
    public bool SetVariableValue(string varKey, string value, string? corpId = null)
    {
        if (string.IsNullOrWhiteSpace(varKey)) return false;
        varKey = varKey.Trim();

        var scope = CurrentDataScope();
        EnsureManualLoaded(scope);
        lock (_manualLock) _manualValues[varKey] = value;
        try { LocalDbService.UpsertDataVariable(scope, varKey, value, string.IsNullOrWhiteSpace(corpId) ? null : corpId); }
        catch (Exception ex) { Debug.WriteLine($"[DataPolling] failed to save manual variable values: {ex.Message}"); }

        var item = FindItemByVarKey(varKey);
        if (item == null) return false;
        UpdateItemValue(item, value);
        return true;
    }

    /// <summary>解除手动值：变量恢复轮询/聚合自动取值。返回是否确实存在手动值</summary>
    public bool ClearVariableValue(string varKey)
    {
        if (string.IsNullOrWhiteSpace(varKey)) return false;
        var scope = CurrentDataScope();
        EnsureManualLoaded(scope);
        bool removed;
        lock (_manualLock) removed = _manualValues.Remove(varKey.Trim());
        if (!removed) return false;
        try { LocalDbService.DeleteDataVariable(scope, varKey.Trim()); }
        catch (Exception ex) { Debug.WriteLine($"[DataPolling] failed to delete manual variable values: {ex.Message}"); }
        return true;
    }

    /// <summary>获取指定变量的手动值（null = 无手动覆盖）</summary>
    public string? GetManualValue(string varKey)
    {
        if (string.IsNullOrWhiteSpace(varKey)) return null;
        EnsureManualLoaded(CurrentDataScope());
        lock (_manualLock) return _manualValues.TryGetValue(varKey.Trim(), out var v) ? v : null;
    }

    /// <summary>全部变量快照（轮询项 + 变量集虚拟项 + 仅有手动值的变量），供本地 HTTP 接口列出</summary>
    public List<(string VarKey, string Value, string Label, bool Manual)> GetVariableSnapshot()
    {
        var dict = new Dictionary<string, (string Value, string Label, bool Manual)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in GetAllItems())
            if (!string.IsNullOrEmpty(item.VarKey))
                dict[item.VarKey!] = (item.Value ?? "--", item.Label ?? "", GetManualValue(item.VarKey!) != null);
        foreach (var item in VariableSetService.Instance.GetItems())
            if (!string.IsNullOrEmpty(item.VarKey) && !dict.ContainsKey(item.VarKey!))
                dict[item.VarKey!] = (item.Value ?? "--", item.Label ?? "", GetManualValue(item.VarKey!) != null);
        EnsureManualLoaded(CurrentDataScope());
        lock (_manualLock)
            foreach (var kv in _manualValues)
                if (!dict.ContainsKey(kv.Key)) dict[kv.Key] = (kv.Value, "", true);
        return dict.Select(kv => (kv.Key, kv.Value.Value, kv.Value.Label, kv.Value.Manual)).ToList();
    }

    /// <summary>
    /// 数据项身份标识 = 数据卡片（后端实例）：同模板+同浏览器+同网址/选择器即同一数据卡片，
    /// 同一卡片出现在多个微屏窗口时共享同一个轮询实例。
    /// 变量名（VarKey）是卡片的属性、不参与身份——避免同一卡片因一个卡片设了变量、另一个没设而被拆成两个实例。
    /// </summary>
    public static string GetItemIdentity(DataDashboardItem item)
    {
        return $"tpl|{item.TemplateId}|{item.BrowserId ?? ""}|{item.ProfileId ?? ""}|{item.TargetUrl}|{item.SelectorType ?? ""}|{item.Selector}|{item.ApiMethod ?? ""}|{item.ResponseExtract ?? ""}".ToLowerInvariant();
    }

    /// <summary>
    /// 从 WindowConfigs 同步数据项（面板项 + 后台项），按身份去重后重建定时器
    /// </summary>
    public void SyncFromConfigs(WindowConfigManager configs)
    {
        int totalBefore;
        lock (_itemsLock)
        {
            _items.Clear();
            _itemGroups.Clear();

            var candidates = new List<DataDashboardItem>();

            // 面板数据项（引用项仅展示、不轮询，不纳入）
            foreach (var win in configs.Windows)
            {
                foreach (var panel in win.Panels)
                {
                    if (panel.DashboardItems == null || panel.DashboardItems.Count == 0) continue;
                    candidates.AddRange(panel.DashboardItems.Where(d => string.IsNullOrEmpty(d.RefVarKey)));
                }
            }

            // 后台数据项
            candidates.AddRange(configs.BackgroundDashboardItems.Where(d => string.IsNullOrEmpty(d.RefVarKey)));

            totalBefore = candidates.Count;

            // 按身份去重：同一数据卡片只保留一个轮询实例，同时记录全部重复实例用于值/事件同步
            foreach (var d in candidates)
            {
                var key = GetItemIdentity(d);
                if (!_itemGroups.TryGetValue(key, out var group))
                {
                    group = new List<DataDashboardItem>();
                    _itemGroups[key] = group;
                    _items.Add(d);
                }
                else if (!string.IsNullOrEmpty(d.VarKey) && string.IsNullOrEmpty(group[0].VarKey))
                {
                    // 变量是卡片属性：代表实例没变量而新成员有则提升为代表，保证按变量名可查到该卡片
                    var idx = _items.IndexOf(group[0]);
                    if (idx >= 0) _items[idx] = d;
                }
                group.Add(d);
            }
        }

        // 新数据源的启动 key 不在 _autoLaunchDone 中，首次轮询自然允许自动尝试一次；
        // 已有数据源失败后保持安静，配置变更不重置该限制（避免增删改后浏览器反复弹出）。

        // 恢复手动值：配置重建后把外部设置的值重新应用到匹配变量（须在锁外：UpdateItemValue 内部会取 _itemsLock）
        EnsureManualLoaded(CurrentDataScope());
        foreach (var item in GetAllItems())
        {
            if (string.IsNullOrEmpty(item.VarKey)) continue;
            var manual = GetManualValue(item.VarKey!);
            if (manual != null) UpdateItemValue(item, manual);
        }

        RebuildTimers();
        Debug.WriteLine($"[DataPolling] SyncFromConfigs: {totalBefore} data items in total, {_items.Count} polling instances after dedup");
    }

    /// <summary>获取当前所有数据项（只读副本引用）</summary>
    public List<DataDashboardItem> GetAllItems()
    {
        lock (_itemsLock) return new List<DataDashboardItem>(_items);
    }

    /// <summary>按变量名查找实际轮询中的数据项（供引用项展示取值；找不到再查变量集虚拟项；仍无返回 null）</summary>
    public DataDashboardItem? FindItemByVarKey(string? varKey)
    {
        if (string.IsNullOrEmpty(varKey)) return null;
        lock (_itemsLock)
        {
            var found = _items.FirstOrDefault(i => string.Equals(i.VarKey, varKey, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }
        return VariableSetService.Instance.FindItemByVarKey(varKey);
    }

    /// <summary>广播外部数据项（如变量集虚拟项）的值更新，供订阅方（引用展示/格式渲染/信号灯）刷新</summary>
    public void PublishUpdate(string itemId, string value) => DataUpdated?.Invoke(itemId, value);

    /// <summary>获取指定数据项的连接状态</summary>
    public ConnectionState GetItemState(DataDashboardItem item)
    {
        var key = GetConnectionKey(item);
        lock (_connections)
        {
            return _connections.TryGetValue(key, out var conn) ? conn.State : ConnectionState.Disconnected;
        }
    }

    /// <summary>获取指定数据项的连接 key</summary>
    public string GetConnectionKey(DataDashboardItem item)
    {
        if (!string.IsNullOrEmpty(item.BrowserId))
            return $"{item.BrowserId}_{item.ProfileId ?? ""}";
        return "default";
    }

    /// <summary>获取指定连接 key 的 BrowserConnection</summary>
    public BrowserConnection? GetConnection(string key)
    {
        lock (_connections)
        {
            return _connections.TryGetValue(key, out var conn) ? conn : null;
        }
    }

    /// <summary>
    /// 断开指定连接。如果是自启动的隐藏浏览器则关闭进程。
    /// </summary>
    public void DisconnectConnection(string key)
    {
        BrowserConnection? conn;
        lock (_connections)
        {
            _connections.TryGetValue(key, out conn);
            _connections.Remove(key);
        }

        if (conn == null) return;

        try { conn.Client?.Dispose(); } catch { }

        if (conn.LaunchedProcess != null)
        {
            try
            {
                if (!conn.LaunchedProcess.HasExited)
                    conn.LaunchedProcess.Kill();
            }
            catch { }
        }

        SetConnectionState(key, ConnectionState.Disconnected);
        Debug.WriteLine($"[DataPolling] disconnected: {key}");
    }

    /// <summary>立即刷新指定数据项（手动触发：允许再试一次自动启动浏览器）</summary>
    public async Task RefreshItemAsync(DataDashboardItem item)
    {
        await PollItemAsync(item, true);
    }

    /// <summary>立即刷新所有数据项（手动触发：允许再试一次自动启动浏览器）</summary>
    public async Task RefreshAllAsync()
    {
        var items = GetAllItems();
        foreach (var item in items)
            await PollItemAsync(item, true);
    }

    /// <summary>重建所有定时器（每个去重后的数据源一个轮询实例）</summary>
    private void RebuildTimers()
    {
        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.Invoke(() =>
        {
            // 清除旧定时器
            foreach (var timer in _timers.Values)
                timer.Stop();
            _timers.Clear();

            var items = GetAllItems();
            foreach (var item in items)
            {
                var interval = Math.Max(item.RefreshIntervalSeconds, 10);
                var capturedItem = item;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(interval) };
                timer.Tick += async (s, e) =>
                {
                    // 上一次抓取还没跑完（如等待页面加载）则跳过本轮，避免重叠执行
                    if (!_pollingItems.TryAdd(capturedItem.Id, 0)) return;
                    try
                    {
                        await PollItemAsync(capturedItem);
                    }
                    catch (Exception ex) { Debug.WriteLine($"[DataPolling] scheduled poll failed {capturedItem.Label}: {ex.Message}"); }
                    finally { _pollingItems.TryRemove(capturedItem.Id, out _); }
                };
                timer.Start();
                _timers[item.Id] = timer;
            }

            // 数据卡片开启（存在数据项）时启动远程调试确认弹窗扫描；没有数据项则停掉
            if (_remoteDebugPromptTimer == null)
            {
                _remoteDebugPromptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _remoteDebugPromptTimer.Tick += (s, e) => _ = Task.Run(ScanRemoteDebugPrompt);
            }
            if (items.Count > 0) _remoteDebugPromptTimer.Start();
            else _remoteDebugPromptTimer.Stop();
        });
    }

    /// <summary>扫描浏览器的“是否允许远程调试?”确认弹窗：找到后向其内类名为 Intermediate D3D Window 的
    /// 渲染面（父窗口即该弹窗）发送模拟点击 (350, 180) 自动确认“允许”，并在主窗口气泡通知。
    /// 点击落空时弹窗仍在，下个周期自动重试。</summary>
    private void ScanRemoteDebugPrompt()
    {
        try
        {
            IntPtr dialogHwnd = IntPtr.Zero;
            IntPtr surfaceHwnd = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                var title = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                var t = title.ToString();
                if (t.IndexOf("允许远程调试", StringComparison.OrdinalIgnoreCase) < 0
                    && t.IndexOf("allow remote debugging", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
                // 确认弹窗的子窗口中存在 Intermediate D3D Window（渲染面，其 parent 即本弹窗）
                NativeMethods.EnumChildWindows(hwnd, (child, __) =>
                {
                    var cls = new StringBuilder(64);
                    NativeMethods.GetClassName(child, cls, cls.Capacity);
                    if (cls.ToString() == "Intermediate D3D Window")
                    {
                        dialogHwnd = hwnd;
                        surfaceHwnd = child;
                        return false; // 找到即停
                    }
                    return true;
                }, IntPtr.Zero);
                return surfaceHwnd == IntPtr.Zero;
            }, IntPtr.Zero);

            if (surfaceHwnd == IntPtr.Zero) return;

            // 模拟点击 (350, 180)（“允许”按钮位置）：WM_LBUTTONDOWN + WM_LBUTTONUP 完成确认
            var lParam = (IntPtr)((180 << 16) | (350 & 0xFFFF));
            NativeMethods.SendMessage(surfaceHwnd, NativeMethods.WM_LBUTTONDOWN, (IntPtr)NativeMethods.MK_LBUTTON, lParam);
            NativeMethods.SendMessage(surfaceHwnd, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);
            Debug.WriteLine($"[DataPolling] ✓ remote debugging auto-allowed (dialog hwnd=0x{dialogHwnd.ToInt64():X}, clicked 350,180)");

            // 主窗口气泡通知（15 秒内只提一次，避免弹窗未消失时反复提醒）
            var now = DateTime.Now;
            if ((now - _lastRemoteDebugNotified).TotalSeconds >= 15)
            {
                _lastRemoteDebugNotified = now;
                var text = Framework.Lang.T("window.remoteDebuggingAllowed");
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var w in Application.Current.Windows)
                    {
                        if (w is MainWindow mw && mw.IsLoaded)
                        {
                            try { mw.ShowBubbleNotification(text, 4000); } catch { }
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] remote debugging dialog scan error: {ex.Message}");
        }
    }

    /// <summary>
    /// 轮询单个数据项：获取连接 → 查找 Tab → 执行 JS → 更新值。
    /// forceLaunch=true（手动刷新）时允许再试一次自动启动浏览器；定时轮询默认 false，失败后不再重复启动。
    /// </summary>
    private async Task PollItemAsync(DataDashboardItem item, bool forceLaunch = false)
    {
        // 手动值覆盖：外部已设置值时跳过轮询（含 API 模式），保持手动值直到解除
        if (!string.IsNullOrEmpty(item.VarKey) && GetManualValue(item.VarKey!) != null)
        {
            Debug.WriteLine($"[DataPolling] ↷ \"{item.Label}\" has manual value, skipping poll");
            return;
        }

        // API 请求模式：HTTP 直连取数，无需浏览器
        if (!string.IsNullOrEmpty(item.ApiMethod))
        {
            await PollApiAsync(item);
            return;
        }

        var appSettings = ((App)Application.Current).AppSettings;
        var key = GetConnectionKey(item);

        Debug.WriteLine($"[DataPolling] === polling \"{item.Label}\" === key={key}, url={item.TargetUrl}");

        try
        {
            // 1. 获取或建立 CDP 连接
            var conn = await GetOrCreateConnectionAsync(item, appSettings, forceLaunch);
            if (conn?.Client == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ cannot establish connection: {key}");
                UpdateItemValue(item, "--");
                return;
            }

            // 2. 查找匹配 URL 的 Tab（优先复用本服务记录过的 app 窗口，
            //    覆盖页面重定向改写了 URL 导致 URL 匹配失败的场景）
            var targetId = await FindRecordedAppTargetAsync(conn.Client, item.TargetUrl);
            if (targetId == null)
                targetId = await FindTabByUrlAsync(conn.Client, item.TargetUrl);

            // 3. 找不到 Tab → 尝试自动打开（每会话最多自动尝试 MaxAutoOpenAttempts 次，预算用尽后不再拉起）
            if (targetId == null)
            {
                var launchKey = $"{key}|{item.TargetUrl}";

                if (appSettings.EnableAutoLaunchHiddenBrowser && conn.IsSelfLaunched)
                {
                    // 自启动的浏览器：导航已有 Tab 到目标 URL（不新开浏览器）
                    targetId = await NavigateToUrlAsync(conn.Client, item.TargetUrl);
                }
                else if (appSettings.EnableAutoLaunchHiddenBrowser && !conn.IsSelfLaunched)
                {
                    // 用户浏览器：目标页未打开时需自动打开才能拉到数据；
                    // 按尝试次数预算限制（首次等待超时但页面随后加载完成的场景仍有重试机会，SyncFromConfigs 不重置），
                    // 预算用尽后安静显示 --，不再反复弹窗；手动「立即刷新」/点击卡片重置预算、不受限。
                    var attempts = _autoLaunchDone.TryGetValue(launchKey, out var tried) ? tried : (byte)0;
                    var allowOpen = forceLaunch || attempts < MaxAutoOpenAttempts;
                    if (!allowOpen)
                    {
                        Debug.WriteLine($"[DataPolling] ↷ target page {item.TargetUrl} auto-opened {MaxAutoOpenAttempts} times with no data, no more auto-opens (right-click 'Refresh Data Now' to retry)");
                        UpdateItemValue(item, "--");
                        return;
                    }
                    // 记录一次尝试（手动刷新时重置计数重新起算）
                    _autoLaunchDone[launchKey] = forceLaunch ? (byte)1 : (byte)(attempts + 1);

                    // 同一数据项并发轮询时只允许一个启动流程在执行
                    if (_launchingItems.TryAdd(launchKey, 0))
                    {
                        try
                        {
                            var (exePath, userDataDir, _) = ResolveBrowserConfig(item, appSettings);
                            if (!string.IsNullOrEmpty(exePath))
                                targetId = await LaunchAppModeAsync(conn.Client, exePath, userDataDir, item.TargetUrl);
                        }
                        finally
                        {
                            _launchingItems.TryRemove(launchKey, out _);
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[DataPolling] ↷ target page {item.TargetUrl} already has a launch in progress, skipping");
                        UpdateItemValue(item, "--");
                        return;
                    }
                }
            }

            if (targetId == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ no matching tab found: {item.TargetUrl}");
                UpdateItemValue(item, "--");
                return;
            }

            // 4. Attach + Evaluate（后台 app 窗口可能被浏览器重新显示，取数前先再隐藏一次）
            ReassertHiddenAppWindows(item.TargetUrl);
            var value = await EvaluateOnTargetAsync(conn.Client, targetId, item.SelectorType, item.Selector);
            UpdateItemValue(item, value);
            Debug.WriteLine($"[DataPolling] ✓ \"{item.Label}\" = \"{value}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ poll error \"{item.Label}\": {ex.GetType().Name}: {ex.Message}");
            // 连接可能已断开，清除以便下次重连
            InvalidateConnection(key);
            UpdateItemValue(item, "--");
        }
    }

    /// <summary>API 请求模式轮询：直接 HTTP GET/POST 取数，不经浏览器</summary>
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private async Task PollApiAsync(DataDashboardItem item)
    {
        try
        {
            var isPost = string.Equals(item.ApiMethod, "POST", StringComparison.OrdinalIgnoreCase);
            using var req = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, item.TargetUrl);
            if (isPost)
                req.Content = new StringContent(item.ApiBody ?? "", Encoding.UTF8, "application/json");

            string body;
            using (var resp = await _httpClient.SendAsync(req))
            {
                if (!resp.IsSuccessStatusCode)
                    Debug.WriteLine($"[DataPolling] ✗ API request failed \"{item.Label}\": HTTP {(int)resp.StatusCode}");
                body = await resp.Content.ReadAsStringAsync();
            }

            var value = ExtractApiValue(body, item.ResponseExtract);
            UpdateItemValue(item, value);
            Debug.WriteLine($"[DataPolling] ✓ API \"{item.Label}\" = \"{value}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ API request error \"{item.Label}\": {ex.GetType().Name}: {ex.Message}");
            UpdateItemValue(item, "--");
        }
    }

    /// <summary>从 API 响应中提取值：按 JSON 路径（如 data.list[0].count）取值；留空返回整个响应；非 JSON 时回退返回原文</summary>
    private static string ExtractApiValue(string body, string? extract)
    {
        if (string.IsNullOrWhiteSpace(extract))
            return string.IsNullOrWhiteSpace(body) ? "--" : body.Trim();
        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(body).SelectToken(extract);
            if (token == null) return "--";
            var text = token.Type == Newtonsoft.Json.Linq.JTokenType.Object || token.Type == Newtonsoft.Json.Linq.JTokenType.Array
                ? token.ToString(Newtonsoft.Json.Formatting.None)
                : token.ToString();
            return string.IsNullOrWhiteSpace(text) ? "--" : text;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] response extraction failed ({extract}): {ex.Message}");
            return string.IsNullOrWhiteSpace(body) ? "--" : body.Trim();
        }
    }

    /// <summary>更新数据项的值并触发事件（同一数据源的重复实例一并同步，保证多个窗口面板都收到更新）</summary>
    private void UpdateItemValue(DataDashboardItem item, string value)
    {
        item.Value = value;

        List<DataDashboardItem>? group = null;
        lock (_itemsLock)
        {
            _itemGroups.TryGetValue(GetItemIdentity(item), out group);
        }

        if (group != null)
        {
            foreach (var dup in group)
            {
                if (!ReferenceEquals(dup, item)) dup.Value = value;
                DataUpdated?.Invoke(dup.Id, value);
            }
        }
        else
        {
            DataUpdated?.Invoke(item.Id, value);
        }
    }

    /// <summary>
    /// 获取或建立浏览器 CDP 连接（连接池复用）。
    /// forceLaunch=true 时允许再试一次自动启动隐藏浏览器。
    /// </summary>
    private async Task<BrowserConnection?> GetOrCreateConnectionAsync(DataDashboardItem item, Settings appSettings, bool forceLaunch = false)
    {
        var key = GetConnectionKey(item);

        // 快速检查：已有连接且存活
        lock (_connections)
        {
            if (_connections.TryGetValue(key, out var existing) && existing.Client != null)
                return existing;
        }

        await _connectLock.WaitAsync();
        try
        {
            // 双重检查
            lock (_connections)
            {
                if (_connections.TryGetValue(key, out var existing) && existing.Client != null)
                    return existing;
            }

            SetConnectionState(key, ConnectionState.Connecting);

            // 解析浏览器配置
            var (exePath, userDataDir, port) = ResolveBrowserConfig(item, appSettings);

            // 尝试连接已运行的浏览器（经全局守卫：同端口已有连接尝试进行中则复用，避免重复弹窗）
            var client = await CdpConnectGuard.ConnectAsync(port, () => ConnectBrowserAsync(port));
            if (client != null)
            {
                var conn = new BrowserConnection { Key = key, Client = client, Port = port, State = ConnectionState.Connected };
                lock (_connections) { _connections[key] = conn; }
                SetConnectionState(key, ConnectionState.Connected);
                Debug.WriteLine($"[DataPolling] ✓ browser connected: {key}, port={port}");
                return conn;
            }

            // 连接失败 → 自动启动隐藏浏览器（每会话只自动尝试一次，失败后不再反复拉起浏览器实例）
            if (!appSettings.EnableAutoLaunchHiddenBrowser || string.IsNullOrEmpty(exePath))
            {
                SetConnectionState(key, ConnectionState.Error);
                return null;
            }

            var hiddenLaunchKey = $"hiddenlaunch|{key}";
            var allowLaunch = forceLaunch || _autoLaunchDone.TryAdd(hiddenLaunchKey, 0);
            if (forceLaunch) _autoLaunchDone[hiddenLaunchKey] = 0;
            if (!allowLaunch)
            {
                SetConnectionState(key, ConnectionState.Error);
                Debug.WriteLine($"[DataPolling] ↷ hidden browser {key} already auto-launched once, no relaunch after failure");
                return null;
            }

            var launched = await LaunchHiddenBrowserAsync(key, exePath, userDataDir, port, item.TargetUrl, appSettings);
            if (launched != null)
            {
                SetConnectionState(key, ConnectionState.Connected);
                return launched;
            }

            SetConnectionState(key, ConnectionState.Error);
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 解析数据项对应的浏览器配置（exe路径、userData目录、CDP端口）
    /// </summary>
    private (string exePath, string userDataDir, int port) ResolveBrowserConfig(DataDashboardItem item, Settings appSettings)
    {
        // 默认浏览器（未指定 BrowserId）
        if (string.IsNullOrEmpty(item.BrowserId))
        {
            var defaultExe = FindDefaultBrowserExe();
            return (defaultExe, "", appSettings.CdpDebugPort);
        }

        // 指定了浏览器：从 ProgramRegistry 查找
        var registry = ((App)Application.Current).ProgramRegistry;
        var browser = registry.Browsers.FirstOrDefault(b => b.Id == item.BrowserId);
        if (browser == null)
            return (FindDefaultBrowserExe(), "", appSettings.CdpDebugPort);

        var exePath = browser.ExePath;
        var userDataDir = "";

        // 解析 Profile（userData 目录）
        if (!string.IsNullOrEmpty(item.ProfileId))
        {
            var profile = browser.Profiles.FirstOrDefault(p => p.Id == item.ProfileId);
            if (profile != null)
                userDataDir = profile.Directory;
        }

        // 为该浏览器分配独立端口（从 9300 起）
        var port = AllocatePort(item.BrowserId, item.ProfileId);
        return (exePath, userDataDir, port);
    }

    // 端口分配记录: key = browserId_profileId
    private readonly Dictionary<string, int> _allocatedPorts = new();

    /// <summary>为指定浏览器+Profile 分配固定端口</summary>
    private int AllocatePort(string browserId, string? profileId)
    {
        var key = $"{browserId}_{profileId ?? ""}";
        lock (_allocatedPorts)
        {
            if (_allocatedPorts.TryGetValue(key, out int existing))
                return existing;
            var port = _nextAutoPort++;
            if (_nextAutoPort > 9399) _nextAutoPort = 9300;
            _allocatedPorts[key] = port;
            return port;
        }
    }

    /// <summary>查找系统默认浏览器 exe 路径（从注册表 UserChoice 读取）</summary>
    private static string FindDefaultBrowserExe()
    {
        try
        {
            // 读取 Windows 默认浏览器 ProgId（如 MSEdgeHTM、ChromeHTML）
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = userChoice?.GetValue("ProgId")?.ToString();
            if (!string.IsNullOrEmpty(progId))
            {
                // 从 HKCR\<ProgId>\shell\open\command 解析 exe 路径
                using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                var cmd = cmdKey?.GetValue(null)?.ToString();
                if (!string.IsNullOrEmpty(cmd))
                {
                    // 命令格式: "C:\...\msedge.exe" -- "%1" 或 C:\...\msedge.exe "%1"
                    var exePath = cmd.StartsWith("\"")
                        ? cmd.Substring(1, cmd.IndexOf('"', 1) - 1)
                        : cmd.Split(' ')[0];
                    if (File.Exists(exePath))
                        return exePath;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to read default browser: {ex.Message}");
        }

        // 兜底：Edge 优先（Windows 自带）
        var candidates = new[]
        {
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary>
    /// 启动隐藏浏览器（headless 或 hidden 窗口模式）。
    /// 注：headless Chrome 可能只监听 IPv6 回环，连接侧已由 DiscoverWsUrisAsync 双栈探测兼容。
    /// </summary>
    private async Task<BrowserConnection?> LaunchHiddenBrowserAsync(string key, string exePath, string userDataDir, int port, string url, Settings appSettings)
    {
        // 如果未指定 userData 目录，使用专属目录避免与用户浏览器冲突
        if (string.IsNullOrEmpty(userDataDir))
        {
            userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDesk", "HeadlessProfile", key.Replace("_", "-"));
            Directory.CreateDirectory(userDataDir);
        }

        var mode = appSettings.HiddenBrowserMode;
        string args;
        if (mode == "headless")
        {
            args = $"--headless=new --remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check --disable-extensions \"{url}\"";
        }
        else
        {
            args = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check \"{url}\"";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = mode == "headless",
                WindowStyle = mode == "headless" ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Minimized
            };

            Debug.WriteLine($"[DataPolling] launching hidden browser: {exePath} {args}");
            var process = Process.Start(psi);
            if (process == null) return null;
            _launchedProcesses.Add(process);

            // 等待 CDP 就绪并直接建立正式连接（全程只建立 1 条连接，
            // 避免“就绪探测 + 正式连接”重复建连触发多次浏览器弹窗）
            var client = await ConnectWhenReadyAsync(port, TimeSpan.FromSeconds(15));
            if (client == null)
            {
                Debug.WriteLine($"[DataPolling] ✗ CDP ready timeout, port={port}");
                try { process.Kill(); } catch { }
                return null;
            }

            var conn = new BrowserConnection
            {
                Key = key,
                Client = client,
                Port = port,
                LaunchedProcess = process,
                State = ConnectionState.Connected
            };
            lock (_connections) { _connections[key] = conn; }
            Debug.WriteLine($"[DataPolling] ✓ hidden browser launched: {key}, port={port}, mode={mode}");
            return conn;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ failed to launch hidden browser: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 等待 CDP 端口就绪，并直接返回成功连接的客户端。
    /// 探测成功的那条连接直接作为正式连接复用，全程只建立 1 条连接，
    /// 避免重复建连触发多次浏览器“允许远程调试”弹窗。
    /// </summary>
    private static async Task<DefaultProtocolClient?> ConnectWhenReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // headless Chrome 150 可能只监听 IPv6，且需带 GUID 的完整路径，逐个候选尝试
            foreach (var wsUri in await CdpBrowserService.DiscoverWsUrisAsync(port))
            {
                var client = new DefaultProtocolClient(wsUri, NullLogger.Instance);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.ConnectAsync(cts.Token);
                    var n = Interlocked.Increment(ref _totalConnections);
                    Debug.WriteLine($"[DataPolling] ★ CDP connection #{n} established (hidden browser ready): {wsUri}");
                    return client; // 连接成功，直接复用，不 Dispose
                }
                catch
                {
                    client.Dispose();
                }
            }
            await Task.Delay(500);
        }
        return null;
    }

    /// <summary>通过 WebSocket CDP 连接到浏览器（兼容 IPv4/IPv6 双栈与带 GUID 路径）</summary>
    private static async Task<DefaultProtocolClient?> ConnectBrowserAsync(int port)
    {
        foreach (var wsUri in await CdpBrowserService.DiscoverWsUrisAsync(port))
        {
            var client = new DefaultProtocolClient(wsUri, NullLogger.Instance);
            try
            {
                // 超时适当放宽：Edge “允许远程调试”弹窗需用户手动确认，太短会在确认前超时重试、叠加弹窗
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.ConnectAsync(cts.Token);
                var n = Interlocked.Increment(ref _totalConnections);
                Debug.WriteLine($"[DataPolling] ★ CDP connection #{n} established (attaching to running browser): {wsUri}");
                return client;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DataPolling] connection to {wsUri} failed: {ex.Message}");
                client.Dispose();
            }
        }
        return null;
    }

    /// <summary>查找本服务记录过的 app 模式页面（按 URL 匹配并校验 target 仍存活，失效记录顺手清理）</summary>
    private static async Task<Target.TargetIDType?> FindRecordedAppTargetAsync(DefaultProtocolClient client, string url)
    {
        List<(DefaultProtocolClient client, Target.TargetIDType targetId, string url)> snapshot;
        lock (_openedAppTargetsLock)
        {
            snapshot = _openedAppTargets.Where(t => t.client == client && UrlMatches(t.url, url)).ToList();
        }
        if (snapshot.Count == 0) return null;
        try
        {
            var result = await client.SendCommandAsync(Target.GetTargets());
            foreach (var rec in snapshot)
            {
                if (result.TargetInfos.Any(t => t.TargetId == rec.targetId)) return rec.targetId;
                // target 已不存在（窗口被关闭）→ 移除失效记录
                lock (_openedAppTargetsLock)
                {
                    _openedAppTargets.RemoveAll(t => t.client == rec.client && t.targetId == rec.targetId && t.url == rec.url);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>查找匹配 URL 的 page Tab（含重定向归属判定：登录跳转页的回调参数指向目标 URL 也算；单 Tab 兜底）</summary>
    private static async Task<Target.TargetIDType?> FindTabByUrlAsync(DefaultProtocolClient client, string url)
    {
        var result = await client.SendCommandAsync(Target.GetTargets());
        var pages = result.TargetInfos.Where(t => t.Type == "page").ToList();

        foreach (var tab in pages)
        {
            if (PageBelongsToUrl(tab.Url, url))
                return tab.TargetId;
        }

        if (pages.Count == 1) return pages[0].TargetId;
        return null;
    }

    /// <summary>查找 URL 严格匹配的 page Tab（不做单 Tab 兜底，供复用显示已打开窗口，避免误切无关窗口）</summary>
    private static async Task<Target.TargetIDType?> FindTabByUrlStrictAsync(DefaultProtocolClient client, string url)
    {
        var result = await client.SendCommandAsync(Target.GetTargets());
        foreach (var tab in result.TargetInfos)
        {
            if (tab.Type != "page") continue;
            if (string.IsNullOrEmpty(tab.Url)) continue;
            if (PageBelongsToUrl(tab.Url, url))
                return tab.TargetId;
        }
        return null;
    }

    /// <summary>判定页面是否属于目标 URL：URL 前缀匹配，或登录重定向页的回调参数
    /// （oauth_callback/redirect_uri/return_to/redirect/next）携带目标 URL（页面 URL 被重定向改写的场景）</summary>
    private static bool PageBelongsToUrl(string? pageUrl, string targetUrl)
    {
        if (string.IsNullOrEmpty(pageUrl)) return false;
        if (UrlMatches(pageUrl, targetUrl)) return true;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return false;
        foreach (var kv in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var name = kv.Substring(0, eq);
            if (!string.Equals(name, "oauth_callback", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "redirect_uri", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "return_to", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "redirect", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "next", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var val = Uri.UnescapeDataString(kv.Substring(eq + 1));
                if (UrlMatches(val, targetUrl)) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>在已连接的浏览器中新建 Tab 并导航到 URL</summary>
    private static async Task<Target.TargetIDType?> CreateTabAsync(DefaultProtocolClient client, string url)
    {
        try
        {
            var result = await client.SendCommandAsync(Target.CreateTarget(url));
            Debug.WriteLine($"[DataPolling] new tab: {url} -> {result.TargetId}");
            await Task.Delay(2000); // 等待页面加载
            return result.TargetId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ failed to create tab: {ex.Message}");
            return null;
        }
    }

    /// <summary>以 app 模式在用户浏览器中启动 URL（干净独立窗口，不在主浏览器新建普通标签页）</summary>
    private async Task<Target.TargetIDType?> LaunchAppModeAsync(DefaultProtocolClient client, string exePath, string userDataDir, string url)
    {
        try
        {
            // 启动前快照已有 page target：新窗口 URL 被重定向改写（登录/语言跳转）时，
            // 也能按“新增 target”识别出我们打开的窗口
            var beforeResult = await client.SendCommandAsync(Target.GetTargets());
            var beforePages = beforeResult.TargetInfos.Where(t => t.Type == "page").ToList();
            var beforeIds = beforePages.Select(t => t.TargetId).ToList();

            // 已存在属于该 URL 的页面（上次打开的窗口还在、或页面停在登录重定向页）→ 直接复用，
            // 不再启动新窗口（避免每次超时都多弹一个窗口）
            var matches = beforePages.Where(t => PageBelongsToUrl(t.Url, url)).ToList();
            if (matches.Count > 0)
            {
                // 历史失败遗留的重复窗口（修复前每次超时多弹一个）：保留一个，其余经 CDP 关掉
                foreach (var extra in matches.Skip(1))
                {
                    try
                    {
                        await client.SendCommandAsync(Target.CloseTarget(extra.TargetId));
                        Debug.WriteLine($"[DataPolling] 🧹 closing duplicate app-mode window: {extra.TargetId} ({extra.Url})");
                    }
                    catch (Exception ex) { Debug.WriteLine($"[DataPolling] failed to close duplicate window: {ex.Message}"); }
                }
                var existing = matches[0];
                lock (_openedAppTargetsLock) { _openedAppTargets.Add((client, existing.TargetId, url)); }
                Debug.WriteLine($"[DataPolling] ✓ reusing existing app-mode page: {url} -> {existing.TargetId} (actual URL: {existing.Url})");
                // 仅当页面位于单标签窗口（我们打开的 app 窗口）才最小化；
                // 多标签窗口说明可能是用户自己在主浏览器开的页，不能把用户整个浏览器最小化
                if (await IsSingleTabWindowAsync(client, existing.TargetId))
                    await MinimizeTargetWindowAsync(client, existing.TargetId);
                else
                    Debug.WriteLine("[DataPolling] reused page is in a multi-tab window (not an app window), skipping hide to avoid affecting the user's browser");
                return existing.TargetId;
            }

            var proc = LaunchAppModeProcess(exePath, userDataDir, url, hideWindow: true);
            if (proc == null) return null;

            // app 窗口会在已运行的浏览器进程中打开，等待其页面出现在 CDP target 列表
            var host = GetUrlHost(url);
            List<string>? lastUrls = null;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                var result = await client.SendCommandAsync(Target.GetTargets());
                var pages = result.TargetInfos.Where(t => t.Type == "page" && !string.IsNullOrEmpty(t.Url)).ToList();
                lastUrls = pages.Select(p => p.Url ?? "").ToList();

                // 1) URL 归属匹配（含页面被重定向到登录页、回调参数指向目标 URL 的情形）
                var found = pages.FirstOrDefault(t => PageBelongsToUrl(t.Url, url));
                // 2) 归属匹配不上 → 按新增 target 识别，限同域名避免误抓用户另开的页面
                if (found == null && host != null)
                    found = pages.FirstOrDefault(t => !beforeIds.Any(id => id == t.TargetId) && GetUrlHost(t.Url ?? "") == host);
                if (found != null)
                {
                    // 记录本次打开的页面，关闭软件时经 CDP 关闭该窗口；点击数据项时也用它直接显示已打开的窗口
                    lock (_openedAppTargetsLock) { _openedAppTargets.Add((client, found.TargetId, url)); }
                    Debug.WriteLine($"[DataPolling] ✓ app-mode page ready: {url} -> {found.TargetId} (actual URL: {found.Url})");
                    // 稍候委托完成（启动进程退出）再判断是否需要 CDP 兜底：
                    // 进程自持 → HideAppWindowWhenReadyAsync 已用 SW_HIDE 处理；已委托 → 监视器已 SW_HIDE，最小化仅作兜底
                    for (int w = 0; w < 8; w++)
                    {
                        bool exitedNow;
                        try { exitedNow = proc.HasExited; } catch { exitedNow = false; }
                        if (exitedNow) break;
                        await Task.Delay(250);
                    }
                    await HideDelegatedAppWindowAsync(client, found.TargetId, proc);
                    return found.TargetId;
                }
            }

            // 诊断：打印当前 CDP 连接可见的全部页面，帮助定位“页面明明打开了却找不到”：
            // - 列表中根本没有目标页 → 窗口开在了另一个浏览器进程（--app 未带 --user-data-dir，
            //   被委托给拥有默认用户数据目录的实例，而 SideDesk 连接的不是它）；
            // - 列表中有目标页但 URL 不同 → 页面重定向改写了地址
            var visible = lastUrls == null || lastUrls.Count == 0 ? "（无页面）" : string.Join(" | ", lastUrls);
            Debug.WriteLine($"[DataPolling] ✗ app-mode page wait timeout: {url}; visible pages on current connection: {visible}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ app-mode page wait error: {ex.Message}");
            return null;
        }
    }

    /// <summary>取 URL 的域名（无法解析时返回 null）</summary>
    private static string? GetUrlHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    /// <summary>启动 app 模式浏览器进程（干净独立窗口，无地址栏/标签栏）。启动成功返回进程，失败返回 null。
    /// hideWindow=true 时（数据卡片后台抓取）在窗口出现后用 ShowWindow(SW_HIDE) 隐藏，不占用屏幕。
    /// hideFromTaskbar=true 时（点击数据项打开）在窗口出现后设为工具窗口样式，不在任务栏显示。</summary>
    private static Process? LaunchAppModeProcess(string exePath, string userDataDir, string url, bool hideWindow = false, bool hideFromTaskbar = false)
    {
        try
        {
            var args = $"--app=\"{url}\"";
            if (!string.IsNullOrEmpty(userDataDir))
                args += $" --user-data-dir=\"{userDataDir}\"";

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // 启动前快照该浏览器的已有窗口（每次启动各自持有，避免并发启动互相污染白名单）；
            // 启动后新出现的同浏览器窗口即为我们打开的 app 窗口（隐藏或去任务栏都靠它识别）
            List<IntPtr>? whitelist = null;
            var browserName = Path.GetFileNameWithoutExtension(exePath);
            if (hideWindow || hideFromTaskbar)
                whitelist = EnumerateBrowserWindows(browserName);

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _launchedProcesses.Add(proc);
                lock (_appModeWindowsLock) { _appModeWindows.Add((proc, url)); }
                if (hideWindow)
                {
                    // 双保险：进程自持 → 拿到窗口句柄直接隐藏；委托给已运行浏览器（进程退出）→ 枚举监视器兜底
                    _ = Task.Run(() => HideAppWindowWhenReadyAsync(proc));
                    _ = Task.Run(() => HideNewBrowserWindowsWatcherAsync(browserName, url, whitelist!));
                }
                else if (hideFromTaskbar)
                {
                    // 点击打开的窗口保持可见，但移除任务栏按钮（进程自持/委托两种情况都由枚举监视器覆盖）
                    _ = Task.Run(() => ExcludeNewAppWindowFromTaskbarAsync(browserName, whitelist!));
                }
            }
            Debug.WriteLine($"[DataPolling] launching in app mode: {exePath} {args}");
            return proc;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ app-mode launch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>轮询等待 app 窗口句柄出现，然后用 ShowWindow(SW_HIDE) 隐藏（数据卡片后台窗口，不占用屏幕）。
    /// 窗口委托给已运行浏览器（启动进程自身退出）时拿不到本进程句柄，则放弃隐藏。</summary>
    private static async Task HideAppWindowWhenReadyAsync(Process proc)
    {
        try
        {
            for (int i = 0; i < 40; i++) // 最长约 8 秒
            {
                bool exited;
                try { exited = proc.HasExited; } catch { exited = true; }
                if (exited) return; // 已委托给运行中的浏览器，窗口不归本进程管
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                    Debug.WriteLine("[DataPolling] 🙈 app-mode window hidden (background window of data module; click the data item to show again)");
                    return;
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to hide app window: {ex.Message}");
        }
    }

    /// <summary>判断窗口是否指定浏览器的顶层窗口：可见 + Chrome_WidgetWin_1 类 + 归属浏览器进程</summary>
    private static bool IsBrowserTopWindow(IntPtr hwnd, string browserName)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        var cls = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "Chrome_WidgetWin_1") return false;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, browserName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>枚举指定浏览器当前可见的顶层窗口句柄（供启动前白名单）</summary>
    private static List<IntPtr> EnumerateBrowserWindows(string browserName)
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try { if (IsBrowserTopWindow(hwnd, browserName)) list.Add(hwnd); } catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>app 模式启动后的新窗口监视器：短时间内反复枚举，把白名单之外新出现的浏览器窗口
    /// 用 ShowWindow(SW_HIDE) 隐藏。不依赖启动进程句柄——窗口委托给已运行浏览器（进程已退出）也能藏住。
    /// 白名单为本次启动的启动前快照，不受其他并发启动影响。</summary>
    private static async Task HideNewBrowserWindowsWatcherAsync(string browserName, string url, List<IntPtr> whitelist)
    {
        try
        {
            for (int i = 0; i < 100; i++) // 最长约 20 秒，覆盖页面加载/委托延迟
            {
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        lock (_appModeHiddenHwnds) { if (_appModeHiddenHwnds.ContainsKey(hwnd)) return true; }
                        if (whitelist.Contains(hwnd)) return true;
                        if (!IsBrowserTopWindow(hwnd, browserName)) return true;
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                        lock (_appModeHiddenHwnds) { _appModeHiddenHwnds[hwnd] = url; }
                        Debug.WriteLine($"[DataPolling] 🙈 newly opened app-mode window hidden (hwnd=0x{hwnd.ToInt64():X}); click the data item to show again");
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] app window watcher error: {ex.Message}");
        }
    }

    /// <summary>移除窗口的任务栏按钮：加 WS_EX_TOOLWINDOW 并去掉 WS_EX_APPWINDOW。
    /// 数据卡片打开的 app 窗口统一由点击数据项管理显隐，不占用任务栏</summary>
    private static void HideWindowFromTaskbar(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    /// <summary>app 模式启动后的新窗口监视器（点击数据项打开场景）：把白名单之外新出现的浏览器窗口
    /// 设为工具窗口样式（不进任务栏）。不依赖启动进程句柄——窗口委托给已运行浏览器也能覆盖。
    /// 命中后继续重套样式约 2 秒，对抗浏览器初始化阶段可能重置扩展样式</summary>
    private static async Task ExcludeNewAppWindowFromTaskbarAsync(string browserName, List<IntPtr> whitelist)
    {
        try
        {
            int styledPasses = 0;
            for (int i = 0; i < 50; i++) // 最长约 10 秒，覆盖页面加载/委托延迟
            {
                bool styledAny = false;
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (whitelist.Contains(hwnd)) return true;
                        if (!IsBrowserTopWindow(hwnd, browserName)) return true;
                        HideWindowFromTaskbar(hwnd);
                        styledAny = true;
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                if (styledAny)
                {
                    if (++styledPasses == 1)
                        Debug.WriteLine("[DataPolling] 🙈 removed clicked app-mode window from taskbar (show/hide still managed by clicking the data item)");
                    if (styledPasses >= 10) break; // 连续重套约 2 秒后停止
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] app window taskbar-removal watcher error: {ex.Message}");
        }
    }

    /// <summary>重新隐藏被 SW_HIDE 藏过、又被浏览器重新显示出来的窗口（页面 JS focus/激活等）。
    /// 用户点击数据项主动显示的窗口已从记录中移除，不受影响</summary>
    private static void ReassertHiddenAppWindows(string url)
    {
        List<IntPtr> targets;
        lock (_appModeHiddenHwnds)
        {
            targets = _appModeHiddenHwnds.Where(kv => UrlMatches(kv.Value, url)).Select(kv => kv.Key).ToList();
        }
        foreach (var hwnd in targets)
        {
            try
            {
                if (!NativeMethods.IsWindow(hwnd))
                {
                    lock (_appModeHiddenHwnds) { _appModeHiddenHwnds.Remove(hwnd); }
                    continue;
                }
                if (NativeMethods.IsWindowVisible(hwnd))
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
            catch { }
        }
    }

    /// <summary>显示被新窗口监视器 SW_HIDE 隐藏的窗口（点击数据项时按网址匹配恢复）；无对应隐藏窗口返回 false</summary>
    private static bool ShowHiddenAppHwnds(string url)
    {
        List<IntPtr> targets;
        lock (_appModeHiddenHwnds)
        {
            targets = _appModeHiddenHwnds.Where(kv => UrlMatches(kv.Value, url)).Select(kv => kv.Key).ToList();
        }
        if (targets.Count == 0) return false;
        bool shown = false;
        foreach (var hwnd in targets)
        {
            try
            {
                if (!NativeMethods.IsWindow(hwnd))
                {
                    lock (_appModeHiddenHwnds) { _appModeHiddenHwnds.Remove(hwnd); }
                    continue;
                }
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
                HideWindowFromTaskbar(hwnd); // 数据卡片窗口恢复显示后仍不进任务栏
                lock (_appModeHiddenHwnds) { _appModeHiddenHwnds.Remove(hwnd); }
                shown = true;
            }
            catch { }
        }
        if (shown) Debug.WriteLine($"[DataPolling] ✓ hidden app-mode window shown: {url}");
        return shown;
    }

    /// <summary>判断页面所在窗口是否为单标签窗口（app 模式窗口的特征）：
    /// 同窗口内还有其他 page target 则返回 false（说明是用户的多标签浏览器窗口）</summary>
    private static async Task<bool> IsSingleTabWindowAsync(DefaultProtocolClient client, Target.TargetIDType targetId)
    {
        try
        {
            var win = await client.SendCommandAsync(Browser.GetWindowForTarget(targetId));
            var result = await client.SendCommandAsync(Target.GetTargets());
            foreach (var p in result.TargetInfos.Where(t => t.Type == "page" && t.TargetId != targetId))
            {
                try
                {
                    var w2 = await client.SendCommandAsync(Browser.GetWindowForTarget(p.TargetId));
                    if (Equals(w2.WindowId, win.WindowId)) return false;
                }
                catch { }
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>经 CDP 把 app 窗口最小化隐藏（委托给已运行浏览器的窗口拿不到进程句柄，只能走窗口控制）。
    /// CDP 无真正的 hide，最小化是最佳近似；点击数据项时经 RestoreCdpHiddenTargetAsync 恢复</summary>
    private static async Task MinimizeTargetWindowAsync(DefaultProtocolClient client, Target.TargetIDType targetId)
    {
        try
        {
            var win = await client.SendCommandAsync(Browser.GetWindowForTarget(targetId));
            await client.SendCommandAsync(Browser.SetWindowBounds(win.WindowId,
                new Browser.BoundsType(null, null, null, null, new Browser.WindowStateType("minimized"))));
            _cdpHiddenTargets[targetId] = 0;
            Debug.WriteLine("[DataPolling] 🙈 window delegated to running browser, hidden via CDP (minimized; click the data item to show)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to hide window via CDP: {ex.Message}");
        }
    }

    /// <summary>启动进程已退出（窗口委托给已运行浏览器）时经 CDP 最小化隐藏；
    /// 进程还活着则 HideAppWindowWhenReadyAsync 已用 ShowWindow(SW_HIDE) 处理，跳过</summary>
    private static async Task HideDelegatedAppWindowAsync(DefaultProtocolClient client, Target.TargetIDType targetId, Process proc)
    {
        bool exited;
        try { exited = proc.HasExited; } catch { exited = false; }
        if (!exited) return;
        await MinimizeTargetWindowAsync(client, targetId);
    }

    /// <summary>CDP 跟踪的 app 窗口（委托给已运行浏览器、无进程句柄）去任务栏：
    /// 按页面标题精确匹配同标题的 Chrome_WidgetWin_1 窗口再套工具窗口样式。
    /// app 模式窗口标题与页面标题一致；用户主浏览器窗口带“- 浏览器名”后缀，不会误伤</summary>
    private static async Task HideTargetWindowFromTaskbarAsync(DefaultProtocolClient client, Target.TargetIDType targetId)
    {
        try
        {
            var result = await client.SendCommandAsync(Target.GetTargets());
            var title = result.TargetInfos.FirstOrDefault(t => t.TargetId == targetId)?.Title;
            if (string.IsNullOrEmpty(title)) return;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    var cls = new StringBuilder(64);
                    NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
                    if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                    var sb = new StringBuilder(512);
                    NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                    if (sb.ToString() == title) HideWindowFromTaskbar(hwnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to remove CDP window from taskbar: {ex.Message}");
        }
    }

    /// <summary>恢复经 CDP 最小化隐藏的窗口（点击数据项显示时调用；未隐藏过则直接跳过）</summary>
    private static async Task RestoreCdpHiddenTargetAsync(DefaultProtocolClient client, Target.TargetIDType targetId)
    {
        if (!_cdpHiddenTargets.TryRemove(targetId, out _)) return;
        try
        {
            var win = await client.SendCommandAsync(Browser.GetWindowForTarget(targetId));
            await client.SendCommandAsync(Browser.SetWindowBounds(win.WindowId,
                new Browser.BoundsType(null, null, null, null, new Browser.WindowStateType("normal"))));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to restore window: {ex.Message}");
        }
    }

    /// <summary>以 app 模式（干净独立窗口）打开数据项的目标网址，供外部调用（如点击数据看板项）。
    /// 若该网址的 app 窗口已经打开过，则直接把它切到前台，不再重复启动新窗口。</summary>
    public void OpenItemUrlInAppMode(DataDashboardItem item)
    {
        var url = item?.TargetUrl;
        if (string.IsNullOrEmpty(url)) return;
        _ = OpenOrShowAppModeAsync(item!);
    }

    /// <summary>先尝试显示已打开的 app 窗口；没有才启动新窗口，启动成功后记录 target 供下次复用</summary>
    private async Task OpenOrShowAppModeAsync(DataDashboardItem item)
    {
        if (await TryShowOpenedAppWindowAsync(item)) return;

        var appSettings = ((App)Application.Current).AppSettings;
        var (exePath, userDataDir, _) = ResolveBrowserConfig(item, appSettings);

        // 未指定 Profile 时，自动检测浏览器默认 User Data 目录以复用登录态
        if (string.IsNullOrEmpty(userDataDir) && !string.IsNullOrEmpty(exePath))
            userDataDir = DetectDefaultUserDataDir(exePath);

        // 优先用数据项配置的浏览器 + Profile 以 app 模式打开（复用登录态）；窗口不进任务栏
        if (!string.IsNullOrEmpty(exePath) && LaunchAppModeProcess(exePath, userDataDir, item.TargetUrl, hideFromTaskbar: true) != null)
        {
            await RecordClickedAppTargetAsync(item);
            return;
        }

        // 回退：找不到浏览器或启动失败 → 系统默认方式打开
        try { Process.Start(new ProcessStartInfo(item.TargetUrl) { UseShellExecute = true }); } catch { }
    }

    /// <summary>查找已打开的 app 模式窗口并显示：优先用启动进程句柄直接激活（我们打开的窗口），
    /// 其次查 CDP 记录/同浏览器内匹配页面（窗口被委托给已运行浏览器时的兜底）</summary>
    private async Task<bool> TryShowOpenedAppWindowAsync(DataDashboardItem item)
    {
        var url = item.TargetUrl;

        // 1. 我们启动的 app 窗口进程还活着 → 直接激活显示（最小化则恢复）
        List<(Process proc, string url)> procSnapshot;
        lock (_appModeWindowsLock) { procSnapshot = new List<(Process, string)>(_appModeWindows); }
        foreach (var (proc, procUrl) in procSnapshot)
        {
            if (!UrlMatches(procUrl, url)) continue;
            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }
            if (exited)
            {
                lock (_appModeWindowsLock) { _appModeWindows.Remove((proc, procUrl)); }
                continue;
            }
            try
            {
                proc.WaitForInputIdle(500);
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    // 恢复最小化或被隐藏的窗口（数据卡片后台窗口默认以 SW_HIDE 隐藏）；
                    // 从隐藏记录中移除，避免后续轮询的 ReassertHiddenAppWindows 把用户正在看的窗口又藏回去
                    NativeMethods.ShowWindow(hwnd, NativeMethods.IsIconic(hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOW);
                    NativeMethods.SetForegroundWindow(hwnd);
                    HideWindowFromTaskbar(hwnd); // 数据卡片窗口重套去任务栏样式（防浏览器重置）
                    lock (_appModeHiddenHwnds) { _appModeHiddenHwnds.Remove(hwnd); }
                    Debug.WriteLine($"[DataPolling] ✓ previously opened app window activated and shown: {url}");
                    return true;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[DataPolling] failed to activate app window: {ex.Message}"); }
        }

        // 2. 委托给已运行浏览器的窗口被新窗口监视器 SW_HIDE 隐藏过 → 按句柄直接恢复显示
        if (ShowHiddenAppHwnds(url)) return true;

        // 3. 本服务经 CDP 记录过的 app 页面（如窗口被委托给已运行浏览器，启动进程已退出）
        List<(DefaultProtocolClient client, Target.TargetIDType targetId, string url)> snapshot;
        lock (_openedAppTargetsLock) { snapshot = new List<(DefaultProtocolClient, Target.TargetIDType, string)>(_openedAppTargets); }
        foreach (var t in snapshot)
        {
            if (!UrlMatches(t.url, url)) continue;
            await RestoreCdpHiddenTargetAsync(t.client, t.targetId);
            if (await BringTargetToFrontAsync(t.client, t.targetId))
            {
                await HideTargetWindowFromTaskbarAsync(t.client, t.targetId);
                return true;
            }
            // target 已不存在（窗口被手动关闭）→ 移除失效记录
            lock (_openedAppTargetsLock) { _openedAppTargets.Remove(t); }
        }

        // 4. 同浏览器内匹配 URL 的其他页面（覆盖此前打开但未记录的窗口）
        var conn = GetConnection(GetConnectionKey(item));
        if (conn?.Client != null)
        {
            try
            {
                var targetId = await FindTabByUrlStrictAsync(conn.Client, url);
                if (targetId != null)
                {
                    await RestoreCdpHiddenTargetAsync(conn.Client, targetId);
                    if (await BringTargetToFrontAsync(conn.Client, targetId))
                    {
                        await HideTargetWindowFromTaskbarAsync(conn.Client, targetId);
                        lock (_openedAppTargetsLock) { _openedAppTargets.Add((conn.Client, targetId, url)); }
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    private static bool UrlMatches(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Attach 到 target 并把页面切到前台（app 模式窗口即被激活显示）；target 已失效时返回 false</summary>
    private static async Task<bool> BringTargetToFrontAsync(DefaultProtocolClient client, Target.TargetIDType targetId)
    {
        try
        {
            var result = await client.SendCommandAsync(Target.GetTargets());
            if (!result.TargetInfos.Any(t => t.TargetId == targetId)) return false;
            var attachResult = await client.SendCommandAsync(Target.AttachToTarget(targetId, Flatten: true));
            await client.SendCommandAsync(Page.BringToFront(), sessionId: attachResult.SessionId.Value);
            Debug.WriteLine($"[DataPolling] ✓ previously opened app window brought to front: {targetId}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to show opened window: {ex.Message}");
            return false;
        }
    }

    /// <summary>点击启动 app 窗口后，若该浏览器已有 CDP 连接则等待页面出现并记录，供下次点击直接复用</summary>
    private async Task RecordClickedAppTargetAsync(DataDashboardItem item)
    {
        try
        {
            var key = GetConnectionKey(item);

            // 连接可能稍晚于窗口出现才可用（如浏览器刚以调试端口启动），最多等约 8 秒
            BrowserConnection? conn = null;
            for (int i = 0; i < 10 && conn?.Client == null; i++)
            {
                conn = GetConnection(key);
                if (conn?.Client == null) await Task.Delay(800);
            }
            if (conn?.Client == null) return; // 浏览器未开 CDP 时无法跟踪，保持原行为

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(800);
                var targetId = await FindTabByUrlStrictAsync(conn.Client, item.TargetUrl);
                if (targetId != null)
                {
                    lock (_openedAppTargetsLock) { _openedAppTargets.Add((conn.Client, targetId, item.TargetUrl)); }
                    Debug.WriteLine($"[DataPolling] ✓ recorded clicked app window: {item.TargetUrl}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] failed to record app window: {ex.Message}");
        }
    }

    /// <summary>探测浏览器默认的 User Data 目录</summary>
    private static string DetectDefaultUserDataDir(string browserExePath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var browserName = Path.GetFileNameWithoutExtension(browserExePath).ToLower();

        if (browserName.Contains("chrome"))
            return Path.Combine(localAppData, "Google", "Chrome", "User Data");
        if (browserName.Contains("edge") || browserName.Contains("msedge"))
            return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        if (browserName.Contains("brave"))
            return Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data");

        return "";
    }

    /// <summary>导航已有 Tab 到 URL（用于自启动浏览器）</summary>
    private static async Task<Target.TargetIDType?> NavigateToUrlAsync(DefaultProtocolClient client, string url)
    {
        try
        {
            // 获取第一个 page target
            var result = await client.SendCommandAsync(Target.GetTargets());
            var page = result.TargetInfos.FirstOrDefault(t => t.Type == "page");
            if (page == null) return await CreateTabAsync(client, url);

            var attachResult = await client.SendCommandAsync(Target.AttachToTarget(page.TargetId, Flatten: true));
            var sessionId = attachResult.SessionId.Value;
            await client.SendCommandAsync(Page.Navigate(url), sessionId: sessionId);
            await Task.Delay(3000); // 等待页面加载
            return page.TargetId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataPolling] ✗ navigation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Attach 到 Target 并执行 JS 获取元素文本</summary>
    private static async Task<string> EvaluateOnTargetAsync(DefaultProtocolClient client, Target.TargetIDType targetId, string selectorType, string selector)
    {
        var attachResult = await client.SendCommandAsync(Target.AttachToTarget(targetId, Flatten: true));
        var sessionId = attachResult.SessionId.Value;

        var jsExpression = BuildExtractJs(selectorType, selector);
        var evalResult = await client.SendCommandAsync(
            Runtime.Evaluate(jsExpression, ReturnByValue: true, AwaitPromise: true),
            sessionId: sessionId);

        if (evalResult.ExceptionDetails != null)
            Debug.WriteLine($"[DataPolling] ✗ JS exception: {evalResult.ExceptionDetails.Text}");

        var value = evalResult.Result?.Value?.ToString();
        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }

    /// <summary>构建取值 JS 表达式（xpath/css/regex/js 四种；空类型回退旧版自动识别）</summary>
    private static string BuildExtractJs(string selectorType, string selector)
    {
        var escaped = selector.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        switch ((selectorType ?? "").ToLowerInvariant())
        {
            case "xpath":
                return $"(function(){{ var r = document.evaluate(\"{escaped}\", document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null); return r.singleNodeValue ? r.singleNodeValue.textContent.trim() : '--'; }})()";
            case "css":
                return $"(function(){{ var el = document.querySelector(\"{escaped}\"); return el ? el.textContent.trim() : '--'; }})()";
            case "regex":
                // 对页面可见文本做正则匹配：优先返回第一个捕获组，否则整体匹配
                return $"(function(){{ try {{ var m = document.body.innerText.match(new RegExp(\"{escaped}\")); return m ? (m[1] !== undefined ? m[1] : m[0]).trim() : '--'; }} catch(e) {{ return '--'; }} }})()";
            case "js":
                // 用户脚本经 CDP Runtime.evaluate 执行取数（可返回值或 Promise）
                return $"(function(){{ try {{ var v = new Function(\"{escaped}\")(); return (v === undefined || v === null) ? '--' : String(v); }} catch(e) {{ return '--'; }} }})()";
            default:
                // 旧版模板无类型：开头斜杠自动识别为 XPath，否则 CSS
                return selector.TrimStart().StartsWith("/")
                    ? BuildExtractJs("xpath", selector)
                    : BuildExtractJs("css", selector);
        }
    }

    /// <summary>使指定连接失效</summary>
    private void InvalidateConnection(string key)
    {
        BrowserConnection? conn;
        lock (_connections)
        {
            _connections.TryGetValue(key, out conn);
            _connections.Remove(key);
        }
        if (conn == null) return;

        try { conn.Client?.Dispose(); } catch { }
        // 注意：不清除 LaunchedProcess，浏览器可能还在运行，下次可以重连
        SetConnectionState(key, ConnectionState.Disconnected);
    }

    /// <summary>设置连接状态并触发事件</summary>
    private void SetConnectionState(string key, ConnectionState state)
    {
        lock (_connections)
        {
            if (_connections.TryGetValue(key, out var conn))
                conn.State = state;
        }
        ConnectionStateChanged?.Invoke(key, state);
    }

    /// <summary>释放所有资源：关闭本服务打开的 app 模式页面、杀死自启动的浏览器进程、断开连接</summary>
    public void Dispose()
    {
        foreach (var timer in _timers.Values)
            timer.Stop();
        _timers.Clear();
        _remoteDebugPromptTimer?.Stop();

        // 关闭软件时把数据看板打开的 app 模式窗口一并关掉（只关本服务打开的页面，不动用户其他标签页）
        List<(DefaultProtocolClient client, Target.TargetIDType targetId, string url)> closeSnapshot;
        lock (_openedAppTargetsLock)
        {
            closeSnapshot = new List<(DefaultProtocolClient, Target.TargetIDType, string)>(_openedAppTargets);
            _openedAppTargets.Clear();
        }
        var closeTasks = closeSnapshot.Select(t => Task.Run(async () =>
        {
            try { await t.client.SendCommandAsync(Target.CloseTarget(t.targetId)); }
            catch { }
        })).ToArray();
        if (closeTasks.Length > 0)
        {
            try { Task.WaitAll(closeTasks, TimeSpan.FromSeconds(2)); } catch { }
        }

        // 杀死本服务拉起的浏览器进程（已委托退出的进程 HasExited=true，自然跳过）
        foreach (var p in _launchedProcesses)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        lock (_connections)
        {
            foreach (var conn in _connections.Values)
            {
                try { conn.Client?.Dispose(); } catch { }
                if (conn.LaunchedProcess != null)
                {
                    try { if (!conn.LaunchedProcess.HasExited) conn.LaunchedProcess.Kill(); } catch { }
                }
            }
            _connections.Clear();
        }
    }
}
