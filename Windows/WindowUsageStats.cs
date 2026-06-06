using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SideDesk.Windows
{
    /// <summary>
    /// 单条窗口日志记录
    /// </summary>
    public class WindowRecord
    {
        public DateTime Timestamp { get; set; }
        public string ClassName { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public string ExePath { get; set; }
        public string Url { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// 按进程名汇总的使用时长
    /// </summary>
    public class AppUsageItem
    {
        public string ProcessName { get; set; }
        public string ExePath { get; set; }
        public string Category { get; set; }
        public TimeSpan TotalDuration { get; set; }
        /// <summary>子项（浏览器按域名、微信按标题等），无子项时为 null</summary>
        public List<AppUsageSubItem> SubItems { get; set; }
    }

    /// <summary>
    /// 应用子项（域名/标题维度的使用时长）
    /// </summary>
    public class AppUsageSubItem
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }

    /// <summary>
    /// 窗口使用时长统计：解析 WindowLogger 产生的日志，计算各程序使用时长。
    /// </summary>
    public class WindowUsageStats
    {
        private static readonly string LogFolder;

        static WindowUsageStats()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LogFolder = Path.Combine(appData, "SideDesk", "WindowLogs");
        }

        /// <summary>
        /// 解析今天的日志文件，返回所有记录（已计算 Duration）
        /// </summary>
        public static List<WindowRecord> ParseTodayLog()
        {
            var records = new List<WindowRecord>();
            string filePath = Path.Combine(LogFolder, $"windowlog_{DateTime.Now:yyyyMMdd}.txt");
            if (!File.Exists(filePath))
                return records;

            string[] lines;
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    lines = sr.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch
            {
                return records;
            }

            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 4) continue;

                DateTime ts;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                    continue;

                records.Add(new WindowRecord
                {
                    Timestamp = ts,
                    ClassName = parts.Length > 1 ? parts[1] : "",
                    Title = parts.Length > 2 ? parts[2] : "",
                    ProcessName = parts.Length > 3 ? parts[3] : "",
                    ExePath = parts.Length > 4 ? parts[4] : "",
                    Url = parts.Length > 5 ? parts[5] : ""
                });
            }

            // 计算每条记录的持续时长 = 下一条时间戳 - 当前时间戳
            for (int i = 0; i < records.Count - 1; i++)
            {
                var diff = records[i + 1].Timestamp - records[i].Timestamp;
                if (diff.TotalMinutes > 5)
                    diff = TimeSpan.FromMinutes(5);
                records[i].Duration = diff;
            }

            // 最后一条记录：持续到当前时间，上限5分钟
            if (records.Count > 0)
            {
                var last = records[records.Count - 1];
                var diff = DateTime.Now - last.Timestamp;
                if (diff.TotalMinutes > 5)
                    diff = TimeSpan.FromMinutes(5);
                last.Duration = diff;
            }

            return records;
        }

        /// <summary>
        /// 获取各应用的使用时长汇总（按总时长降序排列）
        /// </summary>
        public static List<AppUsageItem> GetAppUsageSummary()
        {
            var records = ParseTodayLog();
            var grouped = records
                .Where(r => !string.IsNullOrEmpty(r.ProcessName))
                .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var item = new AppUsageItem
                    {
                        ProcessName = g.Key,
                        ExePath = g.FirstOrDefault(x => !string.IsNullOrEmpty(x.ExePath))?.ExePath ?? "",
                        Category = "", // TODO: integrate later - ProgramCategoryManager
                        TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration.Ticks))
                    };

                    item.SubItems = BuildSubItems(g.Key, g.ToList());

                    return item;
                })
                .OrderByDescending(x => x.TotalDuration)
                .ToList();
            return grouped;
        }

        /// <summary>为特定进程构建子项（域名/标题维度）</summary>
        private static List<AppUsageSubItem> BuildSubItems(string processName, List<WindowRecord> records)
        {
            string lower = processName.ToLowerInvariant();

            // 浏览器：按域名分组
            if (lower == "msedge" || lower == "chrome" || lower == "firefox" || lower == "iexplore" || lower == "msedge.exe" || lower == "chrome.exe")
            {
                var domainGroups = records
                    .Where(r => !string.IsNullOrEmpty(r.Url))
                    .Select(r => new { Domain = ExtractDomain(r.Url), r.Duration })
                    .Where(x => !string.IsNullOrEmpty(x.Domain))
                    .GroupBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new AppUsageSubItem
                    {
                        Name = g.Key,
                        Category = "", // TODO: integrate later - ProgramCategoryManager
                        TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration.Ticks))
                    })
                    .OrderByDescending(x => x.TotalDuration)
                    .ToList();

                if (domainGroups.Count > 0)
                    return domainGroups;
            }

            // 微信：按窗口标题分组
            if (lower == "weixin" || lower == "wechat")
            {
                var titleGroups = records
                    .Select(r => new { Title = r.Title, r.Duration })
                    .Where(x => !string.IsNullOrEmpty(x.Title) && x.Title != processName)
                    .GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new AppUsageSubItem
                    {
                        Name = g.Key,
                        Category = "", // TODO: integrate later - ProgramCategoryManager
                        TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration.Ticks))
                    })
                    .OrderByDescending(x => x.TotalDuration)
                    .ToList();

                if (titleGroups.Count > 0)
                    return titleGroups;
            }

            return null;
        }

        /// <summary>从 URL 提取域名（简化版）</summary>
        private static string ExtractDomain(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            try
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    int start = url.IndexOf("://") + 3;
                    int end = url.IndexOf('/', start);
                    if (end < 0) end = url.IndexOf('?', start);
                    if (end < 0) end = url.Length;
                    string host = url.Substring(start, end - start);
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                        host = host.Substring(4);
                    return host;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// 获取最近 N 条窗口切换记录（最新的在前）
        /// </summary>
        public static List<WindowRecord> GetRecentRecords(int count = 30)
        {
            var records = ParseTodayLog();
            return records.AsEnumerable().Reverse().Take(count).ToList();
        }

        /// <summary>
        /// 获取今天的总活跃时长
        /// </summary>
        public static TimeSpan GetTotalActiveTime()
        {
            var records = ParseTodayLog();
            return TimeSpan.FromTicks(records.Sum(r => r.Duration.Ticks));
        }

        /// <summary>
        /// 格式化 TimeSpan 为可读字符串，如 "2h 15m" 或 "3m 20s"
        /// </summary>
        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}
