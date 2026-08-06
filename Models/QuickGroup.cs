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
        /// <summary>Emoji 图标字符（如 、📁）</summary>
        public string? Icon { get; set; }
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

    /// <summary>
    /// 快捷组件「分组」内存门面：与 PanelItem(Type=QuickGroup) 一一对应，所有属性代理到面板。
    /// 历史上快捷分组存于 quickgroups.json；改造后设置存面板 item_data、快捷项存 item_row，
    /// 本类仅为兼容 UI 层（QuickGroupUIService 等）调用而保留，不再参与任何持久化。
    /// </summary>
    public class QuickGroup
    {
        /// <summary>底层面板（所有属性的真实存储处）</summary>
        [JsonIgnore]
        public PanelItem Panel { get; }

        public QuickGroup() : this(new PanelItem { Type = PanelType.QuickGroup }) { }

        public QuickGroup(PanelItem panel)
        {
            Panel = panel;
            panel.Type = PanelType.QuickGroup;
        }

        /// <summary>分组 Id = 面板本地 Id（稳定 GUID，行级同步寻址基础）</summary>
        [JsonIgnore]
        public string Id => Panel.Id;

        public string Name { get => Panel.QuickGroupName; set => Panel.QuickGroupName = value; }
        public int Columns { get => Panel.QuickColumns; set => Panel.QuickColumns = value; }
        public bool IsExpanded { get => Panel.IsExpanded; set => Panel.IsExpanded = value; }
        public bool HideFrame { get => Panel.QuickHideFrame; set => Panel.QuickHideFrame = value; }
        public string DisplayMode { get => Panel.QuickDisplayMode; set => Panel.QuickDisplayMode = value; }
        public int Order { get => Panel.QuickOrder; set => Panel.QuickOrder = value; }
        public string OwnerType { get => Panel.OwnerType; set => Panel.OwnerType = value; }
        public string? TeamId { get => Panel.TeamId; set => Panel.TeamId = value; }
        public string? TeamName { get => Panel.TeamName; set => Panel.TeamName = value; }

        /// <summary>快捷项：直接引用面板列表本体，增删即时生效</summary>
        [JsonIgnore]
        public List<QuickItem> Items => Panel.QuickItems ??= new List<QuickItem>();

        /// <summary>分组内子模块列表（用于添加便签、剪贴板等模块）</summary>
        [JsonIgnore]
        public List<PanelItem>? ChildPanels { get => Panel.ChildPanels; set => Panel.ChildPanels = value; }
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

    /// <summary>
    /// 快捷分组管理门面：全部操作代理到 WindowConfigManager 内的快捷面板（Type=QuickGroup），
    /// 替代历史上的 quickgroups.json 文件存储。保留同名 API 以兼容 UI 层调用点。
    /// </summary>
    public static class QuickGroupManager
    {
        /// <summary>旧版默认分组的固定 ID（仅迁移时用于匹配未绑定面板）。</summary>
        private const string LEGACY_DEFAULT_GROUP_ID = "__default__";

        private static WindowConfigManager? _mgr;

        /// <summary>WindowConfigManager.Load 完成后注入全局配置管理器（仅一次）。</summary>
        public static void Init(WindowConfigManager mgr) => _mgr = mgr;

        private static string SideDeskFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");

        /// <summary>按声明顺序枚举全部快捷面板（含嵌套子面板）。</summary>
        private static List<PanelItem> EnumerateQuickPanels()
        {
            var result = new List<PanelItem>();
            if (_mgr == null) return result;
            void Collect(List<PanelItem> panels)
            {
                foreach (var p in panels)
                {
                    if (p.Type == PanelType.QuickGroup) result.Add(p);
                    if (p.ChildPanels != null) Collect(p.ChildPanels);
                }
            }
            foreach (var win in _mgr.Windows)
                if (win.Panels != null) Collect(win.Panels);
            return result;
        }

        private static PanelItem? FindPanel(string groupId) =>
            EnumerateQuickPanels().FirstOrDefault(p => p.Id == groupId);

        public static List<QuickGroup> GetAll() =>
            EnumerateQuickPanels().Select(p => new QuickGroup(p)).ToList();

        public static void Save() => _mgr?.Save();

        /// <summary>新增快捷面板：优先 targetWindow，缺省落第一个窗口。</summary>
        public static void AddGroup(QuickGroup group, WindowConfig? targetWindow = null)
        {
            if (_mgr == null || _mgr.Windows.Count == 0) return;
            var win = targetWindow ?? _mgr.Windows[0];
            win.Panels ??= new List<PanelItem>();
            win.Panels.Add(group.Panel);
        }

        /// <summary>按 Id 删除快捷面板（递归查找嵌套子面板）。</summary>
        public static void RemoveGroup(string groupId)
        {
            if (_mgr == null) return;
            bool RemoveFrom(List<PanelItem> panels)
            {
                var target = panels.FirstOrDefault(p => p.Id == groupId);
                if (target != null) { panels.Remove(target); return true; }
                foreach (var p in panels)
                    if (p.ChildPanels != null && RemoveFrom(p.ChildPanels)) return true;
                return false;
            }
            foreach (var win in _mgr.Windows)
                if (win.Panels != null && RemoveFrom(win.Panels)) return;
        }

        /// <summary>获取可用快捷面板 Id：已有面板取第一个，否则新建一个。</summary>
        public static string GetOrCreateDefaultGroup()
        {
            var panels = EnumerateQuickPanels();
            if (panels.Count > 0) return panels[0].Id;
            var g = new QuickGroup { Name = "快捷方式", Columns = 2 };
            AddGroup(g);
            return g.Id;
        }

        public static void AddItem(string groupId, QuickItem item)
        {
            var p = FindPanel(groupId);
            if (p != null)
                (p.QuickItems ??= new List<QuickItem>()).Add(item);
        }

        public static void RemoveItem(string groupId, string itemId)
        {
            var p = FindPanel(groupId);
            p?.QuickItems?.RemoveAll(i => i.Id == itemId);
        }

        /// <summary>门面模式下属性直接代理到面板，无需额外回写（保留方法签名兼容调用点）。</summary>
        public static void UpdateGroup(QuickGroup group) { }

        /// <summary>退出时调用：删除「来自添加窗口」且未勾选长期窗口的临时项。返回删除数量。</summary>
        public static int RemoveTransientWindowItems()
        {
            int count = 0;
            foreach (var p in EnumerateQuickPanels())
            {
                if (p.QuickItems == null) continue;
                count += p.QuickItems.RemoveAll(it => it != null && it.Source == "window" && !it.IsLongTermWindow);
            }
            return count;
        }

        /// <summary>旧版 quickgroups.json 分组结构（仅迁移反序列化用）。</summary>
        private class LegacyQuickGroup
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public int Columns { get; set; } = 1;
            public bool IsExpanded { get; set; } = true;
            public int Order { get; set; }
            public bool HideFrame { get; set; }
            public string DisplayMode { get; set; } = "grid";
            public string OwnerType { get; set; } = "personal";
            public string? TeamId { get; set; }
            public string? TeamName { get; set; }
            public List<QuickItem> Items { get; set; } = new List<QuickItem>();
            public List<PanelItem>? ChildPanels { get; set; }
        }

        /// <summary>更早的 bookmarks/aitools/programs 遗留文件结构（仅迁移反序列化用）。</summary>
        private class LegacyBookmark
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public bool UseAppMode { get; set; } = true;
            public string BackgroundColor { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private class LegacyAiTool
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public string BackgroundColor { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private class LegacyProgram
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Command { get; set; } = "";
            public string Arguments { get; set; } = "";
            public string BackgroundColor { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        /// <summary>
        /// 一次性历史数据迁移（由 LocalDbService 在首次行级落库前调用）：
        /// 1) quickgroups.json 的分组设置与快捷项写入对应快捷面板（按 GroupId 匹配，未绑定面板接收默认组），
        ///    孤儿分组新建快捷面板承载；
        /// 2) 无 quickgroups.json 时，从更早的 bookmarks/aitools/programs JSON 迁移（保持原语义）；
        /// 3) 处理完的文件改名 .bak 留存。返回是否有数据变更。
        /// </summary>
        public static bool MigrateLegacyOnce()
        {
            if (_mgr == null || _mgr.Windows.Count == 0) return false;
            bool changed = false;
            string folder = SideDeskFolder;

            string quickFile = Path.Combine(folder, "quickgroups.json");
            if (File.Exists(quickFile))
            {
                try
                {
                    var groups = JsonConvert.DeserializeObject<List<LegacyQuickGroup>>(File.ReadAllText(quickFile));
                    if (groups != null)
                    {
                        foreach (var g in groups.OrderBy(x => x.Order))
                        {
                            // GroupId 绑定的面板优先；未绑定面板接收旧默认组
                            var panel = EnumerateQuickPanels().FirstOrDefault(p => p.GroupId == g.Id)
                                ?? (g.Id == LEGACY_DEFAULT_GROUP_ID
                                    ? EnumerateQuickPanels().FirstOrDefault(p => string.IsNullOrEmpty(p.GroupId))
                                    : null);
                            if (panel != null)
                            {
                                ApplyLegacyGroup(panel, g);
                                panel.GroupId = null; // 迁移完成后 GroupId 不再使用
                                changed = true;
                                continue;
                            }
                            // 孤儿分组：新建快捷面板承载
                            var np = new PanelItem { Type = PanelType.QuickGroup };
                            ApplyLegacyGroup(np, g);
                            var win = _mgr.Windows[0];
                            win.Panels ??= new List<PanelItem>();
                            win.Panels.Add(np);
                            changed = true;
                        }
                        // 未匹配到任何分组的 GroupId 清理，避免残留引用
                        foreach (var p in EnumerateQuickPanels())
                        {
                            if (!string.IsNullOrEmpty(p.GroupId))
                            {
                                p.GroupId = null;
                                changed = true;
                            }
                        }
                    }
                    File.Move(quickFile, quickFile + ".bak", true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuickGroup] quickgroups.json 迁移失败: {ex.Message}");
                }
                return changed;
            }

            // 更早的遗留文件（原 MigrateFromLegacy 语义：仅无 quickgroups.json 时执行）
            changed |= MigrateLegacyFile<LegacyBookmark>("bookmarks.json", "书签", b => new QuickItem
            {
                Id = b.Id,
                Name = b.Name,
                Type = "url",
                Target = b.Url,
                UseAppMode = b.UseAppMode,
                BackgroundColor = b.BackgroundColor,
                CreatedAt = b.CreatedAt
            });
            changed |= MigrateLegacyFile<LegacyAiTool>("aitools.json", "AI 工具", t => new QuickItem
            {
                Id = t.Id,
                Name = t.Name,
                Type = "url",
                Target = t.Url,
                UseAppMode = true,
                BackgroundColor = t.BackgroundColor,
                CreatedAt = t.CreatedAt
            });
            changed |= MigrateLegacyFile<LegacyProgram>("programs.json", "程序", p => new QuickItem
            {
                Id = p.Id,
                Name = p.Name,
                Type = "program",
                Target = string.IsNullOrEmpty(p.Arguments) ? p.Command : p.Command + " " + p.Arguments,
                UseAppMode = false,
                BackgroundColor = p.BackgroundColor,
                CreatedAt = p.CreatedAt
            });
            return changed;
        }

        private static void ApplyLegacyGroup(PanelItem panel, LegacyQuickGroup g)
        {
            panel.QuickGroupName = string.IsNullOrEmpty(g.Name) ? "快捷方式" : g.Name;
            panel.QuickColumns = g.Columns > 0 ? g.Columns : 1;
            panel.IsExpanded = g.IsExpanded;
            panel.QuickHideFrame = g.HideFrame;
            panel.QuickDisplayMode = string.IsNullOrEmpty(g.DisplayMode) ? "grid" : g.DisplayMode;
            panel.QuickOrder = g.Order;
            panel.OwnerType = string.IsNullOrEmpty(g.OwnerType) ? "personal" : g.OwnerType;
            panel.TeamId = g.TeamId;
            panel.TeamName = g.TeamName;
            panel.QuickItems = g.Items ?? new List<QuickItem>();
            if (g.ChildPanels != null && g.ChildPanels.Count > 0)
            {
                panel.ChildPanels ??= new List<PanelItem>();
                panel.ChildPanels.AddRange(g.ChildPanels);
            }
        }

        private static bool MigrateLegacyFile<T>(string fileName, string panelName, Func<T, QuickItem> convert)
        {
            string path = Path.Combine(SideDeskFolder, fileName);
            if (!File.Exists(path)) return false;
            try
            {
                var legacy = JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path));
                if (legacy == null || legacy.Count == 0)
                {
                    File.Move(path, path + ".bak", true);
                    return false;
                }
                var panel = new PanelItem
                {
                    Type = PanelType.QuickGroup,
                    QuickGroupName = panelName,
                    QuickColumns = 1,
                    QuickItems = legacy.Select(convert).ToList()
                };
                var win = _mgr!.Windows[0];
                win.Panels ??= new List<PanelItem>();
                win.Panels.Add(panel);
                File.Move(path, path + ".bak", true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickGroup] {fileName} 迁移失败: {ex.Message}");
                return false;
            }
        }
    }
}
