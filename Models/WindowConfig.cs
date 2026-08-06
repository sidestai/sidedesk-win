using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models;

public enum WindowType { Native, WebView }

public enum PanelType
{
    QuickGroup,      // 快捷方式
    StickyNotes,     // 便签
    Clipboard,       // 剪贴板收藏
    SystemMonitor,   // 系统信息/计数器
    UsageStats,      // 使用统计
    AIChat,          // AI 对话
    FileArea,        // 文件区
    ClipboardHistory,  // 剪贴板历史
    DataDashboard,     // 数据看板
    Timer,           // 计时器
    Group,           // 分组
    TodoList,        // 今日待办
    SignalLight,     // 信号灯
    DigitalEmployee, // 数字员工
    Friend,          // 好友
    ImageCarousel,   // 图片轮播
    DailyActivity,   // 每日活跃
    Calendar,        // 日历
    Plugin,          // 外部插件
    ScreenTime,      // 屏幕时长
    Countdown,       // 倒计时器
    Pomodoro         // 番茄闹钟
}

public class PanelItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ServerId { get; set; }  // 云同步服务器端 ID（服务器返回，持久化到 windows.json；null=尚未同步）
    public PanelType Type { get; set; }
    public string? GroupId { get; set; }  // 仅 QuickGroup 类型使用
    public string? Title { get; set; }    // 可选自定义标题
    public List<ClipboardSavedItem>? ClipboardItems { get; set; } // 剪贴板收藏数据
    public List<FileAreaItem>? FileItems { get; set; } // 文件区数据
    public List<DataDashboardItem>? DashboardItems { get; set; } // 数据看板项
    public List<TimerItem>? TimerItems { get; set; } // 计时器数据
    public List<CountdownItem>? CountdownItems { get; set; } // 倒计时器数据
    public List<PanelItem>? ChildPanels { get; set; } // 分组子面板
    public bool IsExpanded { get; set; } = true;      // 分组是否展开
    public string? GroupBGColor { get; set; }              // 分组自定义背景色（hex字符串，null=跟随主题）
    public string? BGColor { get; set; }                   // 模块自定义背景色（hex字符串，null=跟随主题）
    public List<StickyNote>? StickyNotes { get; set; } // 便签数据（per-panel）
    public List<TodoItem>? TodoItems { get; set; } // 今日待办数据（per-panel）
    public bool SkipWrapper { get; set; } = false;     // 是否跳过外层容器包装
    public bool HideFrame { get; set; } = false;       // 单个模块独立隐藏外框（边框/标题/背景/内边距）
    public bool IsPinned { get; set; } = false;        // 是否置顶（渲染在滚动区上方的置顶区，压缩滚动区高度）
    public int DashboardColumns { get; set; } = 1;     // 数据看板每排列数（1~4）
    public List<SignalLightItem>? SignalLights { get; set; } // 信号灯数据
    public bool SignalLightVertical { get; set; } = false; // 信号灯纵向排列
    public double SignalLightSpacing { get; set; } = 5;    // 信号灯间距（px）
    public string SignalLightAlignment { get; set; } = "Center"; // 信号灯对齐：Left/Center/Right
    public string FileAreaLayout { get; set; } = "List";         // 文件区布局：LargeIcons/SmallIcons/List
    public int FileAreaColumns { get; set; } = 1;                // 文件区列数（1~4）
    public double? PanelHeight { get; set; }                     // 模块内容区自定义高度（px）；null=自动高度
    public List<string>? DigitalEmployeeIds { get; set; }        // 数字员工模块：展示的员工 Id 列表（引用全局员工库）
    public int DigitalEmployeeColumns { get; set; } = 3;         // 数字员工模块：每排展示的员工数（1~5）
    public string? FriendName { get; set; }                      // 好友模块：名字
    public string? FriendAvatarPath { get; set; }                // 好友模块：头像图片路径
    public string? FriendEmoji { get; set; }                     // 好友模块：Emoji/文字头像（无图片时的兜底）
    public string? FriendStatusText { get; set; }                // 好友模块：状态文字
    public string FriendOnlineStatus { get; set; } = "online";   // 好友模块：在线状态 online/away/busy/offline
    public string FriendDisplayMode { get; set; } = "HorizontalSmall"; // 好友模块：显示方式 HorizontalSmall/VerticalLarge
    public List<string>? FriendUserIds { get; set; }             // 好友模块：展示的好友 userId 加密串列表（引用后端真实好友，与实际数据分离的显示副本）
    public string? CarouselFolder { get; set; }                  // 图片轮播：图片所在目录
    public int CarouselIntervalSeconds { get; set; } = 5;        // 图片轮播：切换间隔（秒）
    public bool CarouselRandom { get; set; } = false;            // 图片轮播：随机播放（否则顺序）
    public bool CarouselIncludeSubfolders { get; set; } = false; // 图片轮播：包含子目录
    public string CarouselStretch { get; set; } = "Uniform";     // 图片轮播：填充方式 Uniform/UniformToFill/Fill
    public double CarouselHeight { get; set; } = 140;            // 图片轮播：显示区高度（px）
    public bool CarouselAutoPlay { get; set; } = true;           // 图片轮播：自动播放
    public string? PluginId { get; set; }                        // 插件模块：插件 ID（对应 plugin.json 中的 id）
    // ===== 快捷方式组件（原 quickgroups.json 分组数据，已改为面板自有 + item_row 行级存储）=====
    public List<QuickItem>? QuickItems { get; set; }             // 快捷方式组件：快捷项列表（行级存储）
    public string QuickGroupName { get; set; } = "快捷方式";      // 快捷方式组件：分组名
    public int QuickColumns { get; set; } = 1;                   // 快捷方式组件：布局列数（1~4）
    public string QuickDisplayMode { get; set; } = "grid";       // 快捷方式组件：显示模式 grid/list/popup
    public bool QuickHideFrame { get; set; } = false;            // 快捷方式组件：隐藏外框（背景/边框/标题栏/内边距）
    public int QuickOrder { get; set; }                          // 快捷方式组件：排序序号（原分组 Order）
    public string ScreenTimeStyle { get; set; } = "List";        // 屏幕时长组件展示风格：List/Pie/Category/Window
    public string DailyActivityView { get; set; } = "Month";      // 每日活跃视图模式：Month(当月热力图)/Today(仅今日)/Week(前后5天逐日)
    public string DailyActivityType { get; set; } = "screen";     // 每日活跃组件展示的数据类型：screen=本地屏幕使用时长，其他=服务端类型（含内置固定类型番茄/屏幕时长/健身记录/鼠标键盘活跃）
    public PomodoroSettings? Pomodoro { get; set; }                    // 番茄闹钟数据
    /// <summary>归属类型: personal=个人, team=团队</summary>
    public string OwnerType { get; set; } = "personal";
    /// <summary>所属团队 ID（OwnerType=team 时有值）</summary>
    public string? TeamId { get; set; }
    /// <summary>团队名称快照（显示用）</summary>
    public string? TeamName { get; set; }
    /// <summary>来源分享记录ID（分享导入时有值，服务端加密ID，可查分享详情/对账）</summary>
    public string? ShareId { get; set; }
    /// <summary>来源分享短码（分享导入时有值，展示/再分享用）</summary>
    public string? ShareCode { get; set; }
    /// <summary>分享导入模式: null=本地创建/非分享来源, copy=复制, reference=引用</summary>
    public string? ShareMode { get; set; }
    /// <summary>导入权限快照（仅 reference 有意义）：允许编辑数据（含便签/todo 等行数据）</summary>
    public bool AllowEdit { get; set; } = true;
    /// <summary>导入权限快照（仅 reference 有意义）：允许删除行/模块</summary>
    public bool AllowDelete { get; set; } = true;
    /// <summary>导入权限快照（仅 reference 有意义）：允许再分享</summary>
    public bool AllowReShare { get; set; } = false;
    /// <summary>引用分享且不允许编辑 → 内容只读（拦截一切行数据修改）</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsShareReadOnly => ShareMode == "reference" && !AllowEdit;
    /// <summary>引用分享且不允许删除 → 禁止删除模块/行</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsShareDeleteBlocked => ShareMode == "reference" && !AllowDelete;
}

/// <summary>
/// 数字员工组件行包装：行级存储/同步用（原始数据为 List&lt;string&gt; 员工 Id 引用）
/// </summary>
public class DigitalEmployeeRow
{
    public string Id { get; set; } = "";      // = EmployeeId（行唯一标识）
    public string EmployeeId { get; set; } = ""; // 全局员工库中的员工 Id
}

/// <summary>
/// 好友组件行包装：行级存储/同步用（原始数据为 List&lt;string&gt; 好友 userId 加密串引用）
/// </summary>
public class FriendUserRow
{
    public string Id { get; set; } = "";      // = UserId（行唯一标识）
    public string UserId { get; set; } = "";  // 好友 userId 加密串
}

public class FileAreaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Path { get; set; } = "";      // 文件或文件夹完整路径
    public string Name { get; set; } = "";      // 显示名称
    public bool IsFolder { get; set; }          // 是否为文件夹
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // ===== 云文件相关（IsCloudFile=true 时有效）=====
    public bool IsCloudFile { get; set; }       // 是否为云文件（云盘导入）
    public string? CloudSpaceId { get; set; }   // 所属云空间 Id（CloudSpaceManager）
    public string? CloudSpaceName { get; set; } // 云空间名称快照（如"百度网盘"）
    public string? CloudSpaceIcon { get; set; } // 云空间图标快照（emoji）
    public string? CloudFileId { get; set; }    // 云文件ID / 分享链接 / 提取码
    public string? CloudUrl { get; set; }       // 实际打开的链接（分享链接优先，否则云空间主页）
}

public class ClipboardSavedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = "";
    public string ContentType { get; set; } = "Text"; // Text, Image, File
    public DateTime SavedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 计时器项
/// </summary>
public class TimerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新计时";
    public int TimeoutSeconds { get; set; } = 3600; // 默认 1 小时
    public bool IsRunning { get; set; } = false;
    public DateTime? StartTime { get; set; }
    public TimeSpan ElapsedTime { get; set; } = TimeSpan.Zero;
    
    /// <summary>
    /// 历史记录：日期 -> 总秒数
    /// </summary>
    public Dictionary<string, int> DailyRecords { get; set; } = new();

    /// <summary>
    /// 获取总经过时间（含当前运行时段）
    /// </summary>
    public TimeSpan GetElapsed()
    {
        if (IsRunning && StartTime.HasValue)
            return ElapsedTime + (DateTime.Now - StartTime.Value);
        return ElapsedTime;
    }
}

/// <summary>
/// 倒计时项
/// </summary>
public class CountdownItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新倒计时";
    public int TotalSeconds { get; set; } = 3600;        // 倒计时总时长（秒）
    public double RemainingSeconds { get; set; } = 3600; // 剩余秒数（暂停时保存进度）
    public bool IsRunning { get; set; } = false;
    public DateTime? StartTime { get; set; }             // 本次启动的时刻（运行中用于计算已消耗时间）
    public bool IsFinished { get; set; } = false;        // 是否已到期

    // 到时提醒方式
    public bool RemindFlash { get; set; } = true;        // 闪烁窗口
    public bool RemindPopup { get; set; } = true;        // 弹出提示窗口
    public string RemindAction { get; set; } = "None";   // 到时操作：None/Hibernate/Shutdown

    /// <summary>获取当前剩余时间（含运行中的实时计算）</summary>
    public TimeSpan GetRemaining()
    {
        if (IsRunning && StartTime.HasValue)
        {
            var remaining = RemainingSeconds - (DateTime.Now - StartTime.Value).TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
        return TimeSpan.FromSeconds(Math.Max(0, RemainingSeconds));
    }
}

/// <summary>
/// 今日待办项
/// </summary>
public class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public string Description { get; set; } = "";  // 详细内容/备注
    public bool IsDone { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? DueTime { get; set; }               // 任务时间（可选）
    public int RemindBeforeMinutes { get; set; } = -1;   // 提前多少分钟提醒；-1=不提醒，0=准时
    public bool Reminded { get; set; } = false;          // 是否已提醒（防重复）
}

/// <summary>
/// 信号灯项
/// </summary>
public class SignalLightItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Color { get; set; } = "#FF4444";   // 灯颜色（hex）
    public double Size { get; set; } = 36;            // 灯直径
    public string? Icon { get; set; }                  // 灯上方图标（emoji/文字，空则无）
    public bool IsOn { get; set; } = true;             // 亮/灭状态
    public string LightStyle { get; set; } = "Circle"; // 灯样式：Circle/Square/Pixel
}

/// <summary>
/// 番茄闹钟设置与运行状态
/// </summary>
public class PomodoroSettings
{
    /// <summary>行级同步用的稳定 GUID（每个番茄钟仅一条行记录）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // 时长配置
    public int WorkMinutes { get; set; } = 25;          // 番茄钟时长（分钟）
    public int ShortBreakMinutes { get; set; } = 5;     // 短休息时长（分钟）
    public int LongBreakMinutes { get; set; } = 15;     // 长休息时长（分钟）
    public int LongBreakInterval { get; set; } = 4;     // 每完成 N 个番茄后进入长休息

    // 行为配置
    public bool AutoStartNext { get; set; } = false;    // 阶段结束后自动开始下一阶段
    public bool RemindFlash { get; set; } = true;       // 结束时闪烁窗口
    public bool RemindPopup { get; set; } = true;       // 结束时弹出提示窗口

    // 运行状态（仅本地持久化，不参与云同步/分享；同步只带番茄数与时长等配置）
    public string Mode { get; set; } = "Work";          // 当前模式：Work/ShortBreak/LongBreak
    public double RemainingSeconds { get; set; } = 25 * 60;
    public bool IsRunning { get; set; } = false;
    public DateTime? StartTime { get; set; }            // 本次启动时刻（运行中用于计算已消耗时间）
    public int CompletedCount { get; set; } = 0;        // 当日已完成番茄数
    public string? CompletedDate { get; set; }          // 计数所属日期（yyyy-MM-dd，跨天自动清零）

    /// <summary>获取当前模式的总时长（秒）</summary>
    public int GetModeTotalSeconds() => Mode switch
    {
        "ShortBreak" => ShortBreakMinutes * 60,
        "LongBreak" => LongBreakMinutes * 60,
        _ => WorkMinutes * 60
    };

    /// <summary>获取当前剩余时间（含运行中的实时计算）</summary>
    public TimeSpan GetRemaining()
    {
        if (IsRunning && StartTime.HasValue)
        {
            var remaining = RemainingSeconds - (DateTime.Now - StartTime.Value).TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
        return TimeSpan.FromSeconds(Math.Max(0, RemainingSeconds));
    }

    /// <summary>确保计数为当天（跨天清零）</summary>
    public void EnsureTodayCount()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (CompletedDate != today)
        {
            CompletedDate = today;
            CompletedCount = 0;
        }
    }
}

public class WindowConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ServerId { get; set; }  // 云同步服务器端 ID（服务器返回，持久化到 windows.json；null=尚未同步）
    public string Name { get; set; } = "新窗口";
    public string? BGColor { get; set; }   // 窗口(bar)自定义背景色（hex字符串，null=跟随主题）
    public WindowType Type { get; set; } = WindowType.Native;
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<PanelItem> Panels { get; set; } = new();
    public bool IsEditMode { get; set; } = false;
    public bool IsAppMode { get; set; } = false;
    /// <summary>窗口级启用的工具列表（显示在底部工具栏）</summary>
    public List<string> EnabledTools { get; set; } = new();
    /// <summary>是否显示在顶部标签列表中（小屏管理中勾选才显示）</summary>
    public bool ShowInTabs { get; set; } = true;
    /// <summary>归属类型: personal=个人, team=团队</summary>
    public string OwnerType { get; set; } = "personal";
    /// <summary>所属团队 ID（OwnerType=team 时有值）</summary>
    public string? CorpId { get; set; }
    /// <summary>分享导入模式: null=本地创建, copy=复制, reference=引用</summary>
    public string? ShareMode { get; set; }
    /// <summary>引用模式时指向源窗口的 serverId</summary>
    public string? SourceBarId { get; set; }
    /// <summary>来源分享记录ID（分享导入时有值，服务端加密ID，可查分享详情/对账）</summary>
    public string? ShareId { get; set; }
    /// <summary>来源分享短码（分享导入时有值，展示/再分享用）</summary>
    public string? ShareCode { get; set; }
    /// <summary>导入权限快照（仅 reference 有意义）：允许编辑数据（含模块内行数据）</summary>
    public bool AllowEdit { get; set; } = true;
    /// <summary>导入权限快照（仅 reference 有意义）：允许删除</summary>
    public bool AllowDelete { get; set; } = true;
    /// <summary>导入权限快照（仅 reference 有意义）：允许再分享</summary>
    public bool AllowReShare { get; set; } = false;
    /// <summary>引用分享且不允许编辑 → 窗口内所有模块内容只读</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsShareReadOnly => ShareMode == "reference" && !AllowEdit;
    /// <summary>引用分享且不允许删除 → 窗口内模块禁止删除</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsShareDeleteBlocked => ShareMode == "reference" && !AllowDelete;
}

public class DockWindowState
{
    public List<string> WindowIds { get; set; } = new();
    public int ActiveIndex { get; set; } = 0;
}

public class WindowConfigManager
{
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDesk", "windows.json");

    public List<WindowConfig> Windows { get; set; } = new();
    public DockWindowState LeftDock { get; set; } = new();
    public DockWindowState RightDock { get; set; } = new();
    /// <summary>全局后台数据看板项（不绑定任何面板，仅后台轮询）</summary>
    public List<DataDashboardItem> BackgroundDashboardItems { get; set; } = new();

    public static WindowConfigManager Load()
    {
        var mgr = LoadCore();
        // 快捷分组门面：将全局配置管理器注入 QuickGroupManager（快捷数据已改存面板）
        QuickGroupManager.Init(mgr);
        return mgr;
    }

    private static WindowConfigManager LoadCore()
    {
        // 主存储：SQLite（sidedesk.db）；windows.json 仅作迁移来源/备份快照兜底
        try
        {
            if (Services.LocalDbService.DbExists)
            {
                try
                {
                    var mgr = Services.LocalDbService.LoadManager();
                    MigrateLegacyStickyNotesOnce(mgr);
                    return mgr;
                }
                catch (Exception dbEx)
                {
                    // db 损坏/读取失败：降级从 JSON 快照加载，并删除坏 db（下次 Save 会重建）
                    System.Diagnostics.Debug.WriteLine($"[LocalDb] 数据库读取失败，降级 JSON 快照: {dbEx.Message}");
                    try { if (File.Exists(Services.LocalDbService.DbPath)) File.Delete(Services.LocalDbService.DbPath); } catch { }
                    if (File.Exists(_filePath))
                    {
                        var jsonSnap = File.ReadAllText(_filePath);
                        var mgrSnap = JsonConvert.DeserializeObject<WindowConfigManager>(jsonSnap) ?? new WindowConfigManager();
                        MigrateLegacyStickyNotesOnce(mgrSnap);
                        return mgrSnap;
                    }
                    return new WindowConfigManager();
                }
            }
            if (File.Exists(_filePath))
            {
                // 首次升级：windows.json 导入 SQLite（成功后原文件改名 .bak-migrated 保留）
                if (Services.LocalDbService.ImportFromJsonFile(_filePath))
                {
                    var mgrDb = Services.LocalDbService.LoadManager();
                    MigrateLegacyStickyNotesOnce(mgrDb);
                    return mgrDb;
                }
                // 导入失败兜底：仍按旧 JSON 方式加载（下次 Save 会再次尝试写入 SQLite）
                var json = File.ReadAllText(_filePath);
                var mgr = JsonConvert.DeserializeObject<WindowConfigManager>(json) ?? new WindowConfigManager();
                MigrateLegacyStickyNotesOnce(mgr);
                return mgr;
            }
        }
        catch { }
        return new WindowConfigManager();
    }

    /// <summary>
    /// 旧版全局便签库一次性迁移：per-panel 存储引入前便签数据存于全局 sticky-notes.json。
    /// 升级后首次启动时将全局数据迁入尚无 per-panel 数据的遗留便签模块，并写入完成标记；
    /// 之后新建/导入的便签模块一律从空列表开始（否则新面板会被全局遗留数据污染）。
    /// </summary>
    private static void MigrateLegacyStickyNotesOnce(WindowConfigManager mgr)
    {
        try
        {
            string flagPath = Path.Combine(Path.GetDirectoryName(_filePath)!, "sticky-notes-migrated.flag");
            if (File.Exists(flagPath)) return;

            bool migrated = false;
            var globalNotes = StickyNoteManager.GetAll();
            if (globalNotes.Count > 0)
            {
                foreach (var win in mgr.Windows)
                {
                    foreach (var p in EnumerateAllPanels(win.Panels))
                    {
                        if (p.Type == PanelType.StickyNotes && p.StickyNotes == null)
                        {
                            p.StickyNotes = new List<StickyNote>(globalNotes);
                            migrated = true;
                        }
                    }
                }
            }
            File.WriteAllText(flagPath, DateTime.Now.ToString("o"));
            if (migrated) mgr.Save();
        }
        catch { }
    }

    private static IEnumerable<PanelItem> EnumerateAllPanels(List<PanelItem> panels)
    {
        foreach (var p in panels)
        {
            yield return p;
            if (p.ChildPanels != null)
                foreach (var c in EnumerateAllPanels(p.ChildPanels))
                    yield return c;
        }
    }

    public void Save()
    {
        // 主存储：SQLite 单事务写入（原子、防写坏）
        try
        {
            Services.LocalDbService.SaveManager(this);
            // 同步导出 JSON 快照：供本地备份打包使用，并在 db 损坏时作为恢复兜底
            Services.LocalDbService.ExportJsonSnapshot(this, _filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] 保存失败: {ex.Message}");
        }

        // 行级同步：任何配置保存后通知 CloudSyncService（防抖 2s + 快照对比确保不重复推送）
        try { Services.CloudSyncService.Instance.NotifyRowsChanged(); } catch { }
    }

    public WindowConfig? GetById(string id) => Windows.FirstOrDefault(w => w.Id == id);

    public void Add(WindowConfig config)
    {
        Windows.Add(config);
        Save();
    }

    public void Remove(string id)
    {
        Windows.RemoveAll(w => w.Id == id);
        LeftDock.WindowIds.Remove(id);
        RightDock.WindowIds.Remove(id);
        Save();
    }

    public void EnsureDefault()
    {
        if (Windows.Count == 0)
        {
            var defaultWin = new WindowConfig
            {
                Name = "主页",
                Type = WindowType.Native,
                Panels = new List<PanelItem>
                {
                    new() { Type = PanelType.QuickGroup },
                    new() { Type = PanelType.StickyNotes },
                    new() { Type = PanelType.Clipboard },
                    new() { Type = PanelType.SystemMonitor }
                }
            };
            Windows.Add(defaultWin);
            LeftDock.WindowIds.Add(defaultWin.Id);
            RightDock.WindowIds.Add(defaultWin.Id);
            Save();
        }
    }
}
