using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using SideDesk.Models;
using NJson = Newtonsoft.Json;

namespace SideDesk.Services
{
    /// <summary>云同步状态</summary>
    public enum CloudSyncState
    {
        Idle,
        Syncing,
        Success,
        Failed,
        NotLoggedIn
    }

    /// <summary>
    /// 云同步服务（事件驱动模型）：
    /// - 启动时对账：推送本地未同步结构 → 拉取服务端全量结构补齐本地 → 拉取行级数据；
    /// - 运行中：本地编辑通过行级推送接口即时更新服务端，远端变更通过 WebSocket 通知后拉取；
    /// - 退出/手动同步时做一次全量上传兑底（改名、移动等低频结构变更）。
    /// 上传成功后解析服务器返回的 localId -> serverId 映射并保存到本地（<see cref="Settings.CloudIdMap"/>）。
    /// </summary>
    public sealed class CloudSyncService
    {
        private static readonly Lazy<CloudSyncService> _lazy = new(() => new CloudSyncService());
        public static CloudSyncService Instance => _lazy.Value;

        /// <summary>上次因「模块不存在」触发愈合同步的时间（UTC），用于节流</summary>
        private DateTime _lastHealSyncUtc = DateTime.MinValue;

        // 接口基址统一收口在 Constants.URLs.ServerBaseUrl，发布时只需改那一处
        private const string SyncEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync";
        // 删除接口：软删除 bar/item（JSON Body 传 type + serverId）
        private const string DeleteEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync/delete";
        // 新增单个窗口接口：只提交新建的 bar（含 items），不触碰其他窗口
        private const string CreateBarEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync/bar";
        // 用户设置同步接口
        private const string SettingSyncEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/setting/sync";
        private const string SettingListEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/setting/list";
        // 模块归属实时变更接口
        private const string ItemOwnerEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync/item-owner";
        // 团队组件库接口
        private const string TeamLibraryEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/item/team-library";
        // 行级数据同步接口
        private const string RowPushEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/item-row/push";
        private const string RowPullEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/item-row/pull";
        // 资源排序接口
        private const string ResourcePosEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/resource-pos/save";
        // 轻量创建单个模块接口
        private const string CreateItemEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync/item";
        // 全量结构拉取接口（启动对账）
        private const string StructurePullEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/sync/pull";
        // 我仍有效的分享导入记录接口（启动对账清理失效共享组件用）
        private const string ShareImportsEndpoint = Constants.URLs.ServerBaseUrl + "/api/sidebar/share/imports/mine";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private bool _syncing;

        /// <summary>对账并发锁（防止启动对账与 WS 触发的对账同时执行）</summary>
        private bool _reconciling;

        /// <summary>行级同步快照：记录每个模块上次同步时的列表数据 JSON（用于差异对比）。key=PanelItem.Id</summary>
        private readonly Dictionary<string, string> _rowSnapshots = new();

        /// <summary>行推送并发锁（防止防抖推送和定时同步同时执行）</summary>
        private bool _pushingRows;

        /// <summary>即时行推送防抖定时器（2秒内的多次编辑合并为一次推送）</summary>
        private DispatcherTimer? _rowPushDebounceTimer;

        /// <summary>同步状态变化通知（状态, 说明文本），供 UI 显示</summary>
        public event Action<CloudSyncState, string>? StatusChanged;

        private CloudSyncService() { }

        /// <summary>开启云同步：执行一次启动对账（不再定时全量上传）。</summary>
        public void Start()
        {
            _ = ReconcileAsync(pushLocal: true);
        }

        /// <summary>停止云同步（已无定时任务，保留空实现兼容既有调用方）。</summary>
        public void Stop() { }

        /// <summary>
        /// 启动对账 / 结构刷新：
        /// 1) pushLocal=true 时先上传本地未同步的窗口/模块（离线变更）并回写 serverId；
        /// 2) 推送本地行级离线变更后，拉取服务端全量结构补齐本地缺失的窗口/模块；
        /// 3) 拉取行级数据与团队模块数据。
        /// 启动时与收到 structure_changed WS 通知（通知侧传 pushLocal=false）时调用。
        /// </summary>
        public async Task<bool> ReconcileAsync(bool pushLocal)
        {
            if (_reconciling) return false;

            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (string.IsNullOrEmpty(settings.AccountToken))
            {
                StatusChanged?.Invoke(CloudSyncState.NotLoggedIn, Framework.Lang.T("未登录，无法同步"));
                return false;
            }

            _reconciling = true;
            StatusChanged?.Invoke(CloudSyncState.Syncing, Framework.Lang.T("正在同步..."));
            try
            {
                // 1) 推送本地未同步的结构与行级离线变更（先推后拉，避免全量拉取覆盖本地新数据）
                if (pushLocal)
                {
                    await UploadStructureAsync(settings, app.WindowConfigs).ConfigureAwait(false);
                    await PushAllRowsAsync(app.WindowConfigs, settings).ConfigureAwait(false);
                }

                // 2) 拉取全量结构并合并，新建的模块需要全量拉取行数据（增量 since 拉不到旧行）
                var newPanels = new List<PanelItem>();
                var pulledItemServerIds = new HashSet<string>();
                bool structureChanged = await PullAndMergeStructureAsync(app.WindowConfigs, settings, newPanels, pulledItemServerIds).ConfigureAwait(false);
                foreach (var np in newPanels)
                {
                    if (RowChangeTracker.IsRowSyncable(np.Type) && !string.IsNullOrEmpty(np.ServerId))
                        await PullItemFullAsync(np).ConfigureAwait(false);
                }

                // 2.5) 清理已失效的共享组件（弥补离线时错过 WS 取消通知的场景）
                if (pushLocal)
                    structureChanged |= await CleanupRevokedSharesAsync(app.WindowConfigs, settings, pulledItemServerIds).ConfigureAwait(false);

                // 3) 拉取行级数据 + 团队模块数据
                await PullAllRowsAsync(app.WindowConfigs, settings).ConfigureAwait(false);
                await PullTeamItemsAsync(app.WindowConfigs).ConfigureAwait(false);

                settings.LastCloudSyncTime = DateTime.Now;
                settings.Save();

                if (structureChanged)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() => app.WindowConfigs.Save());
                    app.RefreshAllMainWindows();
                    // 结构变更可能增删窗口，同步刷新所有窗口的顶部标签栏
                    app.RefreshAllWindowTabs();
                }

                StatusChanged?.Invoke(CloudSyncState.Success, Framework.Lang.TF("已同步 · {0:yyyy-MM-dd HH:mm}", settings.LastCloudSyncTime));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 对账失败: {ex.Message}");
                StatusChanged?.Invoke(CloudSyncState.Failed, Framework.Lang.T("同步失败：") + ex.Message);
                return false;
            }
            finally
            {
                _reconciling = false;
            }
        }

        /// <summary>立即执行一次全量同步（兑底路径：全量结构上传 + 行推送 + 行拉取）。退出前、分享导入后与手动同步使用。</summary>
        public async Task<bool> SyncNowAsync(bool silent = false)
        {
            if (_syncing) return false;

            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;

            if (string.IsNullOrEmpty(settings.AccountToken))
            {
                StatusChanged?.Invoke(CloudSyncState.NotLoggedIn, Framework.Lang.T("未登录，无法同步"));
                return false;
            }

            _syncing = true;
            StatusChanged?.Invoke(CloudSyncState.Syncing, Framework.Lang.T("正在同步..."));
            try
            {
                if (!await UploadStructureAsync(settings, app.WindowConfigs).ConfigureAwait(false))
                {
                    StatusChanged?.Invoke(CloudSyncState.Failed, Framework.Lang.T("同步失败（结构上传失败）"));
                    return false;
                }

                settings.LastCloudSyncTime = DateTime.Now;
                settings.Save();

                // 行级数据同步：推送本地变更行 + 拉取服务端最新行
                await PushAllRowsAsync(app.WindowConfigs, settings).ConfigureAwait(false);
                await PullAllRowsAsync(app.WindowConfigs, settings).ConfigureAwait(false);

                StatusChanged?.Invoke(CloudSyncState.Success, Framework.Lang.TF("已同步 · {0:yyyy-MM-dd HH:mm}", settings.LastCloudSyncTime));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 同步失败: {ex.Message}");
                StatusChanged?.Invoke(CloudSyncState.Failed, Framework.Lang.T("同步失败：") + ex.Message);
                return false;
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>上传本地全量结构（bars/items），回写 localId -> serverId 映射并同步设置项。返回是否成功。</summary>
        private async Task<bool> UploadStructureAsync(Settings settings, WindowConfigManager mgr)
        {
            // 结构化载荷：使用内存中的实时窗口配置（bars/items）
            string bundle = BuildSyncPayloadJson(mgr);
            // 打印上传载荷到调试日志（缩进格式，便于复制给接口实现方；Release 构建自动移除）
            System.Diagnostics.Debug.WriteLine("[CloudSync] 上传载荷 POST " + SyncEndpoint + ":\n" + BuildSyncPayloadJson(mgr, indented: true));
            using var content = new StringContent(bundle, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, SyncEndpoint) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

            using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 结构上传失败 HTTP {(int)resp.StatusCode}");
                return false;
            }

            // 读取服务器响应，保存 localId -> serverId 映射到本地
            string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            // 打印服务器响应到调试日志（Release 构建自动移除）
            System.Diagnostics.Debug.WriteLine("[CloudSync] 服务器响应: " + responseJson);
            ApplyServerIdMappings(responseJson, settings, mgr);

            // 窗口同步成功后，追加设置项同步
            await SyncSettingsAsync(settings).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// 通知服务器删除指定的 bar 或 item（软删除）。
        /// type 为 "bar"（连带软删除其下所有 items）或 "item"（Group 类型连带软删除其子 items）。
        /// 仅在已登录且开启云同步、serverId 非空时发起请求。返回是否成功。
        /// </summary>
        public async Task<bool> DeleteOnServerAsync(string type, string serverId)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            // 仅登录状态且开启了云同步，才将删除发送到服务器
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken) || string.IsNullOrEmpty(serverId)) return false;

            try
            {
                string payload = NJson.JsonConvert.SerializeObject(new { type, serverId });
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 删除请求 POST {DeleteEndpoint}: {payload}");

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, DeleteEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 删除响应: {responseJson}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 删除失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>通知服务器删除 bar（连带软删除其下所有 items）。</summary>
        public Task<bool> DeleteBarOnServerAsync(string serverId) => DeleteOnServerAsync("bar", serverId);

        /// <summary>通知服务器删除 item（Group 类型连带软删除其子 items）。</summary>
        public Task<bool> DeleteItemOnServerAsync(string serverId) => DeleteOnServerAsync("item", serverId);

        /// <summary>
        /// 将单个新建窗口（含其模块）提交到服务器专用接口，不做全量同步。
        /// 仅在已登录且开启云同步时发起；成功后回写 localId -> serverId 映射。返回是否成功。
        /// </summary>
        public async Task<bool> CreateBarOnServerAsync(WindowConfig win)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return false;

            try
            {
                var payload = new
                {
                    app = "SideDesk",
                    machine = Environment.MachineName,
                    corpId = settings.SelectedCorpId,
                    bar = BuildSyncBar(win)
                };
                string json = NJson.JsonConvert.SerializeObject(payload, NJson.Formatting.None, _syncPayloadSettings);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 新增窗口请求 POST {CreateBarEndpoint}: {json}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, CreateBarEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 新增窗口响应: {responseJson}");
                if (!resp.IsSuccessStatusCode) return false;

                // 响应格式与全量同步一致，复用映射回写逻辑
                ApplyServerIdMappings(responseJson, settings, app.WindowConfigs);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 新增窗口提交失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 实时推送模块归属变更到服务器（个人 ↔ 团队），同步更新服务端 corp_id。
        /// 链路兜底：窗口未同步→先建窗口；模块未同步→先建模块；均就绪后推归属。
        /// 返回：true=已同步成功；false=尝试过但失败（含后端拒绝，失败原因见 LastOwnershipError）；null=未尝试（未登录/未开云同步）。
        /// </summary>
        public string? LastOwnershipError { get; private set; }

        public async Task<bool?> UpdateItemOwnershipAsync(PanelItem item, WindowConfig? win = null)
        {
            LastOwnershipError = null;
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return null;

            // 窗口尚未同步：先把整个窗口（含模块）提交，corpId 随载荷一并落库
            if (win != null && string.IsNullOrEmpty(win.ServerId))
            {
                if (!await CreateBarOnServerAsync(win))
                {
                    LastOwnershipError = Framework.Lang.T("窗口同步到服务器失败");
                    return false;
                }
            }

            // 模块尚未同步到服务端：先创建（创建时已携带 corpId）；
            // 窗口不可用（如分组内子模块拿不到窗口）则留给全量同步处理
            if (string.IsNullOrEmpty(item.ServerId))
            {
                if (win == null || string.IsNullOrEmpty(win.ServerId)) return null;
                var sid = await EnsureItemServerIdAsync(item, win.ServerId!);
                if (string.IsNullOrEmpty(sid))
                {
                    LastOwnershipError = Framework.Lang.T("模块同步到服务器失败");
                    return false;
                }
                return true;
            }

            try
            {
                // 团队 ID 为服务端加密串，直接透传，由后端解析
                string? corpId = null;
                if (item.OwnerType == "team" && !string.IsNullOrEmpty(item.TeamId))
                    corpId = item.TeamId;

                var payload = new { serverId = item.ServerId, corpId };
                string json = NJson.JsonConvert.SerializeObject(payload);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 模块归属变更 POST {ItemOwnerEndpoint}: {json}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, ItemOwnerEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 模块归属变更响应: {responseJson}");
                if (!resp.IsSuccessStatusCode)
                {
                    LastOwnershipError = Framework.Lang.TF("服务器拒绝（HTTP {0}）", (int)resp.StatusCode);
                    return false;
                }
                // HTTP 200 也可能业务失败（如后端返回“仅创建者可修改模块归属”），解析 code/msg
                try
                {
                    var root = JObject.Parse(responseJson);
                    if (root["code"]?.ToObject<int>() != 200)
                    {
                        var msg = root["msg"]?.ToString() ?? Framework.Lang.T("服务器处理失败");
                        // 他人创建的模块：服务端归属保持原样（导入组件本就不应改归属），视为无需同步，不报错
                        if (msg.Contains("仅创建者")) return null;
                        LastOwnershipError = msg;
                        return false;
                    }
                }
                catch { /* 非 JSON 响应但 HTTP 成功，视为成功 */ }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 模块归属变更失败: {ex.Message}");
                LastOwnershipError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从服务器拉取团队组件库数据，并将本地团队模块的数据更新为服务端最新版本（数据共享互通）。
        /// 在同步成功后及应用启动时调用。
        /// </summary>
        public async Task PullTeamItemsAsync(WindowConfigManager mgr)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (string.IsNullOrEmpty(settings.AccountToken)) return;
            // 仅当选中了某个团队时才拉取
            if (string.IsNullOrEmpty(settings.SelectedCorpId)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{TeamLibraryEndpoint}?corpId={settings.SelectedCorpId}");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = JObject.Parse(responseJson);
                if (root["code"]?.Value<int>() != 200) return;
                if (root["data"] is not JArray dataArr) return;

                // 构建 serverId -> itemData 映射
                var serverDataMap = new Dictionary<string, JObject>();
                foreach (var entry in dataArr.OfType<JObject>())
                {
                    string? serverId = entry["itemServerId"]?.ToString();
                    string? itemDataStr = entry["itemData"]?.ToString();
                    if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(itemDataStr)) continue;
                    try { serverDataMap[serverId!] = JObject.Parse(itemDataStr!); }
                    catch { }
                }

                if (serverDataMap.Count == 0) return;

                // 遍历本地所有团队模块，匹配 serverId 后更新数据
                bool changed = false;
                foreach (var win in mgr.Windows)
                {
                    foreach (var p in win.Panels)
                    {
                        if (ApplyTeamItemData(p, serverDataMap)) changed = true;
                    }
                }

                if (changed)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() => mgr.Save());
                    System.Diagnostics.Debug.WriteLine("[CloudSync] 团队模块数据已更新为服务端最新版本");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 拉取团队模块数据失败: {ex.Message}");
            }
        }

        /// <summary>递归匹配团队模块并应用服务端数据（含分组子模块）。</summary>
        private static bool ApplyTeamItemData(PanelItem p, Dictionary<string, JObject> serverDataMap)
        {
            bool changed = false;

            if (p.OwnerType == "team" && !string.IsNullOrEmpty(p.ServerId) && serverDataMap.TryGetValue(p.ServerId!, out var data))
            {
                // 用服务端数据覆盖本地数据字段（保留 Id/ServerId/Type/OwnerType/TeamId/TeamName 等元数据）
                try
                {
                    var id = p.Id;
                    var serverId = p.ServerId;
                    var type = p.Type;
                    var ownerType = p.OwnerType;
                    var teamId = p.TeamId;
                    var teamName = p.TeamName;

                    // 行级同步类型的列表/对象已改由行表读取，剔除服务端旧快照避免覆盖本地行数据
                    data.Remove("StickyNotes");
                    data.Remove("TodoItems");
                    data.Remove("TimerItems");
                    data.Remove("CountdownItems");
                    data.Remove("SignalLights");
                    data.Remove("ClipboardItems");
                    data.Remove("FileItems");
                    data.Remove("Pomodoro");
                    data.Remove("DashboardItems");
                    data.Remove("DigitalEmployeeIds");
                    data.Remove("FriendUserIds");
                    data.Remove("QuickItems");
                    NJson.JsonConvert.PopulateObject(data.ToString(), p);

                    p.Id = id;
                    p.ServerId = serverId;
                    p.Type = type;
                    p.OwnerType = ownerType;
                    p.TeamId = teamId;
                    p.TeamName = teamName;
                    changed = true;
                }
                catch { }
            }

            if (p.ChildPanels != null)
            {
                foreach (var c in p.ChildPanels)
                    if (ApplyTeamItemData(c, serverDataMap)) changed = true;
            }

            return changed;
        }

        #region 结构拉取与对账

        /// <summary>
        /// 拉取服务端全量窗口/模块结构并增量合并到本地：
        /// 缺失的个人窗口自动补齐，已有窗口补齐缺失模块；团队窗口不自动新建（由用户主动添加）。
        /// 新建的模块收集到 <paramref name="newPanelsOut"/>，由调用方全量拉取其行数据。
        /// </summary>
        private async Task<bool> PullAndMergeStructureAsync(WindowConfigManager mgr, Settings settings, List<PanelItem> newPanelsOut, HashSet<string>? pulledItemServerIdsOut = null)
        {
            try
            {
                string url = StructurePullEndpoint;
                if (!string.IsNullOrEmpty(settings.SelectedCorpId))
                    url += $"?corpId={Uri.EscapeDataString(settings.SelectedCorpId)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;

                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = JObject.Parse(responseJson);
                if (root["code"]?.Value<int>() != 200) return false;
                if (root["data"]?["bars"] is not JArray serverBars) return false;

                // 收集服务端全量模块 ServerId（含团队窗口），供失效共享清理判断归属
                if (pulledItemServerIdsOut != null)
                {
                    foreach (var barToken in serverBars.OfType<JObject>())
                        foreach (var itemToken in (barToken["items"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            var sid = itemToken["serverId"]?.ToString();
                            if (!string.IsNullOrEmpty(sid)) pulledItemServerIdsOut.Add(sid!);
                        }
                }

                bool changed = false;
                foreach (var barToken in serverBars.OfType<JObject>())
                {
                    string? barServerId = barToken["serverId"]?.ToString();
                    if (string.IsNullOrEmpty(barServerId)) continue;
                    var items = barToken["items"] as JArray ?? new JArray();

                    // 按 ServerId 查找本地窗口
                    WindowConfig? localWin = null;
                    foreach (var w in mgr.Windows)
                        if (w.ServerId == barServerId) { localWin = w; break; }

                    if (localWin == null)
                    {
                        // 团队窗口不自动创建；个人窗口直接新建并补齐模块
                        if (barToken["ownerType"]?.ToString() == "team") continue;

                        string barTitle = barToken["title"]?.ToString() ?? Framework.Lang.T("云端窗口");

                        // 认领优先：本地存在未同步（ServerId 为空）的同名个人窗口时回写 ServerId，
                        // 避免每次拉取都因匹配失败而重复新建整套窗口
                        foreach (var w in mgr.Windows)
                        {
                            if (w.ServerId == null && w.ShareMode == null && w.Name == barTitle)
                            {
                                w.ServerId = barServerId;
                                localWin = w;
                                System.Diagnostics.Debug.WriteLine($"[CloudSync] 认领本地未同步窗口: {w.Name} -> {barServerId}");
                                break;
                            }
                        }

                        if (localWin == null)
                        {
                            var win = new WindowConfig
                            {
                                Id = Guid.NewGuid().ToString(),
                                ServerId = barServerId,
                                Name = barTitle,
                                OwnerType = "personal",
                            };
                            if (MergeServerItemsIntoWindow(win, items, newPanelsOut) || items.Count == 0)
                            {
                                // 云端补齐的窗口同步进入两侧 dock，所有窗口顶部标签可见
                                mgr.AddToAllDocks(win);
                                changed = true;
                                System.Diagnostics.Debug.WriteLine($"[CloudSync] 从云端补齐缺失窗口: {win.Name} ({items.Count} 个模块)");
                            }
                            continue;
                        }

                        // 认领成功：回写 ServerId 需持久化，防止中途失败后下次拉取再次新建
                        mgr.Save();
                        changed = true;
                    }

                    // 窗口已存在：补齐缺失模块
                    if (MergeServerItemsIntoWindow(localWin, items, newPanelsOut))
                    {
                        changed = true;
                        System.Diagnostics.Debug.WriteLine($"[CloudSync] 为窗口 {localWin.Name} 补齐云端新增模块");
                    }
                }
                return changed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 结构拉取失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 将服务端模块清单补齐到窗口（已存在 ServerId 的跳过，本地版本优先）；
        /// group 子模块（groupId 非空）挂到父分组下。返回是否有新增模块。
        /// </summary>
        private static bool MergeServerItemsIntoWindow(WindowConfig win, JArray items, List<PanelItem> newPanelsOut)
        {
            // 收集本地已存在的模块 ServerId（含分组子项）
            var existingServerIds = new HashSet<string>();
            foreach (var p in EnumeratePanelsRecursive(win.Panels))
                if (!string.IsNullOrEmpty(p.ServerId)) existingServerIds.Add(p.ServerId!);

            // 第一遍：构建所有缺失模块
            var newPanels = new Dictionary<string, PanelItem>();
            var order = new List<JObject>();
            foreach (var itemToken in items.OfType<JObject>())
            {
                string? serverId = itemToken["serverId"]?.ToString();
                if (string.IsNullOrEmpty(serverId) || existingServerIds.Contains(serverId!)) continue;
                newPanels[serverId!] = BuildPanelFromServerItem(itemToken, win);
                order.Add(itemToken);
            }
            if (newPanels.Count == 0) return false;

            // 第二遍：分组子模块挂到父分组，其余按服务端顺序追加到窗口末尾
            foreach (var itemToken in order)
            {
                string serverId = itemToken["serverId"]!.ToString();
                var panel = newPanels[serverId];
                newPanelsOut.Add(panel);

                string? groupId = itemToken["groupId"]?.ToString();
                PanelItem? parent = null;
                if (!string.IsNullOrEmpty(groupId))
                {
                    if (!newPanels.TryGetValue(groupId!, out parent))
                    {
                        foreach (var p in EnumeratePanelsRecursive(win.Panels))
                            if (p.ServerId == groupId) { parent = p; break; }
                    }
                }

                if (parent != null)
                {
                    parent.ChildPanels ??= new List<PanelItem>();
                    parent.ChildPanels.Add(panel);
                }
                else
                {
                    win.Panels.Add(panel);
                }
            }
            return true;
        }

        /// <summary>从服务端模块结构构建 PanelItem（itemData 模块设置逐字段应用，列表数据由行级拉取另行填充）。</summary>
        private static PanelItem BuildPanelFromServerItem(JObject item, WindowConfig win)
        {
            string itemType = item["itemType"]?.ToString() ?? "StickyNotes";
            PanelType panelType = Enum.TryParse<PanelType>(itemType, true, out var parsed) ? parsed : PanelType.StickyNotes;

            var panel = new PanelItem
            {
                Id = Guid.NewGuid().ToString(),
                ServerId = item["serverId"]?.ToString(),
                Type = panelType,
                Title = item["title"]?.ToString(),
            };

            // 团队窗口内的模块继承团队归属
            if (win.OwnerType == "team")
            {
                panel.OwnerType = "team";
                panel.TeamId = win.CorpId;
            }

            // 应用 itemData 中的模块级设置
            if (item["itemData"] is JObject data)
            {
                foreach (var prop in data.Properties())
                {
                    // 元数据字段以本地为准，不允许 itemData 覆盖；
                    // 行级同步类型的列表/对象已改由行表读取，服务端 itemData 中的旧快照不得回灌
                    if (prop.Name is "Id" or "ServerId" or "Type" or "ChildPanels" or "Pomodoro"
                        or "StickyNotes" or "TodoItems" or "TimerItems" or "CountdownItems"
                        or "SignalLights" or "ClipboardItems" or "FileItems"
                        or "DashboardItems" or "DigitalEmployeeIds" or "FriendUserIds" or "QuickItems") continue;
                    var targetProp = panel.GetType().GetProperty(prop.Name);
                    if (targetProp != null && targetProp.CanWrite && prop.Value.Type != JTokenType.Null)
                    {
                        try { targetProp.SetValue(panel, prop.Value.ToObject(targetProp.PropertyType)); }
                        catch { /* 忽略单个字段解析失败 */ }
                    }
                }
            }
            return panel;
        }

        /// <summary>
        /// 启动对账时清理已失效的共享组件（弥补接收者离线时错过 share_cancelled WS 通知的场景）：
        /// 拉取当前用户仍有效的导入记录，与服务端结构比对——
        /// 1) 导入的引用窗口（有 SourceBarId）若分享已失效则整个移除；
        /// 2) 个人窗口内“不属于自己云端结构、也不在有效导入记录中”的共享面板移除（含服务端已删的残留模块）。
        /// 团队窗口不参与清理，避免误删。
        /// </summary>
        private async Task<bool> CleanupRevokedSharesAsync(WindowConfigManager mgr, Settings settings, HashSet<string> structureItemIds)
        {
            try
            {
                // 结构拉取失败时不能清理（无法判断归属，避免误删）
                if (structureItemIds.Count == 0) return false;

                using var req = new HttpRequestMessage(HttpMethod.Get, ShareImportsEndpoint);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);
                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;

                var root = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (root["code"]?.Value<int>() != 200 || root["data"] is not JArray imports) return false;

                var validItemServerIds = new HashSet<string>();
                var validImportLocalIds = new HashSet<string>();
                var validSourceBarIds = new HashSet<string>();
                foreach (var imp in imports.OfType<JObject>())
                {
                    var sid = imp["itemServerId"]?.ToString();
                    if (!string.IsNullOrEmpty(sid)) validItemServerIds.Add(sid!);
                    var lid = imp["localId"]?.ToString();
                    if (!string.IsNullOrEmpty(lid)) validImportLocalIds.Add(lid!);
                    var sb = imp["sourceBarId"]?.ToString();
                    if (!string.IsNullOrEmpty(sb)) validSourceBarIds.Add(sb!);
                }

                bool changed = false;
                var removedWindowIds = new List<string>();

                // 1) 窗口级：导入的引用窗口分享已失效 → 移除整个窗口
                for (int i = mgr.Windows.Count - 1; i >= 0; i--)
                {
                    var w = mgr.Windows[i];
                    if (string.IsNullOrEmpty(w.SourceBarId)) continue;
                    bool valid = validImportLocalIds.Contains(w.Id) || validSourceBarIds.Contains(w.SourceBarId!);
                    if (!valid)
                    {
                        mgr.Windows.RemoveAt(i);
                        removedWindowIds.Add(w.Id);
                        changed = true;
                        System.Diagnostics.Debug.WriteLine($"[CloudSync] 分享已失效，移除导入窗口: {w.Name}");
                    }
                }

                // 2) 模块级：个人窗口内残留的失效共享面板
                foreach (var w in mgr.Windows)
                {
                    if (w.OwnerType == "team") continue;
                    if (RemoveRevokedSharedPanels(w.Panels, structureItemIds, validItemServerIds, validImportLocalIds))
                    {
                        changed = true;
                        System.Diagnostics.Debug.WriteLine($"[CloudSync] 分享已失效，已清理窗口 {w.Name} 内的残留共享组件");
                    }
                }

                if (changed)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        mgr.Save();
                        var app = (SideDesk.App)System.Windows.Application.Current;
                        app.RemoveSharedWindowsByIds(removedWindowIds);
                    });
                }
                return changed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 失效共享清理失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>递归移除失效共享面板：ServerId 非空、非团队模块、不在自己云端结构、也不在有效导入记录中的面板</summary>
        private static bool RemoveRevokedSharedPanels(List<PanelItem> panels, HashSet<string> structureItemIds, HashSet<string> validItemServerIds, HashSet<string> validImportLocalIds)
        {
            bool removed = false;
            for (int i = panels.Count - 1; i >= 0; i--)
            {
                var p = panels[i];
                bool candidate = !string.IsNullOrEmpty(p.ServerId)
                    && p.OwnerType != "team"
                    && !structureItemIds.Contains(p.ServerId!)
                    && !validItemServerIds.Contains(p.ServerId!)
                    && !validImportLocalIds.Contains(p.Id);
                if (candidate)
                {
                    panels.RemoveAt(i);
                    removed = true;
                    continue;
                }
                if (p.ChildPanels != null)
                    removed |= RemoveRevokedSharedPanels(p.ChildPanels, structureItemIds, validItemServerIds, validImportLocalIds);
            }
            return removed;
        }

        #endregion

        #region 行级数据同步

        /// <summary>
        /// 通知行数据已变更（UI 编辑后调用）。防抖 2 秒后自动推送变更行到服务端。
        /// 无需指定具体模块——触发时会自动扫描所有有 ServerId 的行同步模块。
        /// </summary>
        public void NotifyRowsChanged()
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            // 未开启云同步：行级数据不提交服务器
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return;

            // 防抖：重置 2 秒定时器（触发非常频繁，不打日志；真正推送时才有日志）
            if (_rowPushDebounceTimer == null)
            {
                _rowPushDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _rowPushDebounceTimer.Tick += async (_, _) =>
                {
                    _rowPushDebounceTimer!.Stop();
                    // 定时器排队期间开关可能被关闭：触发时再次检查
                    if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return;
                    try
                    {
                        // 存在尚未取得 ServerId 的行同步模块（如新建模块）时，先上传结构获取 ServerId，
                        // 否则行推送会被跳过，新建模块的行数据无法实时更新到服务端
                        bool needStructure = false;
                        foreach (var win in app.WindowConfigs.Windows)
                        {
                            foreach (var p in EnumeratePanelsRecursive(win.Panels))
                            {
                                if (RowChangeTracker.IsRowSyncable(p.Type) && string.IsNullOrEmpty(p.ServerId))
                                {
                                    needStructure = true;
                                    break;
                                }
                            }
                            if (needStructure) break;
                        }
                        if (needStructure)
                        {
                            System.Diagnostics.Debug.WriteLine("[CloudSync] 存在未同步模块，先上传结构获取 ServerId");
                            await UploadStructureAsync(settings, app.WindowConfigs).ConfigureAwait(false);
                        }

                        await PushAllRowsAsync(app.WindowConfigs, settings).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CloudSync] 即时行推送失败: {ex.Message}");
                    }
                };
            }
            _rowPushDebounceTimer.Stop();
            _rowPushDebounceTimer.Start();
        }

        /// <summary>提取模块的列表数据为 JSON 字符串（用于快照对比）</summary>
        private static string? GetListDataJson(PanelItem p)
        {
            object? list = p.Type switch
            {
                PanelType.StickyNotes => p.StickyNotes,
                PanelType.TodoList => p.TodoItems,
                PanelType.Timer => p.TimerItems,
                PanelType.Countdown => p.CountdownItems,
                PanelType.SignalLight => p.SignalLights,
                PanelType.Clipboard => p.ClipboardItems,
                PanelType.FileArea => p.FileItems,
                // 番茄钟仅一条行记录，包装成单元素列表参与行级差异对比；
                // 剔除计时运行状态，云同步/分享只带番茄数与时长等配置
                PanelType.Pomodoro => p.Pomodoro == null ? null : new List<JObject> { SanitizePomodoroForSync(p.Pomodoro) },
                PanelType.DataDashboard => p.DashboardItems,
                // 数字员工/好友：引用 Id 列表包装为行对象参与行级差异对比
                PanelType.DigitalEmployee => p.DigitalEmployeeIds == null ? null
                    : p.DigitalEmployeeIds.Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => new DigitalEmployeeRow { Id = id!, EmployeeId = id! }).ToList(),
                PanelType.Friend => p.FriendUserIds == null ? null
                    : p.FriendUserIds.Select(id => new FriendUserRow { Id = id.ToString(), UserId = id.ToString() }).ToList(),
                PanelType.QuickGroup => p.QuickItems,
                _ => null
            };
            if (list == null) return null;
            return NJson.JsonConvert.SerializeObject(list, NJson.Formatting.None);
        }

        /// <summary>番茄钟行内容中不参与云同步/分享的运行状态字段（当前计时/暂停等仅本地保留）</summary>
        private static readonly string[] _pomodoroRuntimeProps = { "Mode", "RemainingSeconds", "IsRunning", "StartTime" };

        /// <summary>剔除番茄钟运行状态字段，仅保留番茄数、时长与行为配置（云同步/分享用）</summary>
        private static JObject SanitizePomodoroForSync(PomodoroSettings pom)
        {
            var jo = JObject.FromObject(pom);
            foreach (var name in _pomodoroRuntimeProps) jo.Remove(name);
            return jo;
        }

        /// <summary>获取模块列表数据的行集合（rowId -> JToken）</summary>
        private static Dictionary<string, JObject>? GetListRows(PanelItem p)
        {
            string? json = GetListDataJson(p);
            if (json == null) return null;
            try
            {
                var arr = JArray.Parse(json);
                var map = new Dictionary<string, JObject>();
                foreach (var item in arr.OfType<JObject>())
                {
                    string? id = item["Id"]?.ToString() ?? item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) map[id!] = item;
                }
                return map;
            }
            catch { return null; }
        }

        /// <summary>
        /// 行级推送：对比快照与当前数据，将变更行推送到服务端。
        /// 绑定同一 ServerId 的多个本地面板（如导入自己的分享做测试，原组件与导入组件共存）
        /// 会先取当前数据并集对齐后只推送一次，避免多视图互相推送删除差异清空对方数据。
        /// </summary>
        private async Task PushAllRowsAsync(WindowConfigManager mgr, Settings settings)
        {
            // 未开启云同步：行级数据不提交服务器（防御性兼顾所有调用方）
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return;
            if (_pushingRows) return; // 防止并发推送
            _pushingRows = true;
            try
            {
                // 收集所有支持行同步的面板（含分组子面板），按 ServerId 分组
                var groups = new Dictionary<string, List<PanelItem>>();
                foreach (var win in mgr.Windows)
                {
                    foreach (var p in EnumeratePanelsRecursive(win.Panels))
                    {
                        if (!RowChangeTracker.IsRowSyncable(p.Type)) continue;
                        if (string.IsNullOrEmpty(p.ServerId))
                        {
                            await PushPanelRows(p, settings).ConfigureAwait(false); // 内部记录日志并跳过
                            continue;
                        }
                        if (!groups.TryGetValue(p.ServerId!, out var list))
                            groups[p.ServerId!] = list = new List<PanelItem>();
                        list.Add(p);
                    }
                }

                foreach (var kv in groups)
                {
                    if (kv.Value.Count == 1)
                        await PushPanelRows(kv.Value[0], settings).ConfigureAwait(false);
                    else
                        await PushSiblingGroupAsync(kv.Value, settings).ConfigureAwait(false);
                }
            }
            finally
            {
                _pushingRows = false;
            }
        }

        private async Task PushPanelRows(PanelItem p, Settings settings)
        {
            if (!RowChangeTracker.IsRowSyncable(p.Type)) return;
            if (string.IsNullOrEmpty(p.ServerId))
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行推送跳过 ({p.Type}): ServerId 为空（模块尚未同步到服务端）");
                return;
            }

            string currentJson = GetListDataJson(p) ?? "[]";

            // 对比快照：无变化则静默跳过（Save/面板渲染会频繁触发本检查，跳过路径不打日志避免刷屏）
            if (_rowSnapshots.TryGetValue(p.Id, out var lastJson) && lastJson == currentJson)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[CloudSync] 行推送检查 ({p.Type}) ServerId={p.ServerId}, 数据长度={currentJson.Length}");

            // 计算差异
            var oldRows = _rowSnapshots.TryGetValue(p.Id, out var snap) ? ParseRowsFromJson(snap) : new Dictionary<string, JObject>();
            var newRows = ParseRowsFromJson(currentJson);
            string rowType = RowChangeTracker.GetRowType(p.Type);

            // 查询相关行的缓存服务端 ID：更新/删除优先走 serverId 寻址
            var idMap = SideDesk.Services.LocalDbService.GetRowServerIds(
                newRows.Keys.Concat(oldRows.Keys));

            var changedRows = new List<Dictionary<string, object>>();
            var deletedRowIds = new List<string>();
            var deletedRowServerIds = new List<string>();

            // 新增/修改：新快照中有，且与旧快照不同
            foreach (var kv in newRows)
            {
                if (!oldRows.TryGetValue(kv.Key, out var oldRow) || !JToken.DeepEquals(oldRow, kv.Value))
                    changedRows.Add(BuildRowPayload(kv.Key, rowType, kv.Value, idMap));
            }

            // 删除：旧快照中有，新快照中无。
            // 已缓存 serverId（已同步到服务端）→ 服务端主键删除；否则 GUID 兜底（缓存缺失/旧数据）
            foreach (var key in oldRows.Keys)
            {
                if (!newRows.ContainsKey(key))
                {
                    if (idMap.TryGetValue(key, out var sids) && sids.RowServerId != null)
                        deletedRowServerIds.Add(sids.RowServerId);
                    else
                        deletedRowIds.Add(key);
                }
            }

            // 无变更则只更新快照（静默）
            if (changedRows.Count == 0 && deletedRowIds.Count == 0 && deletedRowServerIds.Count == 0)
            {
                _rowSnapshots[p.Id] = currentJson;
                return;
            }

            bool ok = await SendRowPushAsync(p.ServerId!, changedRows, deletedRowIds, deletedRowServerIds, settings);
            if (ok)
            {
                _rowSnapshots[p.Id] = currentJson;
            }
        }

        /// <summary>组装单行推送载荷：GUID 保留为新行临时身份/兜底，已缓存的 serverId 一并携带（服务端优先主键寻址）</summary>
        private static Dictionary<string, object> BuildRowPayload(string rowGuid, string rowType, JObject content,
            Dictionary<string, (string? RowServerId, string? DataServerId)> idMap)
        {
            var rowObj = new Dictionary<string, object>
            {
                ["rowId"] = rowGuid,
                ["dataId"] = rowGuid,
                ["rowType"] = rowType,
                ["content"] = content,
                ["updatedAt"] = DateTime.Now.ToString("o")
            };
            if (idMap.TryGetValue(rowGuid, out var sids))
            {
                if (sids.RowServerId != null) rowObj["rowServerId"] = sids.RowServerId;
                if (sids.DataServerId != null) rowObj["dataServerId"] = sids.DataServerId;
            }
            return rowObj;
        }

        /// <summary>推送行级变更载荷到服务端；成功返回 true</summary>
        private async Task<bool> SendRowPushAsync(string serverId, List<Dictionary<string, object>> changedRows,
            List<string> deletedRowIds, List<string> deletedRowServerIds, Settings settings)
        {
            try
            {
                var payload = new { itemServerId = serverId, rows = changedRows, deletedRowIds, deletedRowServerIds };
                string json = NJson.JsonConvert.SerializeObject(payload);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级推送 POST {RowPushEndpoint}: {changedRows.Count} 行变更, {deletedRowServerIds.Count} 行主键删除, {deletedRowIds.Count} 行 GUID 兜底删除");
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级推送载荷: {json.Substring(0, Math.Min(json.Length, 500))}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, RowPushEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级推送响应 HTTP {(int)resp.StatusCode}: {respBody.Substring(0, Math.Min(respBody.Length, 300))}");
                if (!resp.IsSuccessStatusCode) return false;

                // 业务层失败（HTTP 200 但 code≠200，如“无权操作此模块”）也视为推送失败：
                // 不更新快照，下次推送会基于原快照重新计算差异重试；若误判成功数据会永久丢失
                try
                {
                    var root = JObject.Parse(respBody);
                    int bizCode = root["code"]?.ToObject<int>() ?? 200;
                    if (bizCode != 200)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级推送被业务层拒绝: code={bizCode}, msg={root["msg"]}");
                        // 「模块不存在」：服务端模块被对账软删而本地仍存在（多窗口/多设备上报竞态）。
                        // 触发一次全量结构同步，服务端 processItems 会复活该模块；行数据不丢，后续推送重试即可成功。
                        // 60 秒节流，避免多个面板同时被拒时连环触发全量同步
                        if (root["msg"]?.ToString() == "模块不存在"
                            && (DateTime.UtcNow - _lastHealSyncUtc).TotalSeconds > 60)
                        {
                            _lastHealSyncUtc = DateTime.UtcNow;
                            _ = SyncNowAsync(silent: true);
                        }
                        return false;
                    }

                    // 回填服务端返回的行主键映射：后续更新/删除即可改走 serverId 寻址
                    if (root["data"]?["rowIds"] is JObject rowIds)
                    {
                        var entries = new List<(string, string?, string?)>();
                        foreach (var prop in rowIds.Properties())
                        {
                            string? rsid = prop.Value["rowServerId"]?.ToString();
                            string? dsid = prop.Value["dataServerId"]?.ToString();
                            if (!string.IsNullOrEmpty(prop.Name) && (rsid != null || dsid != null))
                                entries.Add((prop.Name, rsid, dsid));
                        }
                        if (entries.Count > 0)
                            SideDesk.Services.LocalDbService.SaveRowServerIds(entries);
                    }
                }
                catch { /* 响应解析失败时保持原有 HTTP 状态码判定结果 */ }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级推送失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 绑定同一 ServerId 的多个本地面板（原组件与导入组件共存场景）的推送处理：
        /// 取各视图当前数据的并集（某行在某视图快照中存在但已从该视图消失则视为删除），
        /// 与旧快照并集对比计算差异，只推送一次，推送成功后将所有视图对齐到合并结果。
        /// </summary>
        private async Task PushSiblingGroupAsync(List<PanelItem> group, Settings settings)
        {
            // 1) 解析各视图的当前数据与快照
            var currents = new List<Dictionary<string, JObject>>();
            var snaps = new List<Dictionary<string, JObject>>();
            foreach (var p in group)
            {
                currents.Add(ParseRowsFromJson(GetListDataJson(p) ?? "[]"));
                snaps.Add(_rowSnapshots.TryGetValue(p.Id, out var snapJson)
                    ? ParseRowsFromJson(snapJson)
                    : new Dictionary<string, JObject>());
            }

            // 2) 并集：优先采纳“本地有改动”（快照≠当前）的视图的行。
            //    一个视图刚编辑、另一个视图还是旧数据时，旧视图不能覆盖新编辑
            var mergedRows = new Dictionary<string, JObject>();
            for (int i = 0; i < group.Count; i++)
            {
                foreach (var kv in currents[i])
                {
                    bool modified = !snaps[i].TryGetValue(kv.Key, out var snapRow) || !JToken.DeepEquals(snapRow, kv.Value);
                    if (modified) mergedRows[kv.Key] = kv.Value;
                }
            }
            // 未被任何视图改动的行：各视图内容一致，任取其一补齐
            for (int i = 0; i < group.Count; i++)
            {
                foreach (var kv in currents[i])
                    if (!mergedRows.ContainsKey(kv.Key)) mergedRows[kv.Key] = kv.Value;
            }

            // 3) 删除优先：某行在某视图快照中存在但已从该视图当前数据消失，则从并集中移除
            for (int i = 0; i < group.Count; i++)
            {
                foreach (var key in snaps[i].Keys)
                    if (!currents[i].ContainsKey(key))
                        mergedRows.Remove(key);
            }

            // 4) 与各视图旧快照的并集对比计算差异
            var oldUnion = new Dictionary<string, JObject>();
            foreach (var s in snaps)
            {
                foreach (var kv in s)
                    if (!oldUnion.ContainsKey(kv.Key)) oldUnion[kv.Key] = kv.Value;
            }

            string rowType = RowChangeTracker.GetRowType(group[0].Type);

            // 查询相关行的缓存服务端 ID：更新/删除优先走 serverId 寻址
            var idMap = SideDesk.Services.LocalDbService.GetRowServerIds(
                mergedRows.Keys.Concat(oldUnion.Keys));

            var changedRows = new List<Dictionary<string, object>>();
            foreach (var kv in mergedRows)
            {
                if (!oldUnion.TryGetValue(kv.Key, out var oldRow) || !JToken.DeepEquals(oldRow, kv.Value))
                    changedRows.Add(BuildRowPayload(kv.Key, rowType, kv.Value, idMap));
            }

            // 删除：已缓存 serverId → 服务端主键删除；否则 GUID 兜底
            var deletedRowIds = new List<string>();
            var deletedRowServerIds = new List<string>();
            foreach (var key in oldUnion.Keys.Where(k => !mergedRows.ContainsKey(k)))
            {
                if (idMap.TryGetValue(key, out var sids) && sids.RowServerId != null)
                    deletedRowServerIds.Add(sids.RowServerId);
                else
                    deletedRowIds.Add(key);
            }

            if (changedRows.Count == 0 && deletedRowIds.Count == 0 && deletedRowServerIds.Count == 0)
            {
                AlignSiblingGroup(group, mergedRows); // 无变更：也对齐各视图数据与快照
                return;
            }

            bool ok = await SendRowPushAsync(group[0].ServerId!, changedRows, deletedRowIds, deletedRowServerIds, settings);
            if (ok)
                AlignSiblingGroup(group, mergedRows);
            // 推送失败时不对齐：下次同步会基于原快照重新计算差异并重试
        }

        /// <summary>将绑定同一模块的多个视图的数据与快照对齐到合并结果；已对齐时直接跳过，
        /// 避免保存/刷新再次触发 NotifyRowsChanged 形成循环空转。</summary>
        private void AlignSiblingGroup(List<PanelItem> group, Dictionary<string, JObject> mergedRows)
        {
            var arr = new JArray(mergedRows.Values);
            string json = arr.ToString(NJson.Formatting.None);

            // 全部视图的数据与快照均已是合并结果 → 无需对齐（避免保存/刷新再次触发 NotifyRowsChanged 形成循环空转）。
            // 用内容对比而非字符串对比：序列化往返（类型对象 ↔ JToken）的格式差异不影响判定
            bool needAlign = false;
            foreach (var p in group)
            {
                var currentArr = new JArray(ParseRowsFromJson(GetListDataJson(p) ?? "[]").Values);
                if (!JToken.DeepEquals(currentArr, arr))
                {
                    needAlign = true;
                    break;
                }
                if (!_rowSnapshots.TryGetValue(p.Id, out var snap)
                    || !JToken.DeepEquals(new JArray(ParseRowsFromJson(snap).Values), arr))
                {
                    needAlign = true;
                    break;
                }
            }
            if (!needAlign) return;

            foreach (var p in group)
            {
                ApplyListDataToPanel(p, JArray.Parse(json));
                _rowSnapshots[p.Id] = json;
            }
            System.Diagnostics.Debug.WriteLine($"[CloudSync] 已对齐同模块 {group.Count} 个视图，共 {mergedRows.Count} 行");
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var app = (SideDesk.App)System.Windows.Application.Current;
                app.WindowConfigs.Save();
                app.RefreshAllMainWindows();
            });
        }

        /// <summary>
        /// 行级拉取：从服务端获取最新行数据并合并到本地。
        /// 团队模块全量拉取，个人模块增量拉取。
        /// </summary>
        public async Task PullAllRowsAsync(WindowConfigManager mgr, Settings settings)
        {
            // 未开启云同步：不从服务器拉取行级数据
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return;

            bool anyChanged = false;
            foreach (var win in mgr.Windows)
            {
                foreach (var p in win.Panels)
                {
                    if (await PullPanelRows(p, settings).ConfigureAwait(false)) anyChanged = true;
                    if (p.ChildPanels != null)
                        foreach (var c in p.ChildPanels)
                            if (await PullPanelRows(c, settings).ConfigureAwait(false)) anyChanged = true;
                }
            }

            if (anyChanged)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => mgr.Save());
                System.Diagnostics.Debug.WriteLine("[CloudSync] 行级拉取完成，本地数据已更新");
            }
        }

        /// <summary>对单个模块做一次全量行拉取（不带 since），用于 reference 分享导入后初始化共享数据。</summary>
        public async Task<bool> PullItemFullAsync(PanelItem p)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return false;
            bool changed = await PullPanelRows(p, settings, forceFull: true).ConfigureAwait(false);
            if (changed)
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => app.WindowConfigs.Save());
            return changed;
        }

        /// <summary>对单个模块做一次全量行拉取（不带 since）并按增量合并语义合并（不会整体删除未返回的本地行），用于 WS 实时刷新。</summary>
        public async Task<bool> PullItemMergeFullAsync(PanelItem p)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (!settings.EnableCloudSync || string.IsNullOrEmpty(settings.AccountToken)) return false;
            bool changed = await PullPanelRows(p, settings, fullMerge: true).ConfigureAwait(false);
            if (changed)
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => app.WindowConfigs.Save());
            return changed;
        }

        private async Task<bool> PullPanelRows(PanelItem p, Settings settings, bool forceFull = false, bool fullMerge = false)
        {
            if (!RowChangeTracker.IsRowSyncable(p.Type)) return false;
            if (string.IsNullOrEmpty(p.ServerId)) return false;

            try
            {
                // 团队模块全量拉取，个人模块增量拉取；forceFull 强制全量（replaceAll 语义），fullMerge 全量但按合并语义（WS 实时刷新）
                string url = RowPullEndpoint + $"?itemServerId={Uri.EscapeDataString(p.ServerId!)}";
                if (!forceFull && !fullMerge && p.OwnerType != "team" && settings.LastCloudSyncTime.HasValue)
                    url += $"&since={Uri.EscapeDataString(settings.LastCloudSyncTime.Value.ToString("o"))}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;

                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = JObject.Parse(responseJson);
                if (root["code"]?.Value<int>() != 200) return false;
                var rowsArr = root["data"]?["rows"] as JArray ?? new JArray();
                // 非全量拉取时无新数据直接返回；全量拉取即使为空也要走替换逻辑（分享取消后不可见的行需从本地清除）
                if (!forceFull && rowsArr.Count == 0) return false;

                // 合并行数据到本地列表（全量拉取时整体替换）
                bool merged = MergeRowsIntoPanel(p, rowsArr, replaceAll: forceFull);
                CacheRowServerIds(rowsArr);
                return merged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 行级拉取失败 ({p.Type}): {ex.Message}");
                return false;
            }
        }

        /// <summary>拉取回写：将服务端返回的行主键 ID（rowServerId/dataServerId）缓存到本地 SQLite，
        /// 供后续行级操作（如按服务端主键精确删除）直接使用。</summary>
        public static void CacheRowServerIds(Newtonsoft.Json.Linq.JArray rowsArr)
        {
            var entries = new List<(string, string?, string?)>();
            foreach (var r in rowsArr.OfType<Newtonsoft.Json.Linq.JObject>())
            {
                string? rowUid = r["rowId"]?.ToString();
                if (string.IsNullOrEmpty(rowUid)) continue;
                string? rowServerId = r["rowServerId"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? r["rowServerId"]?.ToString() : null;
                string? dataServerId = r["dataServerId"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? r["dataServerId"]?.ToString() : null;
                if (rowServerId == null && dataServerId == null) continue;
                entries.Add((rowUid!, rowServerId, dataServerId));
            }
            if (entries.Count > 0)
                SideDesk.Services.LocalDbService.SaveRowServerIds(entries);
        }

        /// <summary>将服务端行数据合并到本地模块列表（增/改/删）；replaceAll=true 时以服务端结果整体替换（全量拉取/分享取消刷新）</summary>
        private bool MergeRowsIntoPanel(PanelItem p, JArray serverRows, bool replaceAll = false)
        {
            string? currentJson = GetListDataJson(p);
            var localRows = ParseRowsFromJson(currentJson ?? "[]");
            // 类型守卫：服务端返回模块下全部行类型，仅合并与本面板类型一致的行，
            // 防止历史错绑/残留的跨类型行被反序列化进本面板（如番茄行灌入便签变空便签）
            string expectedRowType = RowChangeTracker.GetRowType(p.Type);

            if (replaceAll)
            {
                // 整体替换：本地只保留服务端可见的活跃行（不可见的行/墓碑均移除）
                var freshRows = new Dictionary<string, JObject>();
                foreach (var rowToken in serverRows.OfType<JObject>())
                {
                    string? rowId = rowToken["rowId"]?.ToString();
                    if (string.IsNullOrEmpty(rowId)) continue;
                    string? rowType = rowToken["rowType"]?.ToString();
                    if (!string.IsNullOrEmpty(rowType) && rowType != expectedRowType) continue;
                    bool isDeleted = rowToken["deletedAt"]?.Type == JTokenType.Date
                        || (rowToken["deletedAt"]?.Type == JTokenType.String && !string.IsNullOrEmpty(rowToken["deletedAt"]?.ToString()));
                    if (isDeleted) continue;
                    var content = rowToken["content"] as JObject ?? new JObject();
                    content["Id"] = rowId;
                    freshRows[rowId!] = content;
                }
                bool changed = !JToken.DeepEquals(new JArray(localRows.Values), new JArray(freshRows.Values));
                if (changed)
                {
                    var freshArr = new JArray(freshRows.Values);
                    ApplyListDataToPanel(p, freshArr);
                    // 同步更新快照，避免被移除的行被误判为本地待推送变更
                    _rowSnapshots[p.Id] = freshArr.ToString(NJson.Formatting.None);
                    // 镜像到绑定同一 ServerId 的其他本地视图
                    MirrorToSiblingPanels(p, freshArr);
                }
                return changed;
            }

            bool mergeChanged = false;

            foreach (var rowToken in serverRows.OfType<JObject>())
            {
                string? rowId = rowToken["rowId"]?.ToString();
                if (string.IsNullOrEmpty(rowId)) continue;
                string? rowType = rowToken["rowType"]?.ToString();
                if (!string.IsNullOrEmpty(rowType) && rowType != expectedRowType) continue;
                bool isDeleted = rowToken["deletedAt"]?.Type == JTokenType.Date
                    || (rowToken["deletedAt"]?.Type == JTokenType.String && !string.IsNullOrEmpty(rowToken["deletedAt"]?.ToString()));

                if (isDeleted)
                {
                    // 服务端已删除 → 本地也删除
                    if (localRows.Remove(rowId!)) mergeChanged = true;
                }
                else
                {
                    var content = rowToken["content"] as JObject ?? new JObject();
                    // 确保 content 中有 Id 字段
                    content["Id"] = rowId;

                    if (localRows.TryGetValue(rowId!, out var existing))
                    {
                        // 已存在：比较 updatedAt，服务端较新则覆盖
                        if (!JToken.DeepEquals(existing, content))
                        {
                            localRows[rowId!] = content;
                            mergeChanged = true;
                        }
                    }
                    else
                    {
                        // 本地无 → 添加
                        localRows[rowId!] = content;
                        mergeChanged = true;
                    }
                }
            }

            if (mergeChanged)
            {
                // 将合并后的行集合写回模块列表字段
                var mergedArr = new JArray(localRows.Values);
                ApplyListDataToPanel(p, mergedArr);
                // 更新快照
                _rowSnapshots[p.Id] = mergedArr.ToString(NJson.Formatting.None);
                // 镜像到绑定同一 ServerId 的其他本地视图
                MirrorToSiblingPanels(p, mergedArr);
            }

            return mergeChanged;
        }

        /// <summary>将 JArray 行数据写回 PanelItem 的对应列表字段</summary>
        private static void ApplyListDataToPanel(PanelItem p, JArray rows)
        {
            string json = rows.ToString();
            switch (p.Type)
            {
                case PanelType.StickyNotes:
                    p.StickyNotes = NJson.JsonConvert.DeserializeObject<List<StickyNote>>(json) ?? new();
                    break;
                case PanelType.TodoList:
                    p.TodoItems = NJson.JsonConvert.DeserializeObject<List<TodoItem>>(json) ?? new();
                    break;
                case PanelType.Timer:
                    p.TimerItems = NJson.JsonConvert.DeserializeObject<List<TimerItem>>(json) ?? new();
                    break;
                case PanelType.Countdown:
                    p.CountdownItems = NJson.JsonConvert.DeserializeObject<List<CountdownItem>>(json) ?? new();
                    break;
                case PanelType.SignalLight:
                    p.SignalLights = NJson.JsonConvert.DeserializeObject<List<SignalLightItem>>(json) ?? new();
                    break;
                case PanelType.Clipboard:
                    p.ClipboardItems = NJson.JsonConvert.DeserializeObject<List<ClipboardSavedItem>>(json) ?? new();
                    break;
                case PanelType.FileArea:
                    p.FileItems = NJson.JsonConvert.DeserializeObject<List<FileAreaItem>>(json) ?? new();
                    break;
                case PanelType.Pomodoro:
                    var pomodoros = NJson.JsonConvert.DeserializeObject<List<PomodoroSettings>>(json);
                    if (pomodoros != null && pomodoros.Count > 0)
                    {
                        var incoming = pomodoros[0];
                        // 远端行不含运行状态：保留本地计时状态，避免同步拉取/对齐打断当前计时
                        if (p.Pomodoro != null)
                        {
                            incoming.Mode = p.Pomodoro.Mode;
                            incoming.RemainingSeconds = p.Pomodoro.RemainingSeconds;
                            incoming.IsRunning = p.Pomodoro.IsRunning;
                            incoming.StartTime = p.Pomodoro.StartTime;
                        }
                        else
                        {
                            // 首次拉取（如导入分享）：剩余时间对齐时长配置，避免停留在默认值
                            incoming.RemainingSeconds = incoming.GetModeTotalSeconds();
                        }
                        p.Pomodoro = incoming;
                    }
                    break;
                case PanelType.DataDashboard:
                    p.DashboardItems = NJson.JsonConvert.DeserializeObject<List<DataDashboardItem>>(json) ?? new();
                    break;
                case PanelType.DigitalEmployee:
                    var deRows = NJson.JsonConvert.DeserializeObject<List<DigitalEmployeeRow>>(json) ?? new();
                    p.DigitalEmployeeIds = deRows.Select(r => r.EmployeeId).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    break;
                case PanelType.Friend:
                    var fuRows = NJson.JsonConvert.DeserializeObject<List<FriendUserRow>>(json) ?? new();
                    p.FriendUserIds = fuRows.Select(r => r.UserId).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    break;
                case PanelType.QuickGroup:
                    p.QuickItems = NJson.JsonConvert.DeserializeObject<List<QuickItem>>(json) ?? new();
                    break;
            }
        }

        private static Dictionary<string, JObject> ParseRowsFromJson(string json)
        {
            var map = new Dictionary<string, JObject>();
            try
            {
                var arr = JArray.Parse(json);
                foreach (var item in arr.OfType<JObject>())
                {
                    string? id = item["Id"]?.ToString() ?? item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) map[id!] = (JObject)item.DeepClone();
                }
            }
            catch { }
            return map;
        }

        /// <summary>将行数据同步到绑定同一 ServerId 的其他本地面板（多视图数据保持一致，如原组件与导入组件共存）</summary>
        private void MirrorToSiblingPanels(PanelItem p, JArray rows)
        {
            try
            {
                var app = (SideDesk.App)System.Windows.Application.Current;
                if (app?.WindowConfigs == null || string.IsNullOrEmpty(p.ServerId)) return;
                string json = rows.ToString(NJson.Formatting.None);
                bool mirrored = false;
                foreach (var win in app.WindowConfigs.Windows)
                {
                    foreach (var q in EnumeratePanelsRecursive(win.Panels))
                    {
                        if (ReferenceEquals(q, p) || q.ServerId != p.ServerId) continue;
                        if (!RowChangeTracker.IsRowSyncable(q.Type)) continue;
                        ApplyListDataToPanel(q, JArray.Parse(json));
                        _rowSnapshots[q.Id] = json;
                        mirrored = true;
                    }
                }
                if (mirrored)
                {
                    System.Diagnostics.Debug.WriteLine($"[CloudSync] 已镜像行数据到同模块其他视图 ({p.Type})");
                    app.RefreshAllMainWindows();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 兄弟视图同步失败: {ex.Message}");
            }
        }

        /// <summary>递归枚举面板树（含分组子面板）</summary>
        private static IEnumerable<PanelItem> EnumeratePanelsRecursive(List<PanelItem> panels)
        {
            foreach (var p in panels)
            {
                yield return p;
                if (p.ChildPanels != null)
                    foreach (var c in EnumeratePanelsRecursive(p.ChildPanels))
                        yield return c;
            }
        }

        #endregion

        #region 云同步结构化载荷

        /// <summary>云同步上传的 bar（窗口）DTO</summary>
        private class SyncBar
        {
            [NJson.JsonProperty("localId")] public string LocalId { get; set; } = "";
            [NJson.JsonProperty("serverId")] public string? ServerId { get; set; }  // 已同步过的带服务器 ID（供服务器去重/更新）；null 时省略
            [NJson.JsonProperty("name")] public string Name { get; set; } = "";
            [NJson.JsonProperty("bgColor")] public string? BGColor { get; set; }
            [NJson.JsonProperty("ownerType")] public string? OwnerType { get; set; }  // 归属类型: personal/team
            [NJson.JsonProperty("corpId")] public string? CorpId { get; set; }           // 所属团队ID（服务端加密串，后端解析）
            [NJson.JsonProperty("items")] public List<SyncItem> Items { get; set; } = new();
        }

        /// <summary>云同步上传的 item（模块）DTO；group 的子项放在 <see cref="Items"/>（仅一层）</summary>
        private class SyncItem
        {
            [NJson.JsonProperty("localId")] public string LocalId { get; set; } = "";
            [NJson.JsonProperty("serverId")] public string? ServerId { get; set; }  // 已同步过的带服务器 ID（供服务器去重/更新）；null 时省略
            [NJson.JsonProperty("type")] public string Type { get; set; } = "";
            [NJson.JsonProperty("title")] public string? Title { get; set; }
            [NJson.JsonProperty("bgColor")] public string? BGColor { get; set; }
            [NJson.JsonProperty("corpId")] public string? CorpId { get; set; }           // 所属团队ID（服务端加密串，null=个人）
            [NJson.JsonProperty("data")] public JObject Data { get; set; } = new();
            [NJson.JsonProperty("items")] public List<SyncItem>? Items { get; set; }
        }

        /// <summary>云同步上传的数据看板变量 DTO（key-value，团队/个人作用域）。一个数据项可对应多个变量。</summary>
        private class SyncVariable
        {
            [NJson.JsonProperty("itemLocalId")] public string? ItemLocalId { get; set; }
            [NJson.JsonProperty("varKey")] public string VarKey { get; set; } = "";
            [NJson.JsonProperty("varValue")] public string? VarValue { get; set; }
            [NJson.JsonProperty("label")] public string? Label { get; set; }
            [NJson.JsonProperty("corpId")] public string? CorpId { get; set; }  // 团队ID（服务端加密串，null=个人）
        }

        /// <summary>云同步上传的数据看板数据项配置 DTO（名称/网址/选择器/间隔/浏览器/团队归属）。</summary>
        private class SyncDataItem
        {
            [NJson.JsonProperty("itemLocalId")] public string? ItemLocalId { get; set; }
            [NJson.JsonProperty("label")] public string? Label { get; set; }
            [NJson.JsonProperty("targetUrl")] public string? TargetUrl { get; set; }
            [NJson.JsonProperty("selector")] public string? Selector { get; set; }
            [NJson.JsonProperty("selectorType")] public string? SelectorType { get; set; }  // xpath/css/regex/js（空=旧版自动识别）
            [NJson.JsonProperty("refreshInterval")] public int? RefreshInterval { get; set; }
            [NJson.JsonProperty("browserId")] public string? BrowserId { get; set; }
            [NJson.JsonProperty("profileId")] public string? ProfileId { get; set; }
            [NJson.JsonProperty("templateId")] public string? TemplateId { get; set; }
            [NJson.JsonProperty("corpId")] public string? CorpId { get; set; }  // 团队ID（服务端加密串，null=个人）
        }

        /// <summary>云同步上传的数据看板变量集 DTO（多变量聚合配置，团队/个人作用域）。</summary>
        private class SyncVariableSet
        {
            [NJson.JsonProperty("varKey")] public string VarKey { get; set; } = "";
            [NJson.JsonProperty("op")] public string? Op { get; set; }
            [NJson.JsonProperty("sourceKeys")] public List<string>? SourceKeys { get; set; }
            [NJson.JsonProperty("pattern")] public string? Pattern { get; set; }
            [NJson.JsonProperty("corpId")] public string? CorpId { get; set; }  // 团队ID（服务端加密串，null=个人）
        }

        private static readonly NJson.JsonSerializerSettings _syncPayloadSettings = new()
        {
            NullValueHandling = NJson.NullValueHandling.Ignore
        };

        /// <summary>
        /// 构建云同步结构化上传载荷：{ app, machine, corpId, syncedAt, bars[] }。
        /// bar 含 localId/name/bgColor/items；item 含 localId/type/title/bgColor/data，
        /// group 类型的 item 额外带一层子项（items）。
        /// </summary>
        public static string BuildSyncPayloadJson(WindowConfigManager mgr, bool indented = false)
        {
            // 同一 ServerId 模块出现在多个窗口时（自导入 reference 共存：原窗口 + 导入视图），
            // 上传只保留第一个窗口中的条目，后续窗口跳过：避免服务端把模块归属在窗口间
            // 来回挪动（最后上传的窗口生效），导致对账拉取时另一窗口反复“补齐”出重复模块。
            var emittedServerIds = new HashSet<string>();
            var bars = new List<SyncBar>();
            foreach (var win in mgr.Windows)
                bars.Add(BuildSyncBar(win, emittedServerIds));

            // 收集所有数据看板变量（设置了变量名的数据项；含不绑定面板的后台数据项）
            var variables = new List<SyncVariable>();
            foreach (var win in mgr.Windows)
                foreach (var p in win.Panels)
                    CollectVariables(p, variables);
            foreach (var di in mgr.BackgroundDashboardItems)
                CollectVariableFromItem(di, variables);

            // 收集所有数据看板数据项配置（全量，后端结构化记录；含后台数据项）
            var dataItems = new List<SyncDataItem>();
            foreach (var win in mgr.Windows)
                foreach (var p in win.Panels)
                    CollectDataItems(p, dataItems);
            foreach (var di in mgr.BackgroundDashboardItems)
                CollectDataItemFromItem(di, dataItems);

            // 收集变量集配置（多变量聚合；团队归属透传加密串，空=个人）
            var variableSets = new List<SyncVariableSet>();
            foreach (var s in mgr.VariableSets)
            {
                variableSets.Add(new SyncVariableSet
                {
                    VarKey = s.VarKey,
                    Op = s.Op,
                    SourceKeys = s.SourceKeys,
                    Pattern = string.IsNullOrEmpty(s.Pattern) ? null : s.Pattern,
                    CorpId = string.IsNullOrEmpty(s.TeamId) ? null : s.TeamId,
                });
            }

            // 当前选中的团队 ID（个人模式为 null，由后端自行处理）
            string? corpId = null;
            try
            {
                var app = (SideDesk.App)System.Windows.Application.Current;
                corpId = app?.AppSettings?.SelectedCorpId;
            }
            catch { }

            var payload = new
            {
                app = "SideDesk",
                machine = Environment.MachineName,
                corpId,
                syncedAt = DateTime.Now.ToString("o"),
                bars,
                variables,
                dataItems,
                variableSets
            };
            return NJson.JsonConvert.SerializeObject(payload,
                indented ? NJson.Formatting.Indented : NJson.Formatting.None,
                _syncPayloadSettings);
        }

        /// <summary>递归收集面板（含分组子面板）内设置了变量名的数据看板项，转为 SyncVariable。</summary>
        private static void CollectVariables(PanelItem p, List<SyncVariable> vars)
        {
            if (p.DashboardItems != null)
                foreach (var di in p.DashboardItems)
                    CollectVariableFromItem(di, vars);
            if (p.ChildPanels != null)
                foreach (var c in p.ChildPanels)
                    CollectVariables(c, vars);
        }

        /// <summary>将单个设置了变量名的数据项转为 SyncVariable（面板项与后台数据项共用）。</summary>
        private static void CollectVariableFromItem(DataDashboardItem di, List<SyncVariable> vars)
        {
            if (string.IsNullOrEmpty(di.VarKey)) return;
            var v = new SyncVariable
            {
                ItemLocalId = di.Id,
                VarKey = di.VarKey,
                VarValue = di.Value,
                Label = di.Label,
            };
            // 团队ID：数据项归属的团队（服务端加密串，个人则为 null）
            if (!string.IsNullOrEmpty(di.TeamId))
                v.CorpId = di.TeamId;
            vars.Add(v);
        }

        /// <summary>递归收集面板（含分组子面板）内所有数据看板数据项的配置，转为 SyncDataItem。</summary>
        private static void CollectDataItems(PanelItem p, List<SyncDataItem> items)
        {
            if (p.DashboardItems != null)
                foreach (var di in p.DashboardItems)
                    CollectDataItemFromItem(di, items);
            if (p.ChildPanels != null)
                foreach (var c in p.ChildPanels)
                    CollectDataItems(c, items);
        }

        /// <summary>将单个数据看板数据项配置转为 SyncDataItem（面板项与后台数据项共用）。</summary>
        private static void CollectDataItemFromItem(DataDashboardItem di, List<SyncDataItem> items)
        {
            var it = new SyncDataItem
            {
                ItemLocalId = di.Id,
                Label = di.Label,
                TargetUrl = di.TargetUrl,
                Selector = di.Selector,
                SelectorType = string.IsNullOrEmpty(di.SelectorType) ? null : di.SelectorType,
                RefreshInterval = di.RefreshIntervalSeconds,
                BrowserId = di.BrowserId,
                ProfileId = di.ProfileId,
                TemplateId = di.TemplateId,
            };
            // 团队ID：数据项归属的团队（服务端加密串，个人则为 null）
            if (!string.IsNullOrEmpty(di.TeamId))
                it.CorpId = di.TeamId;
            items.Add(it);
        }

        /// <summary>将单个窗口配置转为 SyncBar（含其全部模块）。</summary>
        private static SyncBar BuildSyncBar(WindowConfig win, HashSet<string>? emittedServerIds = null)
        {
            emittedServerIds ??= new HashSet<string>();
            var bar = new SyncBar { LocalId = win.Id, ServerId = win.ServerId, Name = win.Name, BGColor = win.BGColor, OwnerType = win.OwnerType != "personal" ? win.OwnerType : null };
            // 团队 ID 为服务端加密串，直接透传，由后端解析
            if (win.OwnerType == "team" && !string.IsNullOrEmpty(win.CorpId))
                bar.CorpId = win.CorpId;
            foreach (var p in win.Panels)
            {
                var item = BuildSyncItem(p, isInsideGroup: false, emittedServerIds);
                if (item != null) bar.Items.Add(item);
            }
            return bar;
        }

        /// <summary>将单个模块转为 SyncItem；group 的子项仅展开一层（group 内不再嵌套 group）。
        /// 共享 ServerId 已在前面窗口上传过时返回 null（跳过）。</summary>
        private static SyncItem? BuildSyncItem(PanelItem p, bool isInsideGroup, HashSet<string> emittedServerIds)
        {
            if (!string.IsNullOrEmpty(p.ServerId) && !emittedServerIds.Add(p.ServerId!))
                return null;
            var item = new SyncItem
            {
                LocalId = p.Id,
                ServerId = p.ServerId,
                Type = p.Type.ToString(),
                Title = p.Title,
                BGColor = p.BGColor ?? p.GroupBGColor,
                Data = BuildItemData(p)
            };
            // 团队归属时附带团队 ID（服务端加密串，后端解析）
            if (p.OwnerType == "team" && !string.IsNullOrEmpty(p.TeamId))
                item.CorpId = p.TeamId;

            if (!isInsideGroup && p.Type == PanelType.Group && p.ChildPanels is { Count: > 0 })
            {
                item.Items = new List<SyncItem>();
                foreach (var child in p.ChildPanels)
                {
                    var childItem = BuildSyncItem(child, isInsideGroup: true, emittedServerIds);
                    if (childItem != null) item.Items.Add(childItem);
                }
            }
            return item;
        }

        /// <summary>模块内容参数：仅保留模块级设置，排除列表数据（列表数据通过行级端点同步）。</summary>
        private static JObject BuildItemData(PanelItem p)
        {
            var jo = JObject.FromObject(p);
            jo.Remove("Id");
            jo.Remove("ServerId");
            jo.Remove("Type");
            jo.Remove("Title");
            jo.Remove("BGColor");
            jo.Remove("GroupBGColor");
            jo.Remove("ChildPanels");
            // 排除列表数据（改由行级同步端点处理）
            jo.Remove("StickyNotes");
            jo.Remove("TodoItems");
            jo.Remove("TimerItems");
            jo.Remove("CountdownItems");
            jo.Remove("SignalLights");
            jo.Remove("ClipboardItems");
            jo.Remove("FileItems");
            jo.Remove("Pomodoro");
            jo.Remove("DashboardItems");
            jo.Remove("DigitalEmployeeIds");
            jo.Remove("FriendUserIds");
            jo.Remove("QuickItems");

            foreach (var prop in jo.Properties().Where(pr => pr.Value.Type == JTokenType.Null).ToList())
                prop.Remove();
            return jo;
        }

        /// <summary>
        /// 解析服务器响应中的 localId -> serverId 映射，同时写入两处：
        /// 1) <see cref="Settings.CloudIdMap"/>（settings.json 独立映射表）；
        /// 2) 对应 WindowConfig / PanelItem 的 ServerId 字段（windows.json 模型自身）。
        /// 响应格式：{ code: 200, data: { bars:[{localId,serverId}], items:[...], groups:[...] } }。
        /// 解析失败、code 非 200 或无 data 时静默跳过，不影响同步成功状态。
        /// </summary>
        private static void ApplyServerIdMappings(string responseJson, Settings settings, WindowConfigManager mgr)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(responseJson)) return;
                var root = JObject.Parse(responseJson);

                // 成功标志：code == 200
                if (root["code"]?.Value<int>() != 200) return;
                if (root["data"] is not JObject data) return;

                bool changed = false;

                // 映射 key 带账号作用域前缀：防止切换账号后 A 的 serverId 泄漏给 B（实际寻址走模型 ServerId 字段）
                var scopePrefix = LocalDbService.CurrentScope() + ":";

                // bars -> WindowConfig.ServerId
                if (data["bars"] is JArray barArr)
                {
                    foreach (var entry in barArr.OfType<JObject>())
                    {
                        string? localId = entry["localId"]?.ToString();
                        string? serverId = entry["serverId"]?.ToString();
                        if (string.IsNullOrEmpty(localId) || string.IsNullOrEmpty(serverId)) continue;
                        settings.CloudIdMap[scopePrefix + localId!] = serverId!;
                        var win = mgr.GetById(localId!);
                        if (win != null) win.ServerId = serverId;
                        changed = true;
                    }
                }

                // items / groups -> PanelItem.ServerId（group 本质也是 PanelItem）
                foreach (var section in new[] { "items", "groups" })
                {
                    if (data[section] is not JArray arr) continue;
                    foreach (var entry in arr.OfType<JObject>())
                    {
                        string? localId = entry["localId"]?.ToString();
                        string? serverId = entry["serverId"]?.ToString();
                        if (string.IsNullOrEmpty(localId) || string.IsNullOrEmpty(serverId)) continue;
                        settings.CloudIdMap[scopePrefix + localId!] = serverId!;
                        var item = FindPanelItem(mgr, localId!);
                        if (item != null) item.ServerId = serverId;
                        changed = true;
                    }
                }

                if (changed)
                {
                    settings.Save();
                    mgr.Save();
                    // 遗留 xid- 密文经服务端认领收敛后，同窗口内多个重复面板会被映射到同一 ServerId，
                    // 此处一次性去重（仅在真正移除过重复面板时写完成标记）
                    mgr.DedupPanelsSharingServerIdOnce();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 解析服务器 ID 映射失败: {ex.Message}");
            }
        }

        /// <summary>按 localId 在所有窗口的模块（含 group 子项）中查找 PanelItem。</summary>
        private static PanelItem? FindPanelItem(WindowConfigManager mgr, string localId)
        {
            foreach (var win in mgr.Windows)
            {
                foreach (var p in win.Panels)
                {
                    if (p.Id == localId) return p;
                    if (p.ChildPanels != null)
                    {
                        foreach (var c in p.ChildPanels)
                            if (c.Id == localId) return c;
                    }
                }
            }
            return null;
        }

        #endregion

        #region 设置项云同步

        /// <summary>需要同步的设置项白名单（key = 上传标识, category, isSensitive）</summary>
        private static readonly (string Key, string Category, bool Sensitive)[] SettingWhitelist =
        {
            // general
            ("general.theme_mode",        "general", false),
            ("general.background_opacity","general", false),
            ("general.show_notification", "general", false),
            ("general.enable_auto_update","general", false),
            ("general.enable_mcp_server", "general", false),
            ("general.data_connection",   "general", false),
            // cloud_drive
            ("baidu_pan.access_token",    "cloud_drive", true),
            ("baidu_pan.refresh_token",   "cloud_drive", true),
            ("baidu_pan.token_expiry",    "cloud_drive", false),
        };

        /// <summary>从 Settings 对象提取指定 key 的值</summary>
        private static string? GetSettingValue(Settings s, string key)
        {
            return key switch
            {
                "general.theme_mode"         => s.ThemeMode,
                "general.background_opacity" => s.BackgroundOpacity.ToString(),
                "general.show_notification"  => s.ShowNotificationStrip.ToString(),
                "general.enable_auto_update" => s.EnableAutoUpdate.ToString(),
                "general.enable_mcp_server"  => s.EnableMcpServer.ToString(),
                "general.data_connection"    => s.DataConnectionMode,
                "baidu_pan.access_token"     => s.BaiduPanAccessToken,
                "baidu_pan.refresh_token"    => s.BaiduPanRefreshToken,
                "baidu_pan.token_expiry"     => s.BaiduPanTokenExpiry?.ToString("o"),
                _ => null
            };
        }

        /// <summary>将服务器拉取的设置值写回本地 Settings</summary>
        private static void ApplySettingValue(Settings s, string key, string? value)
        {
            switch (key)
            {
                case "general.theme_mode":         s.ThemeMode = value ?? "Dark"; break;
                case "general.background_opacity": if (double.TryParse(value, out var op)) s.BackgroundOpacity = op; break;
                case "general.show_notification":  s.ShowNotificationStrip = value == "True"; break;
                case "general.enable_auto_update": s.EnableAutoUpdate = value == "True"; break;
                case "general.enable_mcp_server":  s.EnableMcpServer = value == "True"; break;
                case "general.data_connection":    s.DataConnectionMode = value ?? "extension"; break;
                case "baidu_pan.access_token":     s.BaiduPanAccessToken = value; break;
                case "baidu_pan.refresh_token":    s.BaiduPanRefreshToken = value; break;
                case "baidu_pan.token_expiry":
                    s.BaiduPanTokenExpiry = DateTime.TryParse(value, out var exp) ? exp : null;
                    break;
            }
        }

        /// <summary>获取当前加密密钥（若已启用加密同步且本地有缓存密钥）</summary>
        private static byte[]? GetEncryptionKey(Settings settings)
        {
            if (!settings.SyncEncryptEnabled || string.IsNullOrEmpty(settings.SyncDerivedKey))
                return null;
            try
            {
                return SyncEncryption.UnprotectKeyFromLocal(settings.SyncDerivedKey);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将本地设置项同步到服务器。敏感字段在启用加密同步时加密后上传。
        /// </summary>
        private async Task SyncSettingsAsync(Settings settings)
        {
            try
            {
                byte[]? encKey = GetEncryptionKey(settings);
                var items = new List<object>();

                foreach (var (key, category, sensitive) in SettingWhitelist)
                {
                    string? rawValue = GetSettingValue(settings, key);
                    if (rawValue == null) continue;

                    int isEncrypted = 0;
                    string uploadValue = rawValue;

                    if (sensitive && encKey != null)
                    {
                        uploadValue = SyncEncryption.Encrypt(rawValue, encKey);
                        isEncrypted = 1;
                    }

                    items.Add(new { key, value = uploadValue, isEncrypted, category });
                }

                if (items.Count == 0) return;

                var payload = new { settings = items };
                string json = NJson.JsonConvert.SerializeObject(payload);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 设置同步 POST {SettingSyncEndpoint}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, SettingSyncEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    settings.LastSettingsSyncTime = DateTime.Now;
                    settings.Save();
                    System.Diagnostics.Debug.WriteLine("[CloudSync] 设置同步成功");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CloudSync] 设置同步失败: HTTP {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 设置同步异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 从服务器拉取设置并写回本地（用于“恢复设置”功能）。
        /// 加密字段自动解密。返回恢复的设置项数，-1 表示失败。
        /// </summary>
        public async Task<int> RestoreSettingsAsync()
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;

            if (string.IsNullOrEmpty(settings.AccountToken)) return -1;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, SettingListEndpoint);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return -1;

                string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = JObject.Parse(responseJson);
                if (root["code"]?.Value<int>() != 200) return -1;
                if (root["data"] is not JArray dataArr) return -1;

                byte[]? encKey = GetEncryptionKey(settings);
                int count = 0;

                foreach (var entry in dataArr.OfType<JObject>())
                {
                    string? key = entry["key"]?.ToString();
                    string? value = entry["value"]?.ToString();
                    int isEncrypted = entry["isEncrypted"]?.Value<int>() ?? 0;

                    if (string.IsNullOrEmpty(key) || value == null) continue;

                    // 解密敏感字段
                    if (isEncrypted == 1 && encKey != null && SyncEncryption.IsEncrypted(value))
                    {
                        try { value = SyncEncryption.Decrypt(value, encKey); }
                        catch { continue; } // 解密失败跳过该项
                    }

                    ApplySettingValue(settings, key, value);
                    count++;
                }

                if (count > 0) settings.Save();
                return count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 恢复设置失败: {ex.Message}");
                return -1;
            }
        }

        #endregion

        #region 本地备份 / 恢复 / 导出

        // 本地备份目录与文件（覆盖式，仅保留最后一个）
        private static string BackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "backup");
        private static string LastBackupFile => Path.Combine(BackupDir, "last-backup.json");

        /// <summary>是否存在本地备份。</summary>
        public static bool HasLocalBackup() => File.Exists(LastBackupFile);

        /// <summary>获取最后一次本地备份的时间（无则返回 null）。</summary>
        public static DateTime? GetLocalBackupTime()
            => File.Exists(LastBackupFile) ? File.GetLastWriteTime(LastBackupFile) : null;

        /// <summary>创建本地备份（覆盖式，只保留最后一个）。返回是否成功。</summary>
        public static bool CreateLocalBackup()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                File.WriteAllText(LastBackupFile, BuildConfigBundleJson(indented: true));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>从最后一次本地备份恢复所有配置。返回恢复的文件数（-1 表示失败/无备份）。</summary>
        public static int RestoreLocalBackup()
        {
            try
            {
                if (!File.Exists(LastBackupFile)) return -1;
                return RestoreFromBundleJson(File.ReadAllText(LastBackupFile));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 恢复失败: {ex.Message}");
                return -1;
            }
        }

        /// <summary>把打包 JSON 中的文件写回各自的原始位置。返回写回的文件数。</summary>
        public static int RestoreFromBundleJson(string json)
        {
            int count = 0;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
                return 0;

            foreach (var prop in filesEl.EnumerateObject())
            {
                string? fileContent = prop.Value.GetString();
                if (fileContent == null) continue;
                string target = MapBackupKeyToPath(prop.Name);
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(target, fileContent);
                    count++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Backup] 写回 {prop.Name} 失败: {ex.Message}");
                }
            }

            // 窗口配置主存储已改为 SQLite：若备份写回了 windows.json，需重新导入 sidedesk.db，
            // 否则重启后仍读旧 db，恢复不生效
            try
            {
                string windowsJson = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk", "windows.json");
                if (File.Exists(windowsJson))
                    LocalDbService.ReimportJsonIfExists(windowsJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 重新导入 windows.json 到 SQLite 失败: {ex.Message}");
            }
            return count;
        }

        /// <summary>将备份键映射回原始文件路径（与 BuildConfigBundleJson 的键命名一致）。</summary>
        private static string MapBackupKeyToPath(string key)
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
            return key switch
            {
                "app-settings.json" => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"),
                "user-templates.json" => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json"),
                _ => Path.Combine(local, key)
            };
        }

        #endregion

        /// <summary>收集本地全部配置文件，打包成一个 JSON 负载。</summary>
        public static string BuildConfigBundleJson(bool indented = false)
        {
            var files = new Dictionary<string, string>();

            // 1) %LocalAppData%\SideDesk\ 下的全部 *.json（quickgroups.json、
            //    bookmarks.json、aitools.json、programs.json、browser_profiles.json、settings.json 等）
            string localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
            AddJsonFilesFromFolder(files, localFolder);

            // 窗口配置主存储为 SQLite（windows.json 已停止写盘）：备份时从内存实时序列化，
            // 键名沿用 windows.json，与恢复路径（写回后 ReimportJsonIfExists 导入 db）保持一致
            try
            {
                if (Application.Current is App app && app.WindowConfigs != null)
                    files["windows.json"] = NJson.JsonConvert.SerializeObject(app.WindowConfigs, NJson.Formatting.Indented);
            }
            catch { }

            // 2) 程序目录下的 settings.json（应用停靠/主题/账户等设置）
            TryAddFile(files,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"),
                "app-settings.json");

            // 3) 自定义数据模版
            TryAddFile(files,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json"),
                "user-templates.json");

            var payload = new
            {
                app = "SideDesk",
                machine = Environment.MachineName,
                syncedAt = DateTime.Now.ToString("o"),
                files
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = indented });
        }

        private static void AddJsonFilesFromFolder(Dictionary<string, string> files, string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var path in Directory.GetFiles(folder, "*.json"))
                {
                    string key = Path.GetFileName(path);
                    TryAddFile(files, path, key);
                }
            }
            catch { }
        }

        private static void TryAddFile(Dictionary<string, string> files, string path, string key)
        {
            try
            {
                if (File.Exists(path))
                    files[key] = File.ReadAllText(path);
            }
            catch { }
        }

        #region 资源排序同步

        /// <summary>
        /// 推送资源排序到服务端。
        /// resourceType: "bar" / "item" / "row"
        /// serverIds: 按当前顺序排列的加密服务端 ID 列表
        /// corpId: 团队模块行调序时传入（团队加密串，user_id 置 0，全团队共享顺序）
        /// </summary>
        public async Task PushResourcePosAsync(string resourceType, List<string> serverIds, string? corpId = null)
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (string.IsNullOrEmpty(settings.AccountToken)) return;
            if (serverIds == null || serverIds.Count == 0) return;

            try
            {
                var items = serverIds
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select((id, idx) => new { resourceServerId = id, posInx = idx })
                    .ToList();
                if (items.Count == 0) return;

                var payload = new { resourceType, items, corpId };
                string json = NJson.JsonConvert.SerializeObject(payload);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 排序推送 POST {ResourcePosEndpoint}: type={resourceType}, count={items.Count}, corpId={corpId}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, ResourcePosEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 排序推送响应 HTTP {(int)resp.StatusCode}: {respBody.Substring(0, Math.Min(respBody.Length, 200))}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 排序推送失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 轻量接口：确保单个模块已同步到服务端，返回加密的 serverId。
        /// 如果模块已有 ServerId 则直接返回，否则调用 /sidebar/sync/item 创建。
        /// </summary>
        public async Task<string?> EnsureItemServerIdAsync(Models.PanelItem item, string barServerId)
        {
            if (!string.IsNullOrEmpty(item.ServerId)) return item.ServerId;

            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;
            if (string.IsNullOrEmpty(settings.AccountToken) || string.IsNullOrEmpty(barServerId)) return null;

            try
            {
                // 构建与全量同步一致的 item 载荷（去除列表数据）
                var itemData = BuildItemData(item);
                // 团队 ID 为服务端加密串，直接透传，由后端解析
                string? corpId = null;
                if (item.OwnerType == "team" && !string.IsNullOrEmpty(item.TeamId))
                    corpId = item.TeamId;

                var payload = new
                {
                    barServerId,
                    item = new
                    {
                        localId = item.Id,
                        type = item.Type.ToString(),
                        title = item.Title,
                        corpId,
                        data = itemData
                    }
                };
                string json = NJson.JsonConvert.SerializeObject(payload);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 创建模块 POST {CreateItemEndpoint}: localId={item.Id}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, CreateItemEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 创建模块响应: {respBody}");

                var root = JObject.Parse(respBody);
                if (root["code"]?.ToObject<int>() == 200)
                {
                    var serverId = root["data"]?["serverId"]?.ToString();
                    if (!string.IsNullOrEmpty(serverId))
                    {
                        item.ServerId = serverId;
                        return serverId;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 创建模块失败: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
