using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SideDesk.Windows;

namespace SideDesk.Services
{
    /// <summary>
    /// 屏幕时长按「每小时 × 每应用」落库：
    /// 把 WindowLogger 产生的每日窗口日志聚合成 local_screen_time_hourly 记录
    /// （date + hour + process + seconds），支撑小时 / 日 / 月 / 年四种粒度统计。
    ///
    /// 同步策略：
    ///   - 首次同步时按日志文件增量补录历史日期（local_app_state 记录最后补录日期）；
    ///   - 今天的数据每次同步都整日重算覆盖（日志持续增长，重算保证准确）；
    ///   - WindowUsageService 运行时每 5 分钟自动重算一次今天。
    /// </summary>
    public sealed class ScreenTimeHourlyService
    {
        private static readonly Lazy<ScreenTimeHourlyService> _instance = new(() => new ScreenTimeHourlyService());
        public static ScreenTimeHourlyService Instance => _instance.Value;

        private const string MarkerKey = "screen_time_last_past_date";
        private const string CloudMarkerKey = "screen_time_cloud_synced_date";
        private const string BaseUrl = Constants.URLs.ServerBaseUrl;
        private static readonly Regex LogFilePattern = new(@"^windowlog_(\d{8})\.txt$", RegexOptions.IgnoreCase);
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly object _syncLock = new();
        private System.Timers.Timer? _timer;
        private DateTime _lastTodayPush = DateTime.MinValue;

        private ScreenTimeHourlyService() { }

        /// <summary>启动后台同步：先全量补齐历史（后台线程），之后每 5 分钟重算今天</summary>
        public void Start()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { SyncAll(); } catch { }
            });
            lock (_syncLock)
            {
                _timer?.Stop();
                _timer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds) { AutoReset = true };
                _timer.Elapsed += (s, e) =>
                {
                    try { SyncAll(); } catch { }
                };
                _timer.Start();
            }
        }

        public void Stop()
        {
            lock (_syncLock)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
            }
        }

        // ==================== 同步 ====================

        /// <summary>补齐历史日期 + 重算今天（幂等，可重复调用）；已登录时同步推送云端</summary>
        public void SyncAll()
        {
            SyncPastDates();
            SyncDate(DateTime.Today);
            // 今天的数据 5 分钟内不重复推送（定时器和页签切换都会触发 SyncAll）
            if (DateTime.Now - _lastTodayPush >= TimeSpan.FromMinutes(5))
            {
                if (PushDateToCloudAsync(DateTime.Today).GetAwaiter().GetResult())
                    _lastTodayPush = DateTime.Now;
            }
        }

        /// <summary>增量补录：窗口日志中存在但尚未入库（本地/云端）的历史日期</summary>
        private void SyncPastDates()
        {
            string logFolder = GetLogFolder();
            if (!Directory.Exists(logFolder)) return;

            string marker = GetMarker();
            string cloudMarker = GetMarker(CloudMarkerKey);
            bool loggedIn = !string.IsNullOrEmpty(GetToken());

            var dates = new List<DateTime>();
            foreach (var file in Directory.GetFiles(logFolder, "windowlog_*.txt"))
            {
                var m = LogFilePattern.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    continue;
                if (d.Date >= DateTime.Today) continue; // 今天单独重算
                string dateStr = d.ToString("yyyy-MM-dd");
                bool localDone = string.CompareOrdinal(dateStr, marker) <= 0;
                bool cloudDone = !string.IsNullOrEmpty(cloudMarker) && string.CompareOrdinal(dateStr, cloudMarker) <= 0;
                if (localDone && (cloudDone || !loggedIn)) continue; // 本地与云端都已完成（未登录时只看本地）
                dates.Add(d.Date);
            }
            dates.Sort();
            foreach (var d in dates)
            {
                string dateStr = d.ToString("yyyy-MM-dd");
                if (string.CompareOrdinal(dateStr, marker) > 0)
                {
                    SyncDate(d);
                    SetMarker(dateStr);
                }
                if (loggedIn && (string.IsNullOrEmpty(cloudMarker) || string.CompareOrdinal(dateStr, cloudMarker) > 0))
                {
                    // 云端补录：成功才推进云端标记，失败下次重试
                    if (PushDateToCloudAsync(d).GetAwaiter().GetResult())
                    {
                        SetMarker(dateStr, CloudMarkerKey);
                        cloudMarker = dateStr;
                    }
                }
            }
        }

        /// <summary>解析某日窗口日志，按（小时 × 应用）聚合后整日覆盖写入</summary>
        public void SyncDate(DateTime date)
        {
            var records = WindowUsageStats.ParseLog(date);
            var agg = AggregateHourly(records);
            string dateKey = date.ToString("yyyy-MM-dd");

            using var conn = LocalDbService.Open();
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM local_screen_time_hourly WHERE date=$d";
                del.Parameters.AddWithValue("$d", dateKey);
                del.ExecuteNonQuery();
            }
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"
INSERT INTO local_screen_time_hourly (date, hour, process, seconds)
VALUES ($d, $h, $p, $s)
ON CONFLICT(date, hour, process) DO UPDATE SET seconds=excluded.seconds;";
                ins.Parameters.Add("$d", SqliteType.Text);
                ins.Parameters.Add("$h", SqliteType.Integer);
                ins.Parameters.Add("$p", SqliteType.Text);
                ins.Parameters.Add("$s", SqliteType.Integer);
                foreach (var kv in agg)
                {
                    ins.Parameters["$d"].Value = dateKey;
                    ins.Parameters["$h"].Value = kv.Key.Hour;
                    ins.Parameters["$p"].Value = kv.Key.Process;
                    ins.Parameters["$s"].Value = kv.Value;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }

        /// <summary>把窗口记录按时长归属到（小时 × 应用），跨小时边界的记录拆分到两个小时</summary>
        private static Dictionary<(int Hour, string Process), long> AggregateHourly(List<WindowRecord> records)
        {
            var agg = new Dictionary<(int, string), long>();
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r.ProcessName)) continue;
                double totalSec = r.Duration.TotalSeconds;
                if (totalSec <= 0) continue;

                var start = r.Timestamp;
                var end = r.Timestamp.AddSeconds(totalSec);
                var cur = start;
                while (cur < end)
                {
                    var hourEnd = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
                    var segEnd = hourEnd < end ? hourEnd : end;
                    long secs = (long)Math.Round((segEnd - cur).TotalSeconds);
                    if (secs > 0)
                    {
                        var key = (cur.Hour, r.ProcessName);
                        agg.TryGetValue(key, out long old);
                        agg[key] = old + secs;
                    }
                    cur = segEnd;
                }
            }
            return agg;
        }

        private static string GetLogFolder() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "Logs");

        private static string GetMarker(string key = MarkerKey)
        {
            try
            {
                using var conn = LocalDbService.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM local_app_state WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", key);
                return cmd.ExecuteScalar() as string ?? "";
            }
            catch { return ""; }
        }

        private static void SetMarker(string value, string key = MarkerKey)
        {
            try
            {
                using var conn = LocalDbService.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO local_app_state (key, value) VALUES ($k, $v)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // ==================== 查询 ====================

        /// <summary>单条统计行：period 为时间桶标识（小时 "08:00" / 日期 / 月份 / 年份）</summary>
        public class StatRow
        {
            public string Period = "";
            public string Process = "";
            public long Seconds;
        }

        /// <summary>
        /// 按粒度查询时间范围内的（时间桶 × 应用）秒数，按时间桶倒序。
        /// granularity：hour（仅 dateFrom 当天）/ day / month / year。
        /// dateFrom 为 null 表示不限起点（查全部历史）。
        /// </summary>
        public List<StatRow> Query(string granularity, DateTime? dateFrom, DateTime? dateTo = null)
        {
            try { SyncAll(); } catch { }

            string periodExpr = granularity switch
            {
                "hour" => "printf('%02d:00', hour)",
                "month" => "substr(date, 1, 7)",
                "year" => "substr(date, 1, 4)",
                _ => "date"
            };

            var rows = new List<StatRow>();
            using var conn = LocalDbService.Open();
            using var cmd = conn.CreateCommand();
            string sql = $@"
SELECT {periodExpr} AS period, process, SUM(seconds) AS s
FROM local_screen_time_hourly";
            var where = new List<string>();
            if (granularity == "hour" && dateFrom != null)
                where.Add("date=$from");
            else if (dateFrom != null)
                where.Add("date>=$from");
            if (dateTo != null)
                where.Add("date<=$to");
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            sql += " GROUP BY period, process ORDER BY period DESC, s DESC";
            cmd.CommandText = sql;
            if (dateFrom != null) cmd.Parameters.AddWithValue("$from", dateFrom.Value.ToString("yyyy-MM-dd"));
            if (dateTo != null) cmd.Parameters.AddWithValue("$to", dateTo.Value.ToString("yyyy-MM-dd"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new StatRow
                {
                    Period = reader.GetString(0),
                    Process = reader.GetString(1),
                    Seconds = reader.GetInt64(2)
                });
            }
            return rows;
        }

        // ==================== 云端同步（后端 sidedesk_screen_time_hourly）====================

        private static string GetToken()
        {
            try
            {
                var app = (SideDesk.App)System.Windows.Application.Current;
                return app?.AppSettings?.AccountToken ?? "";
            }
            catch { return ""; }
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
            System.Diagnostics.Debug.WriteLine($"[ScreenTime] {tag} {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode) return null;
            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            if (bizCode != 200) return null;
            return root["data"];
        }

        /// <summary>读取本地某日全部小时记录（推送用）</summary>
        private static List<(int Hour, string Process, long Seconds)> ReadLocalDay(DateTime date)
        {
            var rows = new List<(int, string, long)>();
            using var conn = LocalDbService.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hour, process, seconds FROM local_screen_time_hourly WHERE date=$d";
            cmd.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
            return rows;
        }

        /// <summary>
        /// 把某日的小时记录推送到云端（POST /sidebar/screen-time/sync，按唯一键覆盖）。
        /// 未登录/后端不可达返回 false（不阻断本地流程）。
        /// </summary>
        public async Task<bool> PushDateToCloudAsync(DateTime date)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return false;
                var rows = ReadLocalDay(date);
                if (rows.Count == 0) return true; // 空日视为成功，避免反复重试

                string dateStr = date.ToString("yyyy-MM-dd");
                // 后端单次上限 5000 条，分批推送
                for (int i = 0; i < rows.Count; i += 2000)
                {
                    var chunk = rows.Skip(i).Take(2000)
                        .Select(r => new { date = dateStr, hour = r.Hour, process = r.Process, seconds = r.Seconds });
                    var body = JsonConvert.SerializeObject(new { deviceId = DeviceIdService.DeviceId, records = chunk });
                    using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/screen-time/sync", body);
                    using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    if (await ParseResponseTokenAsync(resp, "sync") == null) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenTime] 推送云端失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// GET /api/sidebar/screen-time/stats — 云端按粒度聚合查询，排除本机设备
        /// （本机数据已在本地统计，避免重复）。未登录/失败返回 null。
        /// </summary>
        public async Task<List<StatRow>?> GetCloudStatsAsync(string granularity, DateTime? dateFrom, DateTime? dateTo = null)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return null;
                var url = $"{BaseUrl}/api/sidebar/screen-time/stats?granularity={granularity}"
                    + $"&excludeDevice={Uri.EscapeDataString(DeviceIdService.DeviceId)}";
                if (dateFrom != null) url += $"&start={dateFrom.Value:yyyy-MM-dd}";
                if (dateTo != null) url += $"&end={dateTo.Value:yyyy-MM-dd}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp, "stats").ConfigureAwait(false);
                if (data is not JArray arr) return null;
                var list = new List<StatRow>();
                foreach (var tok in arr)
                {
                    list.Add(new StatRow
                    {
                        Period = tok["period"]?.Value<string>() ?? "",
                        Process = tok["process"]?.Value<string>() ?? "",
                        Seconds = tok["seconds"]?.Value<long>() ?? 0
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenTime] 云端查询失败: {ex.Message}");
                return null;
            }
        }
    }
}
