using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class QuickItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>"url" / "program"</summary>
        public string Type { get; set; } = "url";
        /// <summary>网址或程序路径</summary>
        public string Target { get; set; }
        /// <summary>URL 类型：是否 App 模式打开</summary>
        public bool UseAppMode { get; set; } = true;
        public string BackgroundColor { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>是否窗口置顶</summary>
        public bool AlwaysOnTop { get; set; } = false;

        /// <summary>是否允许该窗口在系统任务栏上显示（并可 Alt+Tab 切换）</summary>
        public bool ShowInTaskbar { get; set; } = false;

        /// <summary>是否保存为「长期窗口」：启动时根据匹配规则查找现有窗口并接管</summary>
        public bool IsLongTermWindow { get; set; } = false;
        /// <summary>长期窗口匹配：标题文本</summary>
        public string MatchTitle { get; set; }
        /// <summary>长期窗口匹配：标题模式 exact / contains</summary>
        public string MatchTitleMode { get; set; } = "contains";
        /// <summary>长期窗口匹配：窗口类名</summary>
        public string MatchClass { get; set; }
        /// <summary>长期窗口匹配：exe 文件名（不含路径）</summary>
        public string MatchExe { get; set; }

        /// <summary>项来源标记："window" = 通过「添加窗口」接管创建（未勾选长期窗口时，退出会被清理）</summary>
        public string Source { get; set; }

        /// <summary>URL 类型打开时使用的浏览器 exe 配置 ID（空=使用系统默认 msedge）</summary>
        public string BrowserExeId { get; set; }
        /// <summary>URL 类型打开时使用的 user-data-dir 配置 ID（空=不指定）</summary>
        public string BrowserProfileId { get; set; }

        /// <summary>程序启动时的命令行参数</summary>
        public string LaunchArgs { get; set; }

        /// <summary>URL 类型打开时使用的 ProgramRegistry 浏览器 ID（空=默认）</summary>
        public string BrowserId { get; set; }
        /// <summary>URL 类型打开时使用的 ProgramRegistry Profile ID（空=不指定）</summary>
        public string ProfileId { get; set; }

        [JsonIgnore]
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

        public QuickItem()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            BackgroundColor = "#3498DB";
        }
    }

    public class QuickGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>布局列数 (1~4)</summary>
        public int Columns { get; set; } = 1;
        public bool IsExpanded { get; set; } = true;
        public int Order { get; set; }
        /// <summary>是否隐藏该快捷组外框（背景/边框/标题栏/内边距），仅用于视觉裸露显示</summary>
        public bool HideFrame { get; set; } = false;
        public List<QuickItem> Items { get; set; }
        /// <summary>分组内子模块列表（用于添加便签、剪贴板等模块）</summary>
        public List<PanelItem>? ChildPanels { get; set; }

        public QuickGroup()
        {
            Id = Guid.NewGuid().ToString("N");
            Items = new List<QuickItem>();
        }
    }

    // ===== 浏览器 Profile 管理 =====

    public class BrowserExeEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>显示名，如 "Chrome", "Edge"</summary>
        public string Name { get; set; }
        /// <summary>浏览器 exe 完整路径</summary>
        public string ExePath { get; set; }
    }

    public class BrowserProfileEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>显示名，如 "工作", "个人"</summary>
        public string Name { get; set; }
        /// <summary>user-data 目录完整路径</summary>
        public string UserDataDir { get; set; }
    }

    public class BrowserProfileData
    {
        public List<BrowserExeEntry> Exes { get; set; } = new List<BrowserExeEntry>();
        public List<BrowserProfileEntry> Profiles { get; set; } = new List<BrowserProfileEntry>();
    }

    public static class BrowserProfileManager
    {
        private static readonly string _filePath;
        private static BrowserProfileData _data;
        private static readonly object _lock = new object();

        static BrowserProfileManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "browser_profiles.json");
            Load();
        }

        public static List<BrowserExeEntry> GetAllExes()
        {
            lock (_lock) return _data.Exes.ToList();
        }

        public static List<BrowserProfileEntry> GetAllProfiles()
        {
            lock (_lock) return _data.Profiles.ToList();
        }

        public static BrowserExeEntry GetExeById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _data.Exes.FirstOrDefault(e => e.Id == id);
        }

        public static BrowserProfileEntry GetProfileById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _data.Profiles.FirstOrDefault(p => p.Id == id);
        }

        public static void AddExe(BrowserExeEntry entry)
        {
            lock (_lock) { _data.Exes.Add(entry); }
        }

        public static void RemoveExe(string id)
        {
            lock (_lock) { _data.Exes.RemoveAll(e => e.Id == id); }
        }

        public static void AddProfile(BrowserProfileEntry entry)
        {
            lock (_lock) { _data.Profiles.Add(entry); }
        }

        public static void RemoveProfile(string id)
        {
            lock (_lock) { _data.Profiles.RemoveAll(p => p.Id == id); }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                    File.WriteAllText(_filePath, json);
                }
                catch { }
            }
        }

        /// <summary>计算目录磁盘占用（字节）</summary>
        public static long GetDiskSize(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            try
            {
                return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { return 0; }
        }

        /// <summary>删除整个 user-data 目录（释放磁盘空间）</summary>
        public static bool CleanupProfile(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                Directory.Delete(dir, true);
                return true;
            }
            catch { return false; }
        }

        /// <summary>格式化字节数为易读字符串</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _data = JsonConvert.DeserializeObject<BrowserProfileData>(json) ?? new BrowserProfileData();
                }
                else
                {
                    _data = new BrowserProfileData();
                }
            }
            catch { _data = new BrowserProfileData(); }
        }
    }

    public static class QuickGroupManager
    {
        /// <summary>默认分组的固定 ID。</summary>
        public const string DEFAULT_GROUP_ID = "__default__";

        private static readonly string _filePath;
        private static List<QuickGroup> _groups;
        private static readonly object _lock = new object();

        static QuickGroupManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "quickgroups.json");
            Load();
        }

        public static List<QuickGroup> GetAll()
        {
            lock (_lock)
                return _groups.OrderBy(g => g.Order).ToList();
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(_groups, Formatting.Indented);
                    File.WriteAllText(_filePath, json);
                }
                catch { }
            }
        }

        public static void AddGroup(QuickGroup group)
        {
            lock (_lock)
            {
                if (group.Order == 0)
                    group.Order = _groups.Count > 0 ? _groups.Max(g => g.Order) + 1 : 1;
                _groups.Add(group);
            }
        }

        /// <summary>获取默认分组的 ID；不存在则创建并插入到顶部。</summary>
        public static string GetOrCreateDefaultGroup()
        {
            lock (_lock)
            {
                var existing = _groups.FirstOrDefault(g => g.Id == DEFAULT_GROUP_ID);
                if (existing != null) return existing.Id;

                int minOrder = _groups.Count > 0 ? _groups.Min(g => g.Order) : 1;
                var grp = new QuickGroup
                {
                    Id = DEFAULT_GROUP_ID,
                    Name = "默认",
                    Columns = 2,
                    IsExpanded = true,
                    Order = minOrder - 1
                };
                _groups.Add(grp);
                return grp.Id;
            }
        }

        public static void RemoveGroup(string groupId)
        {
            lock (_lock)
            {
                _groups.RemoveAll(g => g.Id == groupId);
            }
        }

        public static void AddItem(string groupId, QuickItem item)
        {
            lock (_lock)
            {
                var g = _groups.FirstOrDefault(x => x.Id == groupId);
                if (g != null)
                {
                    g.Items.Add(item);
                }
            }
        }

        public static void RemoveItem(string groupId, string itemId)
        {
            lock (_lock)
            {
                var g = _groups.FirstOrDefault(x => x.Id == groupId);
                if (g != null)
                {
                    g.Items.RemoveAll(i => i.Id == itemId);
                }
            }
        }

        public static void UpdateGroup(QuickGroup group)
        {
            lock (_lock)
            {
                int idx = _groups.FindIndex(g => g.Id == group.Id);
                if (idx >= 0)
                {
                    _groups[idx] = group;
                }
            }
        }

        /// <summary>退出时调用：删除「来自添加窗口」且未勾选长期窗口的临时项。返回删除数量。</summary>
        public static int RemoveTransientWindowItems()
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var g in _groups)
                {
                    if (g?.Items == null) continue;
                    count += g.Items.RemoveAll(it => it != null && it.Source == "window" && !it.IsLongTermWindow);
                }
                return count;
            }
        }

        /// <summary>
        /// 首次启动时，将旧 bookmarks/aitools/programs JSON 迁移为新分组。
        /// 仅在 quickgroups.json 不存在时执行。
        /// </summary>
        public static void MigrateFromLegacy()
        {
            if (File.Exists(_filePath)) return;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            int order = 1;

            // 迁移书签
            string bookmarksFile = Path.Combine(folder, "bookmarks.json");
            if (File.Exists(bookmarksFile))
            {
                try
                {
                    var bookmarks = JsonConvert.DeserializeObject<List<Bookmark>>(File.ReadAllText(bookmarksFile));
                    if (bookmarks != null && bookmarks.Count > 0)
                    {
                        var g = new QuickGroup { Name = "书签", Order = order++, Columns = 1 };
                        foreach (var b in bookmarks)
                        {
                            g.Items.Add(new QuickItem
                            {
                                Id = b.Id,
                                Name = b.Name,
                                Type = "url",
                                Target = b.Url,
                                UseAppMode = b.UseAppMode,
                                BackgroundColor = b.BackgroundColor,
                                CreatedAt = b.CreatedAt
                            });
                        }
                        lock (_lock) _groups.Add(g);
                    }
                }
                catch { }
            }

            // 迁移 AI 工具
            string aiFile = Path.Combine(folder, "aitools.json");
            if (File.Exists(aiFile))
            {
                try
                {
                    var tools = JsonConvert.DeserializeObject<List<AITool>>(File.ReadAllText(aiFile));
                    if (tools != null && tools.Count > 0)
                    {
                        var g = new QuickGroup { Name = "AI 工具", Order = order++, Columns = 1 };
                        foreach (var t in tools)
                        {
                            g.Items.Add(new QuickItem
                            {
                                Id = t.Id,
                                Name = t.Name,
                                Type = "url",
                                Target = t.Url,
                                UseAppMode = true,
                                BackgroundColor = t.BackgroundColor,
                                CreatedAt = t.CreatedAt
                            });
                        }
                        lock (_lock) _groups.Add(g);
                    }
                }
                catch { }
            }

            // 迁移程序快捷方式
            string progFile = Path.Combine(folder, "programs.json");
            if (File.Exists(progFile))
            {
                try
                {
                    var progs = JsonConvert.DeserializeObject<List<ProgramShortcut>>(File.ReadAllText(progFile));
                    if (progs != null && progs.Count > 0)
                    {
                        var g = new QuickGroup { Name = "程序", Order = order++, Columns = 1 };
                        foreach (var p in progs)
                        {
                            g.Items.Add(new QuickItem
                            {
                                Id = p.Id,
                                Name = p.Name,
                                Type = "program",
                                Target = string.IsNullOrEmpty(p.Arguments) ? p.Command : p.Command + " " + p.Arguments,
                                UseAppMode = false,
                                BackgroundColor = p.BackgroundColor,
                                CreatedAt = p.CreatedAt
                            });
                        }
                        lock (_lock) _groups.Add(g);
                    }
                }
                catch { }
            }

            Save();
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _groups = JsonConvert.DeserializeObject<List<QuickGroup>>(json) ?? new List<QuickGroup>();
                }
                else
                {
                    _groups = new List<QuickGroup>();
                }
            }
            catch
            {
                _groups = new List<QuickGroup>();
            }
        }
    }
}
