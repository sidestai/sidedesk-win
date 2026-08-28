using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    /// <summary>
    /// MCP 工具定义（来自服务端注册表或内置）
    /// </summary>
    public class McpToolDef
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>参数 Schema（OpenAI function parameters JSON 字符串，可为空）</summary>
        public string? ParamsSchema { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsBuiltin { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    /// <summary>
    /// MCP 工具注册表服务（网页版工具配置的客户端侧）：
    /// 已登录时从服务端拉取用户工具清单，首次自动回传内置工具种子；清单变化时通知 MCP Server 重建。
    /// 未登录（隐私模式）时不使用服务端清单，MCP/AI 回退内置工具。
    /// </summary>
    public sealed class McpToolRegistryService
    {
        private static readonly Lazy<McpToolRegistryService> _instance = new(() => new McpToolRegistryService());
        public static McpToolRegistryService Instance => _instance.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl + "/api/mcp-tool";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>本地下载的官方 MCP 配置文件（未登录时的工具来源）</summary>
        private static readonly string PresetFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDesk", "mcp-tools-preset.json");

        private readonly System.Threading.Timer? _pollTimer;
        private readonly object _lock = new();
        private List<McpToolDef> _tools = new();
        private string _hash = "";
        private bool _seeded;
        /// <summary>推送触发的延迟刷新时刻（短时间多次推送合并为一次拉取）</summary>
        private DateTime _pushRefreshAt = DateTime.MinValue;

        /// <summary>注册表（含启停标记）；未登录或未拉取成功时为空列表</summary>
        public IReadOnlyList<McpToolDef> Tools { get { lock (_lock) return _tools.ToList(); } }

        /// <summary>服务端清单是否已生效（已登录拉取成功，或未登录但本地预置配置已加载）</summary>
        public bool IsActive { get; private set; }

        /// <summary>当前工具来源：server=账号云端清单，local=本地下载的官方配置，builtin=内置硬编码</summary>
        public string Source { get; private set; } = "builtin";

        /// <summary>本地预置配置版本号（下载后记录）</summary>
        public int PresetVersion { get; private set; }

        /// <summary>本地预置配置的更新日期（服务端文件中的 updatedAt）</summary>
        public string PresetUpdatedAt { get; private set; } = "";

        /// <summary>清单变化时触发（需要重建 MCP Server / 刷新 AI 工具列表）</summary>
        public event Action? RegistryChanged;

        private McpToolRegistryService()
        {
            // 清单变更以 WebSocket 推送为主（OnRegistryChangedPush），轮询仅作兜底（推送丢失/未连接时每 10 分钟拉一次）
            _pollTimer = new System.Threading.Timer(_ =>
            {
                // 离线时不发请求，避免无效重试刷后端日志
                if (App.AccountOffline) return;
                _ = SyncAsync();
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

            // 后端推送 mcp_registry_changed（网页端改了工具配置）→ 延迟 500ms 合并多次推送后重新拉取
            BackendWebSocketService.Instance.MessageReceived += text =>
            {
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(text);
                    if ((string?)obj["event"] == "mcp_registry_changed")
                        OnRegistryChangedPush();
                }
                catch { /* 非 JSON 或其它消息：忽略 */ }
            };
        }

        /// <summary>收到服务端清单变更推送：延迟 500ms 后拉取一次（期间再次推送只顺延时刻，避免连续操作时连续拉取）</summary>
        private void OnRegistryChangedPush()
        {
            bool first;
            lock (_lock)
            {
                first = _pushRefreshAt <= DateTime.Now;
                _pushRefreshAt = DateTime.Now.AddMilliseconds(500);
            }
            if (!first) return;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    DateTime due;
                    lock (_lock) due = _pushRefreshAt;
                    var wait = due - DateTime.Now;
                    if (wait > TimeSpan.Zero) { await Task.Delay(wait); continue; }
                    lock (_lock) _pushRefreshAt = DateTime.MinValue;
                    System.Diagnostics.Debug.WriteLine("[McpRegistry] 收到清单变更推送，重新拉取");
                    await SyncAsync();
                    break;
                }
            });
        }

        /// <summary>
        /// 同步服务端工具清单：未登录 → 清空回退内置；已登录 → 拉取（首次空则种子回传），
        /// 清单哈希变化时触发 RegistryChanged。
        /// </summary>
        public async Task SyncAsync()
        {
            try
            {
                var app = (App)Application.Current;
                var token = app.AppSettings.AccountToken;

                if (string.IsNullOrEmpty(token))
                {
                    // 未登录（隐私模式）：不用服务端清单，优先用本地下载的官方配置，否则回退内置
                    ApplyLocalPresetOrClear();
                    return;
                }

                // 每次会话首次同步：回传内置工具定义（upsert：新增插入，已有的覆盖更新描述/参数，保留启停设置）
                if (!_seeded)
                {
                    _seeded = true;
                    await SeedBuiltinAsync(token);
                }

                var list = await FetchListAsync(token);
                if (list == null) return; // 请求失败：保持现状

                string hash = ComputeHash(list);
                bool changed;
                lock (_lock)
                {
                    changed = hash != _hash;
                    if (changed)
                    {
                        _tools = list;
                        _hash = hash;
                    }
                }

                bool wasActive = IsActive;
                IsActive = true;
                Source = "server";
                if (changed || !wasActive)
                    RegistryChanged?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 同步异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 未登录时的工具来源处理：本地存在已下载的官方配置 → 加载生效；
        /// 否则回退内置硬编码工具。
        /// </summary>
        private void ApplyLocalPresetOrClear()
        {
            var preset = LoadLocalPreset();
            if (preset != null && preset.Count > 0)
            {
                string hash = ComputeHash(preset);
                bool changed;
                lock (_lock)
                {
                    changed = hash != _hash;
                    if (changed)
                    {
                        _tools = preset;
                        _hash = hash;
                    }
                }
                bool wasActive = IsActive;
                IsActive = true;
                Source = "local";
                if (changed || !wasActive)
                    RegistryChanged?.Invoke();
            }
            else
            {
                Source = "builtin";
                if (IsActive)
                {
                    IsActive = false;
                    lock (_lock) { _tools.Clear(); _hash = ""; }
                    RegistryChanged?.Invoke();
                }
            }
        }

        /// <summary>加载本地下载的官方配置文件（失败返回 null）</summary>
        private List<McpToolDef>? LoadLocalPreset()
        {
            try
            {
                if (!File.Exists(PresetFilePath)) return null;
                var root = JObject.Parse(File.ReadAllText(PresetFilePath));
                var tools = root["tools"] as JArray;
                if (tools == null || tools.Count == 0) return null;

                PresetVersion = root.Value<int?>("version") ?? 0;
                PresetUpdatedAt = root.Value<string>("updatedAt") ?? "";

                int idx = 0;
                return tools.Select(t => new McpToolDef
                {
                    Id = ++idx,
                    Name = t.Value<string>("name") ?? "",
                    Description = t.Value<string>("description") ?? "",
                    ParamsSchema = t["paramsSchema"]?.ToString(Formatting.None),
                    Enabled = true,
                    IsBuiltin = true,
                    UpdatedAt = PresetUpdatedAt
                }).Where(t => !string.IsNullOrEmpty(t.Name)).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 本地配置加载失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从服务端下载官方 MCP 工具配置并保存到本地（无需登录）。
        /// 未登录时立即生效；已登录时仅保存，登出后自动启用。
        /// </summary>
        public async Task<(bool ok, string msg)> DownloadPresetAsync()
        {
            try
            {
                using var resp = await _http.GetAsync($"{BaseUrl}/preset");
                var root = JObject.Parse(await resp.Content.ReadAsStringAsync());
                if (root["code"]?.ToObject<int>() != 200)
                    return (false, root["msg"]?.ToString() ?? Framework.Lang.T("system.fetchConfigFailed"));

                var data = root["data"];
                var tools = data?["tools"] as JArray;
                if (tools == null || tools.Count == 0)
                    return (false, Framework.Lang.T("system.mcpNoToolDefinitions"));

                var dir = Path.GetDirectoryName(PresetFilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(PresetFilePath, data!.ToString(Formatting.Indented));

                var version = data.Value<int?>("version") ?? 0;
                var updatedAt = data.Value<string>("updatedAt") ?? "";

                // 未登录 → 立即生效；已登录 → 仅保存（云端清单优先），登出后自动启用
                var app = (App)Application.Current;
                if (string.IsNullOrEmpty(app.AppSettings.AccountToken))
                    ApplyLocalPresetOrClear();

                return (true, $"已更新 {tools.Count} 个工具定义（配置版本 v{version}，更新于 {updatedAt}）");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 下载配置失败: {ex.Message}");
                return (false, $"下载失败：{ex.Message}");
            }
        }

        /// <summary>本地配置文件的最后更新时间（未下载过返回 null）</summary>
        public static DateTime? GetLocalPresetFileTime()
        {
            try { return File.Exists(PresetFilePath) ? File.GetLastWriteTime(PresetFilePath) : null; }
            catch { return null; }
        }

        /// <summary>拉取工具清单（失败返回 null）</summary>
        private static async Task<List<McpToolDef>?> FetchListAsync(string token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/list");
                req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;

                var root = JObject.Parse(await resp.Content.ReadAsStringAsync());
                if (root["code"]?.ToObject<int>() != 200) return null;
                var data = root["data"] as JArray ?? new JArray();

                return data.Select(r => new McpToolDef
                {
                    Id = r.Value<int>("id"),
                    Name = r.Value<string>("name") ?? "",
                    Description = r.Value<string>("description") ?? "",
                    ParamsSchema = r.Value<string>("paramsSchema"),
                    Enabled = r.Value<int>("enabled") == 1,
                    IsBuiltin = r.Value<int>("isBuiltin") == 1,
                    UpdatedAt = r.Value<string>("updatedAt") ?? ""
                }).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 拉取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>回传内置工具定义（服务端 upsert：新增插入，已有内置工具覆盖更新定义，保留启停设置）</summary>
        private static async Task SeedBuiltinAsync(string token)
        {
            try
            {
                var seeds = AIToolService.GetToolDefinitions().Select(d =>
                {
                    var f = ((dynamic)d).function;
                    return new
                    {
                        name = (string)f.name,
                        description = (string)f.description,
                        paramsSchema = JsonConvert.SerializeObject(f.parameters)
                    };
                }).ToList();

                var body = JsonConvert.SerializeObject(new { tools = seeds });
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/init")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await _http.SendAsync(req);
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 种子同步: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpRegistry] 种子同步失败: {ex.Message}");
            }
        }

        /// <summary>清单哈希（内容变化检测）</summary>
        private static string ComputeHash(List<McpToolDef> list)
        {
            var sb = new StringBuilder();
            foreach (var t in list)
                sb.Append(t.Id).Append('|').Append(t.Name).Append('|').Append(t.Description).Append('|')
                  .Append(t.ParamsSchema ?? "").Append('|').Append(t.Enabled).Append(';');
            using var md5 = MD5.Create();
            return Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        /// <summary>
        /// 获取当前生效的 OpenAI function 格式工具定义：
        /// 服务端清单生效时用清单（仅启用项），否则返回内置定义。
        /// </summary>
        public static List<object> GetActiveToolDefinitions()
        {
            var reg = Instance;
            if (reg.IsActive)
            {
                var defs = reg.Tools.Where(t => t.Enabled).Select(t =>
                {
                    object parameters;
                    try { parameters = string.IsNullOrEmpty(t.ParamsSchema) ? DefaultParams() : JsonConvert.DeserializeObject(t.ParamsSchema!) ?? DefaultParams(); }
                    catch { parameters = DefaultParams(); }
                    return (object)new
                    {
                        type = "function",
                        function = new { name = t.Name, description = t.Description, parameters }
                    };
                }).ToList();
                if (defs.Count > 0) return defs;
            }
            return AIToolService.GetToolDefinitions();

            static object DefaultParams() => new { type = "object", properties = new { }, required = new string[] { } };
        }
    }
}
