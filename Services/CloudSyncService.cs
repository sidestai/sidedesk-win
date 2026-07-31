using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
    /// 云同步服务：开启后每 30 分钟把本地全部配置以结构化载荷（bars/items 大 JSON）上传到服务器，退出时再同步一次。
    /// 上传成功后解析服务器返回的 localId -> serverId 映射并保存到本地（<see cref="Settings.CloudIdMap"/>）。
    /// 说明：后端同步接口尚未确定，<see cref="SyncEndpoint"/> 为占位地址，接入真实接口时替换即可。
    /// </summary>
    public sealed class CloudSyncService
    {
        private static readonly Lazy<CloudSyncService> _lazy = new(() => new CloudSyncService());
        public static CloudSyncService Instance => _lazy.Value;

        // TODO: 上线时替换为正式环境同步接口地址（当前为测试环境）
        private const string SyncEndpoint = "http://localhost:3007/api/sidebar/sync";
        // 删除接口：软删除 bar/item（JSON Body 传 type + serverId）
        private const string DeleteEndpoint = "http://localhost:3007/api/sidebar/sync/delete";
        // 新增单个窗口接口：只提交新建的 bar（含 items），不触碰其他窗口
        private const string CreateBarEndpoint = "http://localhost:3007/api/sidebar/sync/bar";
        // 用户设置同步接口
        private const string SettingSyncEndpoint = "http://localhost:3007/api/sidebar/setting/sync";
        private const string SettingListEndpoint = "http://localhost:3007/api/sidebar/setting/list";

        private const int IntervalMinutes = 30;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _timer;
        private bool _syncing;

        /// <summary>同步状态变化通知（状态, 说明文本），供 UI 显示</summary>
        public event Action<CloudSyncState, string>? StatusChanged;

        private CloudSyncService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(IntervalMinutes) };
            _timer.Tick += async (_, _) => await SyncNowAsync(silent: true);
        }

        public bool IsRunning => _timer.IsEnabled;

        /// <summary>开启定时同步（若已开启则忽略），并立即触发一次同步。</summary>
        public void Start()
        {
            if (_timer.IsEnabled) return;
            _timer.Start();
            _ = SyncNowAsync(silent: true);
        }

        /// <summary>停止定时同步。</summary>
        public void Stop() => _timer.Stop();

        /// <summary>立即执行一次同步。返回是否成功。</summary>
        public async Task<bool> SyncNowAsync(bool silent = false)
        {
            if (_syncing) return false;

            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;

            if (string.IsNullOrEmpty(settings.AccountToken))
            {
                StatusChanged?.Invoke(CloudSyncState.NotLoggedIn, "未登录，无法同步");
                return false;
            }

            _syncing = true;
            StatusChanged?.Invoke(CloudSyncState.Syncing, "正在同步...");
            try
            {
                // 结构化载荷：使用内存中的实时窗口配置（bars/items）
                string bundle = BuildSyncPayloadJson(app.WindowConfigs);
                // 打印上传载荷到调试日志（缩进格式，便于复制给接口实现方；Release 构建自动移除）
                System.Diagnostics.Debug.WriteLine("[CloudSync] 上传载荷 POST " + SyncEndpoint + ":\n" + BuildSyncPayloadJson(app.WindowConfigs, indented: true));
                using var content = new StringContent(bundle, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, SyncEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    // 读取服务器响应，保存 localId -> serverId 映射到本地
                    string responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // 打印服务器响应到调试日志（Release 构建自动移除）
                    System.Diagnostics.Debug.WriteLine("[CloudSync] 服务器响应: " + responseJson);
                    ApplyServerIdMappings(responseJson, settings, app.WindowConfigs);

                    settings.LastCloudSyncTime = DateTime.Now;
                    settings.Save();

                    // 窗口同步成功后，追加设置项同步
                    await SyncSettingsAsync(settings).ConfigureAwait(false);

                    StatusChanged?.Invoke(CloudSyncState.Success, $"已同步 · {settings.LastCloudSyncTime:yyyy-MM-dd HH:mm}");
                    return true;
                }

                StatusChanged?.Invoke(CloudSyncState.Failed, $"同步失败（HTTP {(int)resp.StatusCode}）");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 同步失败: {ex.Message}");
                StatusChanged?.Invoke(CloudSyncState.Failed, "同步失败：" + ex.Message);
                return false;
            }
            finally
            {
                _syncing = false;
            }
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

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
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

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
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

        #region 云同步结构化载荷

        /// <summary>云同步上传的 bar（窗口）DTO</summary>
        private class SyncBar
        {
            [NJson.JsonProperty("localId")] public string LocalId { get; set; } = "";
            [NJson.JsonProperty("serverId")] public string? ServerId { get; set; }  // 已同步过的带服务器 ID（供服务器去重/更新）；null 时省略
            [NJson.JsonProperty("name")] public string Name { get; set; } = "";
            [NJson.JsonProperty("bgColor")] public string? BGColor { get; set; }
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
            [NJson.JsonProperty("data")] public JObject Data { get; set; } = new();
            [NJson.JsonProperty("items")] public List<SyncItem>? Items { get; set; }
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
            var bars = new List<SyncBar>();
            foreach (var win in mgr.Windows)
                bars.Add(BuildSyncBar(win));

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
                bars
            };
            return NJson.JsonConvert.SerializeObject(payload,
                indented ? NJson.Formatting.Indented : NJson.Formatting.None,
                _syncPayloadSettings);
        }

        /// <summary>将单个窗口配置转为 SyncBar（含其全部模块）。</summary>
        private static SyncBar BuildSyncBar(WindowConfig win)
        {
            var bar = new SyncBar { LocalId = win.Id, ServerId = win.ServerId, Name = win.Name, BGColor = win.BGColor };
            foreach (var p in win.Panels)
                bar.Items.Add(BuildSyncItem(p, isInsideGroup: false));
            return bar;
        }

        /// <summary>将单个模块转为 SyncItem；group 的子项仅展开一层（group 内不再嵌套 group）。</summary>
        private static SyncItem BuildSyncItem(PanelItem p, bool isInsideGroup)
        {
            var item = new SyncItem
            {
                LocalId = p.Id,
                ServerId = p.ServerId,
                Type = p.Type.ToString(),
                Title = p.Title,
                BGColor = p.BGColor ?? p.GroupBGColor,
                Data = BuildItemData(p)
            };

            if (!isInsideGroup && p.Type == PanelType.Group && p.ChildPanels is { Count: > 0 })
            {
                item.Items = new List<SyncItem>();
                foreach (var child in p.ChildPanels)
                    item.Items.Add(BuildSyncItem(child, isInsideGroup: true));
            }
            return item;
        }

        /// <summary>模块内容参数：除 Id/Type/Title/BGColor/GroupBGColor/ChildPanels 外的全部字段，剔除 null。</summary>
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

                // bars -> WindowConfig.ServerId
                if (data["bars"] is JArray barArr)
                {
                    foreach (var entry in barArr.OfType<JObject>())
                    {
                        string? localId = entry["localId"]?.ToString();
                        string? serverId = entry["serverId"]?.ToString();
                        if (string.IsNullOrEmpty(localId) || string.IsNullOrEmpty(serverId)) continue;
                        settings.CloudIdMap[localId!] = serverId!;
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
                        settings.CloudIdMap[localId!] = serverId!;
                        var item = FindPanelItem(mgr, localId!);
                        if (item != null) item.ServerId = serverId;
                        changed = true;
                    }
                }

                if (changed)
                {
                    settings.Save();
                    mgr.Save();
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

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
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

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
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

            // 1) %LocalAppData%\SideDesk\ 下的全部 *.json（windows.json、quickgroups.json、
            //    bookmarks.json、aitools.json、programs.json、browser_profiles.json、
            //    title-lock-rules.json、digital-employees.json、settings.json 等）
            string localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
            AddJsonFilesFromFolder(files, localFolder);

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
    }
}
