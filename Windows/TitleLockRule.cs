using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SideDesk.Windows
{
    /// <summary>
    /// 标题匹配模式（当 MatchPattern 非空时生效）。
    /// </summary>
    public enum TitleMatchMode
    {
        Equals = 0,
        Contains = 1
    }

    /// <summary>
    /// 单条标题锁定规则：进程名 + 锁定前标题 + 进程目录 + 锁定后文字。
    /// 可选：MatchPattern + MatchMode 用于更灵活的启动自动恢复匹配。
    /// </summary>
    public class TitleLockRule
    {
        public string ProcessName { get; set; }
        public string OriginalTitle { get; set; }
        public string ExecutableDir { get; set; }
        public string LockText { get; set; }
        // 可选的模糊匹配字符串。非空时优先用于 RestoreLocksFromRules 和主动匹配。
        public string MatchPattern { get; set; }
        public TitleMatchMode MatchMode { get; set; } = TitleMatchMode.Contains;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// 标题锁定规则的本地 JSON 管理器（位于 %LocalAppData%\SideDesk\title-lock-rules.json）。
    /// </summary>
    public static class TitleLockRuleManager
    {
        private static readonly string _filePath;
        private static List<TitleLockRule> _rules = new List<TitleLockRule>();
        private static readonly object _lock = new object();

        static TitleLockRuleManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "title-lock-rules.json");
            Load();
        }

        public static List<TitleLockRule> GetAll()
        {
            lock (_lock)
            {
                return _rules.Select(Clone).ToList();
            }
        }

        /// <summary>
        /// 新增或更新一条规则。唯一键：
        /// - 有 MatchPattern：ProcessName + MatchPattern + MatchMode
        /// - 无 MatchPattern：ProcessName + OriginalTitle + ExecutableDir
        /// </summary>
        public static void Upsert(TitleLockRule rule)
        {
            if (rule == null || string.IsNullOrEmpty(rule.ProcessName)) return;

            lock (_lock)
            {
                var existing = _rules.FirstOrDefault(r => IsSameKey(r, rule));
                if (existing != null)
                {
                    existing.LockText = rule.LockText;
                    existing.ExecutableDir = rule.ExecutableDir;
                    existing.OriginalTitle = rule.OriginalTitle;
                    existing.MatchPattern = rule.MatchPattern;
                    existing.MatchMode = rule.MatchMode;
                    existing.UpdatedAt = DateTime.Now;
                }
                else
                {
                    rule.CreatedAt = DateTime.Now;
                    rule.UpdatedAt = rule.CreatedAt;
                    _rules.Add(rule);
                }
                Save();
            }
        }

        /// <summary>
        /// 删除一条完全等于该 key 的规则。
        /// </summary>
        public static void RemoveExact(TitleLockRule rule)
        {
            if (rule == null) return;
            lock (_lock)
            {
                int n = _rules.RemoveAll(r => IsSameKey(r, rule));
                if (n > 0) Save();
            }
        }

        /// <summary>
        /// 按进程名 + 锁定前标题删除（忽略目录）。常用于解锁时清理。
        /// </summary>
        public static void Remove(string processName, string originalTitle)
        {
            if (string.IsNullOrEmpty(processName)) return;
            lock (_lock)
            {
                int n = _rules.RemoveAll(r =>
                    string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.OriginalTitle ?? string.Empty, originalTitle ?? string.Empty, StringComparison.Ordinal));
                if (n > 0) Save();
            }
        }

        /// <summary>
        /// 在给定窗口信息中找到第一条匹配的规则；未匹配返回 null。
        /// </summary>
        public static TitleLockRule FindMatch(string processName, string currentTitle, string executableDir)
        {
            if (string.IsNullOrEmpty(processName)) return null;
            string title = currentTitle ?? string.Empty;
            lock (_lock)
            {
                // 优先 MatchPattern 规则
                var fuzzy = _rules.FirstOrDefault(r =>
                    !string.IsNullOrEmpty(r.MatchPattern)
                    && string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                    && IsTitleMatch(title, r.MatchPattern, r.MatchMode));
                if (fuzzy != null) return fuzzy;

                // 回退到精确匹配
                return _rules.FirstOrDefault(r =>
                    string.IsNullOrEmpty(r.MatchPattern)
                    && string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.OriginalTitle ?? string.Empty, title, StringComparison.Ordinal)
                    && (string.IsNullOrEmpty(r.ExecutableDir)
                        || string.IsNullOrEmpty(executableDir)
                        || string.Equals(r.ExecutableDir, executableDir, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private static bool IsTitleMatch(string title, string pattern, TitleMatchMode mode)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            switch (mode)
            {
                case TitleMatchMode.Equals:
                    return string.Equals(title, pattern, StringComparison.Ordinal);
                case TitleMatchMode.Contains:
                default:
                    return title.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static bool IsSameKey(TitleLockRule a, TitleLockRule b)
        {
            bool aHasPattern = !string.IsNullOrEmpty(a.MatchPattern);
            bool bHasPattern = !string.IsNullOrEmpty(b.MatchPattern);
            if (aHasPattern || bHasPattern)
            {
                return string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.MatchPattern ?? string.Empty, b.MatchPattern ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && a.MatchMode == b.MatchMode;
            }
            return string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.OriginalTitle ?? string.Empty, b.OriginalTitle ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.ExecutableDir ?? string.Empty, b.ExecutableDir ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static TitleLockRule Clone(TitleLockRule r)
        {
            return new TitleLockRule
            {
                ProcessName = r.ProcessName,
                OriginalTitle = r.OriginalTitle,
                ExecutableDir = r.ExecutableDir,
                LockText = r.LockText,
                MatchPattern = r.MatchPattern,
                MatchMode = r.MatchMode,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt
            };
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _rules = new List<TitleLockRule>();
                    return;
                }
                string json = File.ReadAllText(_filePath);
                _rules = JsonConvert.DeserializeObject<List<TitleLockRule>>(json) ?? new List<TitleLockRule>();
            }
            catch
            {
                _rules = new List<TitleLockRule>();
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_rules, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TitleLockRuleManager] Save failed: {ex.Message}");
            }
        }
    }
}
