using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    #region API 数据模型

    /// <summary>每日活跃单日数值（后端 sidedesk_daily_activity 表的一行）</summary>
    public class DailyActivityValue
    {
        [Newtonsoft.Json.JsonProperty("date")] public string Date { get; set; } = "";   // yyyy-MM-dd
        [Newtonsoft.Json.JsonProperty("value")] public double Value { get; set; }
    }

    #endregion

    /// <summary>
    /// 每日活跃内置固定类型：组件类型菜单始终展示；各来源（番茄钟/屏幕时长/健身/键鼠活跃等）
    /// 固定写入对应类型，类型名即服务端表中的 type 值。
    /// </summary>
    public static class DailyActivityTypes
    {
        public const string Pomodoro = "番茄";
        public const string ScreenTime = "屏幕时长";
        public const string Fitness = "健身记录";
        public const string InputActive = "鼠标键盘活跃";

        /// <summary>全部内置类型（菜单展示顺序）</summary>
        public static readonly string[] BuiltIn = { Pomodoro, ScreenTime, Fitness, InputActive };
    }

    /// <summary>
    /// 每日活跃数据服务：对接后端独立数据表 sidedesk_daily_activity（localhost:3007，与数据看板变量同一鉴权与响应格式）。
    /// 每行 = 作用域(userId/corpId) + 类型 + 日期 + 当天数值。
    /// 作用域：corpId 有值=团队共享；为空=个人。
    /// 失败时返回 null/false 由 UI 优雅降级（不阻断用户操作）。
    /// 内置内存缓存（key=作用域|类型，TTL 60s）供组件渲染同步读取。
    /// </summary>
    public sealed class DailyActivityService
    {
        private static readonly Lazy<DailyActivityService> _lazy = new(() => new DailyActivityService());
        public static DailyActivityService Instance => _lazy.Value;

        private const string BaseUrl = "http://localhost:3007";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>缓存条目：某作用域+类型的已拉取每日数值</summary>
        private sealed class CacheEntry
        {
            public DateTime FetchedAt;
            public string Start = "", End = "";
            public readonly Dictionary<string, double> Values = new();
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

        private DailyActivityService() { }

        private static string CacheKey(string? corpId, string type) => $"{corpId ?? ""}|{type}";

        private static string GetToken()
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            return app.AppSettings.AccountToken ?? "";
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? jsonBody = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + GetToken());
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return req;
        }

        /// <summary>解析通用响应 { code: 200, data: ... }，返回 data 的 JToken；失败返回 null</summary>
        private static async Task<JToken?> ParseResponseTokenAsync(HttpResponseMessage resp, string tag)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[DailyActivity] {tag} {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode) return null;

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            if (bizCode != 200) return null;
            return root["data"];
        }

        // ==================== API ====================

        /// <summary>
        /// GET /api/sidebar/daily-activity/types — 列出作用域内已有的全部类型。
        /// corpId 为空=个人。失败返回 null。
        /// </summary>
        public async Task<List<string>?> ListTypesAsync(string? corpId)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return null;
                var url = $"{BaseUrl}/api/sidebar/daily-activity/types";
                if (!string.IsNullOrEmpty(corpId)) url += $"?corpId={Uri.EscapeDataString(corpId)}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp, "types").ConfigureAwait(false);
                if (data is not JArray arr) return null;
                var list = new List<string>();
                foreach (var t in arr)
                {
                    string? s = t.Value<string>();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                return list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DailyActivity] 获取类型列表失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// GET /api/sidebar/daily-activity/list — 按类型取日期范围内的每日数值。
        /// 失败返回 null。
        /// </summary>
        public async Task<Dictionary<string, double>?> GetValuesAsync(string? corpId, string type, DateTime start, DateTime end)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return null;
                var url = $"{BaseUrl}/api/sidebar/daily-activity/list?type={Uri.EscapeDataString(type)}"
                    + $"&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}";
                if (!string.IsNullOrEmpty(corpId)) url += $"&corpId={Uri.EscapeDataString(corpId)}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp, "list").ConfigureAwait(false);
                if (data is not JArray arr) return null;
                var dict = new Dictionary<string, double>();
                foreach (var tok in arr)
                {
                    // activityDate 为 DATE 列，序列化可能是 ISO 时间串，取前 10 位即日期
                    string date = (tok["date"]?.Value<string>() ?? "")[..Math.Min(10, (tok["date"]?.Value<string>() ?? "").Length)];
                    if (date.Length == 10) dict[date] = tok["value"]?.Value<double>() ?? 0;
                }
                return dict;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DailyActivity] 获取数值失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// POST /api/sidebar/daily-activity/add — 累加某天数值（不存在则自动创建）。
        /// 成功后同步更新本地缓存。失败返回 false。
        /// </summary>
        public async Task<bool> AddAsync(string? corpId, string type, DateTime date, double delta)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return false;
                var body = JsonConvert.SerializeObject(new
                {
                    corpId,
                    type,
                    date = date.ToString("yyyy-MM-dd"),
                    delta
                });
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/daily-activity/add", body);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp, "add").ConfigureAwait(false);
                if (data == null) return false;

                // 同步更新缓存当日值
                if (_cache.TryGetValue(CacheKey(corpId, type), out var entry))
                {
                    lock (entry.Values)
                    {
                        string key = date.ToString("yyyy-MM-dd");
                        entry.Values.TryGetValue(key, out double cur);
                        entry.Values[key] = cur + delta;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DailyActivity] 累加失败: {ex.Message}");
                return false;
            }
        }

        // ==================== 缓存读取（供组件渲染同步调用）====================

        /// <summary>同步读取缓存中某天的数值；未缓存返回 null（不发起请求）</summary>
        public double? TryGetCachedValue(string? corpId, string type, DateTime date)
        {
            if (!_cache.TryGetValue(CacheKey(corpId, type), out var entry)) return null;
            lock (entry.Values)
            {
                return entry.Values.TryGetValue(date.ToString("yyyy-MM-dd"), out double v) ? v : 0;
            }
        }

        /// <summary>
        /// 确保某作用域+类型的日期范围数据已缓存（TTL 内不重复拉取）。
        /// 返回 true 表示本次实际发起了拉取（调用方可据此重绘）。
        /// </summary>
        public async Task<bool> EnsureLoadedAsync(string? corpId, string type, DateTime start, DateTime end)
        {
            string key = CacheKey(corpId, type);
            string s = start.ToString("yyyy-MM-dd"), e = end.ToString("yyyy-MM-dd");
            if (_cache.TryGetValue(key, out var entry)
                && DateTime.Now - entry.FetchedAt < CacheTtl
                && string.Compare(entry.Start, s, StringComparison.Ordinal) <= 0
                && string.Compare(entry.End, e, StringComparison.Ordinal) >= 0)
                return false;

            var values = await GetValuesAsync(corpId, type, start, end).ConfigureAwait(false);
            if (values == null) return false; // 后端不可达/未登录：不缓存，下次渲染再试

            var newEntry = new CacheEntry { FetchedAt = DateTime.Now, Start = s, End = e };
            foreach (var kv in values) newEntry.Values[kv.Key] = kv.Value;
            _cache[key] = newEntry;
            return true;
        }

        /// <summary>清除某作用域+类型的缓存（刷新菜单用）</summary>
        public void Invalidate(string? corpId, string type) => _cache.TryRemove(CacheKey(corpId, type), out _);
    }
}
