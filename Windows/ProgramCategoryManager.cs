using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SideDesk.Windows
{
    /// <summary>
    /// 程序使用分类：进程名 → 类别映射
    /// </summary>
    public class ProgramCategory
    {
        public string ProcessName { get; set; }
        public string Category { get; set; }
    }

    /// <summary>
    /// 程序分类管理器：维护 进程名→类别 的匹配规则，持久化到 program_categories.json
    /// </summary>
    public static class ProgramCategoryManager
    {
        private static readonly string DataFilePath;
        private static readonly object _lock = new object();
        private static List<ProgramCategory> _rules = new List<ProgramCategory>();
        private static bool _loaded = false;

        /// <summary>
        /// 预设分类列表
        /// </summary>
        public static readonly string[] AvailableCategories = new[]
        {
            "教育", "学习", "工作", "娱乐", "生活", "交友", "忽略"
        };

        /// <summary>
        /// 分类显示名：内部存储值保持中文键不变（已持久化到 program_categories.json），
        /// 仅显示时按 i18n 本地化；未知分类原样返回。
        /// </summary>
        public static string GetDisplayName(string category)
        {
            return category switch
            {
                "教育" => Framework.Lang.T("category.education"),
                "学习" => Framework.Lang.T("category.learning"),
                "工作" => Framework.Lang.T("category.work"),
                "娱乐" => Framework.Lang.T("common.entertainment"),
                "生活" => Framework.Lang.T("common.life"),
                "交友" => Framework.Lang.T("category.social"),
                "忽略" => Framework.Lang.T("category.ignored"),
                "未分类" => Framework.Lang.T("category.uncategorized"),
                _ => category
            };
        }

        static ProgramCategoryManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            DataFilePath = Path.Combine(appData, "SideDesk", "program_categories.json");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }

        /// <summary>
        /// 获取指定进程名的分类（自动持久化）
        /// </summary>
        public static string GetCategory(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return string.Empty;
            lock (_lock)
            {
                EnsureLoaded();
                var match = _rules.FirstOrDefault(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                return match?.Category ?? string.Empty;
            }
        }

        /// <summary>
        /// 设置指定进程名的分类（自动持久化）
        /// </summary>
        public static void SetCategory(string processName, string category)
        {
            if (string.IsNullOrEmpty(processName)) return;
            lock (_lock)
            {
                EnsureLoaded();
                var existing = _rules.FirstOrDefault(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (string.IsNullOrEmpty(category))
                    {
                        _rules.Remove(existing);
                    }
                    else
                    {
                        existing.Category = category;
                    }
                }
                else if (!string.IsNullOrEmpty(category))
                {
                    _rules.Add(new ProgramCategory
                    {
                        ProcessName = processName,
                        Category = category
                    });
                }
                Save();
            }
        }

        /// <summary>
        /// 获取所有已配置的 进程名→类别 规则
        /// </summary>
        public static List<ProgramCategory> GetAll()
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _rules.ToList();
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(DataFilePath))
                {
                    string json = File.ReadAllText(DataFilePath);
                    var list = JsonConvert.DeserializeObject<List<ProgramCategory>>(json);
                    _rules = list ?? new List<ProgramCategory>();
                }
                else
                {
                    _rules = new List<ProgramCategory>();
                }
            }
            catch
            {
                _rules = new List<ProgramCategory>();
            }
        }

        private static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(DataFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(_rules, Formatting.Indented);
                File.WriteAllText(DataFilePath, json);
            }
            catch { }
        }
    }
}
