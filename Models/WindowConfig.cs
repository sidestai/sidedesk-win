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
    Pomodoro,        // 番茄闹钟
    WebBaidu,        // 百度搜索（内嵌网页组件）
    CheckIn          // 打卡（多个打卡按钮，点击计数并写入每日活跃）
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
    public List<CheckInItem>? CheckInItems { get; set; }               // 打卡组件：打卡按钮列表（行级存储）
    public int CheckInColumns { get; set; } = 2;                       // 打卡组件：每排列数（1~4）
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
    /// <summary>用户自定义别名：非空时屏幕上优先于 Name 显示（项右键菜单「编辑别名」设置，清空恢复原名）</summary>
    public string? Alias { get; set; }
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
/// 信号灯状态条件：变量按 Op 比较满足；MISSING 时变量缺失/未取到值即满足
/// </summary>
public class SignalLightCondition
{
    public string VarKey { get; set; } = "";   // 数据变量名
    public string Op { get; set; } = "EQ";     // 比较操作符：EQ 等于 / GT 大于 / LT 小于 / MISSING 不存在
    public string Value { get; set; } = "";    // 期望值（用于 EQ/GT/LT）
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
    public bool IsOn { get; set; } = true;             // 亮/灭状态（无条件匹配时的手动状态）
    public string LightStyle { get; set; } = "Circle"; // 灯样式：Circle/Square/Pixel
    public List<SignalLightCondition> OnConditions { get; set; } = new();    // 满足 → 开启
    public List<SignalLightCondition> OffConditions { get; set; } = new();   // 满足 → 关闭
    public List<SignalLightCondition> BlinkConditions { get; set; } = new(); // 满足 → 闪烁（优先级最高）
    public string OnConditionMode { get; set; } = "OR";    // 开条件组合：OR 任一满足 / AND 全部满足
    public string OffConditionMode { get; set; } = "OR";   // 关条件组合
    public string BlinkConditionMode { get; set; } = "OR"; // 闪烁条件组合
    public double BlinkIntervalSeconds { get; set; } = 1; // 闪烁频率（秒/次）
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

/// <summary>
/// 打卡项：一个可点击的打卡按钮，每点一次当日计数 +1，并镜像写入「每日活跃」同名类型。
/// 与番茄钟 CompletedCount 同构：本地保留当日计数（离线可用、跨天自动清零），点击时同步累加到服务端每日活跃。
/// </summary>
public class CheckInItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";              // 打卡内容文字（同时作为「每日活跃」的类型名）
    public string Icon { get; set; } = "";              // emoji 图标
    public string Color { get; set; } = "#4A90D9";      // 主题色（hex）
    public int Count { get; set; } = 0;                 // 当日打卡次数
    public string? CountDate { get; set; }              // 计数所属日期（yyyy-MM-dd，跨天自动清零）
    public int SortIndex { get; set; } = 0;             // 排序序号

    /// <summary>确保计数为当天（跨天清零）</summary>
    public void EnsureTodayCount()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (CountDate != today)
        {
            CountDate = today;
            Count = 0;
        }
    }

    /// <summary>当日打卡次数（跨天未点击时返回 0，不改动持久值，供渲染展示）</summary>
    public int TodayCount()
        => CountDate == DateTime.Now.ToString("yyyy-MM-dd") ? Count : 0;
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
    /// <summary>是否显示在顶部标签列表中（微屏管理中勾选才显示）</summary>
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
    // 遗留存储路径：首次升级时作为迁移来源导入 SQLite；快照写盘已废弃，遗留文件由 Save 归档为 .bak-migrated
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDesk", "windows.json");

    public List<WindowConfig> Windows { get; set; } = new();
    public DockWindowState LeftDock { get; set; } = new();
    public DockWindowState RightDock { get; set; } = new();
    /// <summary>全局后台数据看板项（不绑定任何面板，仅后台轮询）</summary>
    public List<DataDashboardItem> BackgroundDashboardItems { get; set; } = new();
    /// <summary>变量集定义（多变量聚合：求和/求平均/最大/最小/计数，生成本地新变量）</summary>
    public List<VariableSet> VariableSets { get; set; } = new();

    /// <summary>
    /// 本内存实例加载时所归属的数据作用域（"local" 或 "u:{密文}"），由 LocalDbService 加载时打标。
    /// 与 <see cref="Services.LocalDbService.CurrentScope"/> 不一致即表示登录态已变更、而窗口尚未按新作用域重载：
    /// 此时内存里是旧作用域的数据（登出重启后往往只剩兜底空窗口），任何“以内存为准”的全量写库或结构上传
    /// 都会把新作用域下的既有数据整体清空（本地 DeleteMissing + 服务端对账软删），因此必须一律拒绝。
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public string? LoadedScope { get; set; }

    public static WindowConfigManager Load()
    {
        var mgr = LoadCore();
        // 作用域戳兜底：LoadManager 已按实际读取的作用域打标；其余兜底路径（库损坏/旧 JSON 回退/异常）
        // 返回的实例语义是“当前作用域下没有数据”，同样按当前作用域打标
        if (string.IsNullOrEmpty(mgr.LoadedScope))
            mgr.LoadedScope = Services.LocalDbService.CurrentScope();
        // dock 成员自愈：dock 状态与窗口表是两份独立持久化，异常写入可能使二者脱节
        mgr.RepairDockMembership();
        // 快捷分组门面：将全局配置管理器注入 QuickGroupManager（快捷数据已改存面板）
        QuickGroupManager.Init(mgr);
        // 遗留 JSON → SQLite 迁移预热：各管理器为惰性迁移（首次访问才触发），
        // 此处启动时主动触发一次，确保数据尽早落库而非滞留旧 JSON
        try { AIConfigManager.GetConfig(); } catch { }
        try { CloudSpaceManager.GetAll(); } catch { }
        try { DigitalEmployeeManager.Load(); } catch { }
        try { SideDesk.Windows.TitleLockRuleManager.GetAll(); } catch { }
        return mgr;
    }

    private static WindowConfigManager LoadCore()
    {
        // 主存储：SQLite（sidedesk.db）；windows.json 仅作首次升级的迁移来源
        try
        {
            if (Services.LocalDbService.DbExists)
            {
                try
                {
                    var mgr = Services.LocalDbService.LoadManager();
                    MigrateLegacyStickyNotesOnce(mgr);
                    PurgeEmptyStickyNotesOnce(mgr);
                    ConsolidateDuplicateWindowsOnce(mgr);
                    mgr.ArchiveLegacyWindowsJsonOnce();
                    return mgr;
                }
                catch (Exception dbEx)
                {
                    // db 损坏/读取失败：改名留存坏库便于人工修复（SQLite WAL 下极罕见）；
                    // windows.json 快照已停止写盘不再作为回滚源，窗口数据由云同步从服务端重新拉取
                    System.Diagnostics.Debug.WriteLine($"[LocalDb] database read failed: {dbEx.Message}");
                    try
                    {
                        string corrupt = Services.LocalDbService.DbPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        if (File.Exists(Services.LocalDbService.DbPath)) File.Move(Services.LocalDbService.DbPath, corrupt);
                    }
                    catch { }
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
                    PurgeEmptyStickyNotesOnce(mgrDb);
                    ConsolidateDuplicateWindowsOnce(mgrDb);
                    return mgrDb;
                }
                // 导入失败兜底：仍按旧 JSON 方式加载（下次 Save 会再次尝试写入 SQLite）
                var json = File.ReadAllText(_filePath);
                var mgr = JsonConvert.DeserializeObject<WindowConfigManager>(json) ?? new WindowConfigManager();
                MigrateLegacyStickyNotesOnce(mgr);
                PurgeEmptyStickyNotesOnce(mgr);
                ConsolidateDuplicateWindowsOnce(mgr);
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

    /// <summary>
    /// 一次性清理被同步污染的空便签：旧版同步 bug 会把其他类型的行错合并为空白便签并持久化
    /// （便签编辑器有“空内容不落库”气闸，本地存在的空白便签均为污染数据）。
    /// 升级后首次启动时移除全部面板中的空白便签，并写入完成标记。
    /// </summary>
    private static void PurgeEmptyStickyNotesOnce(WindowConfigManager mgr)
    {
        try
        {
            string flagPath = Path.Combine(Path.GetDirectoryName(_filePath)!, "sticky-empty-purged.flag");
            if (File.Exists(flagPath)) return;

            bool changed = false;
            foreach (var win in mgr.Windows)
            {
                foreach (var p in EnumerateAllPanels(win.Panels))
                {
                    if (p.Type != PanelType.StickyNotes || p.StickyNotes == null || p.StickyNotes.Count == 0) continue;
                    int before = p.StickyNotes.Count;
                    p.StickyNotes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Content));
                    if (p.StickyNotes.Count < before) changed = true;
                }
            }
            File.WriteAllText(flagPath, DateTime.Now.ToString("o"));
            if (changed) mgr.Save();
        }
        catch { }
    }

    /// <summary>
    /// 一次性合并重名重复窗口：旧版同步 bug（结构同步 500 + xid- 遗留密文反复新建）
    /// 使服务端产生重复窗口，结构拉取又把它们补齐成同名本地窗口。
    /// 升级后首次启动时，同名个人窗口仅保留模块最多的一个，其余移除
    /// （其内容为同批 bug 的重复镜像，服务端会在下次同步对账时级联软删）。
    /// </summary>
    private static void ConsolidateDuplicateWindowsOnce(WindowConfigManager mgr)
    {
        try
        {
            string flagPath = Path.Combine(Path.GetDirectoryName(_filePath)!, "dup-windows-consolidated.flag");
            if (File.Exists(flagPath)) return;

            bool changed = false;
            var groups = mgr.Windows
                .Where(w => w.OwnerType != "team" && string.IsNullOrEmpty(w.ShareMode))
                .GroupBy(w => w.Name ?? "")
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var g in groups)
            {
                var keep = g.OrderByDescending(w => CountPanelsRecursive(w.Panels)).First();
                foreach (var w in g.Where(w => w.Id != keep.Id).ToList())
                {
                    mgr.Windows.Remove(w);
                    mgr.LeftDock.WindowIds.Remove(w.Id);
                    mgr.RightDock.WindowIds.Remove(w.Id);
                    changed = true;
                    System.Diagnostics.Debug.WriteLine($"[WindowConfig] merged duplicate windows: removed {w.Name} ({w.Id}), kept {keep.Id}");
                }
            }
            File.WriteAllText(flagPath, DateTime.Now.ToString("o"));
            if (changed) mgr.Save();
        }
        catch { }
    }

    private static int CountPanelsRecursive(List<PanelItem> panels)
    {
        int n = 0;
        foreach (var p in panels)
        {
            n++;
            if (p.ChildPanels != null) n += CountPanelsRecursive(p.ChildPanels);
        }
        return n;
    }

    /// <summary>
    /// 一次性移除同一窗口内绑定相同 ServerId 的重复面板（保留遍历顺序中最先出现的一个，跨窗口镜像不受影响）。
    /// 遗留 xid- 密文经服务端认领收敛后，多个重复面板会被映射到同一服务端模块，此处去重。
    /// 在同步映射回写成功后调用；真正移除过重复面板才写完成标记，否则下次同步继续检查。
    /// </summary>
    public void DedupPanelsSharingServerIdOnce()
    {
        try
        {
            string flagPath = Path.Combine(Path.GetDirectoryName(_filePath)!, "dup-panels-deduped.flag");
            if (File.Exists(flagPath)) return;

            bool changed = false;
            foreach (var win in Windows)
            {
                var seen = new HashSet<string>();
                if (DedupPanelListByServerId(win.Panels, seen)) changed = true;
            }
            if (changed)
            {
                File.WriteAllText(flagPath, DateTime.Now.ToString("o"));
                Save();
            }
        }
        catch { }
    }

    private static bool DedupPanelListByServerId(List<PanelItem> panels, HashSet<string> seen)
    {
        bool changed = false;
        int i = 0;
        while (i < panels.Count)
        {
            var p = panels[i];
            if (!string.IsNullOrEmpty(p.ServerId) && !seen.Add(p.ServerId!))
            {
                panels.RemoveAt(i);
                changed = true;
                continue;
            }
            if (p.ChildPanels != null && DedupPanelListByServerId(p.ChildPanels, seen)) changed = true;
            i++;
        }
        return changed;
    }

    public void Save()
    {
        // 主存储：SQLite 单事务写入（原子、防写坏）
        try
        {
            Services.LocalDbService.SaveManager(this);
            ArchiveLegacyWindowsJsonOnce();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] save failed: {ex.Message}");
        }
    
        // 行级同步：任何配置保存后通知 CloudSyncService（防抖 2s + 快照对比确保不重复推送）
        try { Services.CloudSyncService.Instance.NotifyRowsChanged(); } catch { }
    }
    
    /// <summary>
    /// 一次性归档遗留 windows.json：快照写盘已废弃（SQLite 为唯一存储，备份打包时从内存实时生成），
    /// 旧文件改名 .bak-migrated 保留。
    /// </summary>
    private void ArchiveLegacyWindowsJsonOnce()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            File.Move(_filePath, _filePath + ".bak-migrated", overwrite: true);
        }
        catch { }
    }

    public WindowConfig? GetById(string id) => Windows.FirstOrDefault(w => w.Id == id);

    public void Add(WindowConfig config)
    {
        Windows.Add(config);
        Save();
    }

    /// <summary>
    /// 新增微屏并同步加入左右两侧 dock 的顶部标签（多屏/双侧布局下所有窗口都能看到新微屏）。
    /// </summary>
    public void AddToAllDocks(WindowConfig config)
    {
        Windows.Add(config);
        if (!LeftDock.WindowIds.Contains(config.Id)) LeftDock.WindowIds.Add(config.Id);
        if (!RightDock.WindowIds.Contains(config.Id)) RightDock.WindowIds.Add(config.Id);
        Save();
    }

    public void Remove(string id)
    {
        Windows.RemoveAll(w => w.Id == id);
        LeftDock.WindowIds.Remove(id);
        RightDock.WindowIds.Remove(id);
        Save();
    }

    /// <summary>样例数据初始化的一次性标记 key（按账号作用域隔离，存 local_app_state）</summary>
    private const string DefaultBarSeededFlag = "default_bar_seeded";

    /// <summary>
    /// 样例数据初始化：仅新安装（当前作用域未置标）且没有任何 bar 时，自动创建默认 bar 并填充示例面板；
    /// 置标后即使用户删光所有 bar 也不再自动填充。旧版升级首次启动若已有 bar 则只置标不建数据；
    /// 若 bar 为空视为用户已主动清空，同样不再补建。
    /// 示例面板顺序：便签（使用指南/欢迎）→ 常用网址快捷组 → 分组（信号灯）→ 今日待办 → 番茄闹钟。
    /// </summary>
    public void EnsureDefault()
    {
        if (Services.LocalDbService.GetStateFlag(DefaultBarSeededFlag)) return; // 已初始化过，永不再填充
        // 全库只播一次：其他作用域已播过示例（如曾在登录态首次安装）时，本作用域只置标不播 ——
        // 否则登出回 local / 切换账号会再播一份示例 bar，随后被晋升搬进账号作用域造成重复窗口
        if (Services.LocalDbService.AnyScopeStateFlag(DefaultBarSeededFlag))
        {
            Services.LocalDbService.SetStateFlag(DefaultBarSeededFlag);
            return;
        }
        Services.LocalDbService.SetStateFlag(DefaultBarSeededFlag);

        if (Windows.Count == 0)
        {
            // 播种文案按编译区域切换：中文构建用中文，国际构建（API_REGION_EN）用英文；
            // 产品英文名跟随 i18n 惯例 common.metadeskScreen = SideDesk，元桌浏览器英文名为 MetaDesk
#if API_REGION_EN
            const string noteHowTo = "How to use SideDesk\n· Right-click the sidebar to add or edit modules\n· Drag a module's title bar to reorder\n· Change theme, language and dock side in Settings";
            const string noteWelcome = "Welcome to SideDesk!\nA smart sidebar that lives on the edge of your screen: notes, todos, shortcuts, timers... always within reach, no window switching.";
            const string groupName = "Frequent Sites";
            const string browserName = "MetaDesk";
            const string browserUrl = "https://www.desktxp.com";
            const string officialName = "Official Site";
            const string bingName = "Bing";
            const string todoEat = "Eat";
            const string todoSleep = "Sleep";
#else
            const string noteHowTo = "如何使用微屏\n· 右键侧边栏可添加/编辑模块\n· 拖拽模块标题栏可排序\n· 设置中可换主题、语言与停靠位置";
            const string noteWelcome = "欢迎使用微屏！\n这是一条常驻屏幕边缘的智能侧边栏：便签、待办、快捷方式、计时器……触手可及，无需切换窗口。";
            const string groupName = "常用网址";
            const string browserName = "元桌浏览器";
            const string browserUrl = "https://yzllq.com";
            const string officialName = "微屏官网";
            const string bingName = "必应";
            const string todoEat = "吃饭";
            const string todoSleep = "睡觉";
#endif
            var defaultWin = new WindowConfig
            {
                Name = Framework.Lang.T("common.home"),
                Type = WindowType.Native,
                Panels = new List<PanelItem>
                {
                    // ① 便签：使用指南 + 欢迎文案
                    new()
                    {
                        Type = PanelType.StickyNotes,
                        StickyNotes = new List<StickyNote>
                        {
                            new() { Content = noteHowTo },
                            new() { Content = noteWelcome }
                        }
                    },
                    // ② 快捷组：常用网址（单列列表）
                    new()
                    {
                        Type = PanelType.QuickGroup,
                        QuickGroupName = groupName,
                        QuickColumns = 1,
                        QuickItems = new List<QuickItem>
                        {
                            new() { Name = browserName, Type = "url", Target = browserUrl, SortIndex = 0 },
                            new() { Name = bingName, Type = "url", Target = "https://www.bing.com", SortIndex = 1 },
                            new() { Name = "DEEPSEEK", Type = "url", Target = "https://chat.deepseek.com", SortIndex = 2 },
                            new() { Name = officialName, Type = "url", Target = Constants.URLs.HomePageBaseUrl, SortIndex = 3 }
                        }
                    },
                    // ③ 分组：内嵌信号灯（红/黄/绿三灯竖排，与手动添加的默认配置一致）
                    new()
                    {
                        Type = PanelType.Group,
                        Title = Framework.Lang.T("nativePanel.newGroup"),
                        IsExpanded = true,
                        ChildPanels = new List<PanelItem>
                        {
                            new()
                            {
                                Type = PanelType.SignalLight,
                                Title = Framework.Lang.T("nativePanel.signalLight"),
                                SignalLightVertical = true,
                                SignalLights = new List<SignalLightItem>
                                {
                                    new() { Color = "#FF4444", Size = 36, Icon = "", IsOn = true },
                                    new() { Color = "#FFAA00", Size = 36, Icon = "", IsOn = true },
                                    new() { Color = "#44CC44", Size = 36, Icon = "", IsOn = true }
                                }
                            }
                        }
                    },
                    // ④ 今日待办：示例任务
                    new()
                    {
                        Type = PanelType.TodoList,
                        TodoItems = new List<TodoItem>
                        {
                            new() { Text = todoEat },
                            new() { Text = todoSleep }
                        }
                    },
                    // ⑤ 番茄闹钟：默认 25 分钟番茄钟
                    new() { Type = PanelType.Pomodoro, Pomodoro = new PomodoroSettings() }
                }
            };
            Windows.Add(defaultWin);
            LeftDock.WindowIds.Add(defaultWin.Id);
            RightDock.WindowIds.Add(defaultWin.Id);
            Save();
        }
    }

    /// <summary>
    /// 兜底保证至少存在一个 bar：所有 bar 被删光（或数据异常为空）时创建一个空的原生 bar。
    /// 否则侧边栏会 ClearContent 折叠 nativePanel → 右键失效，且空白态无「新建微屏」入口，陷入无法新增模块的死状态。
    /// 与 EnsureDefault「首次安装播种示例面板」不同：这里只建一个空 bar（尊重用户已清空内容的意图），
    /// 空 bar 会显示「右键添加模块」提示且右键可用。
    /// </summary>
    public void EnsureAtLeastOneBar()
    {
        if (Windows.Count > 0) return;
        AddToAllDocks(new WindowConfig
        {
            Name = Framework.Lang.T("common.home"),
            Type = WindowType.Native,
            Panels = new List<PanelItem>()
        });
    }

    /// <summary>
    /// dock 成员自愈：把 ShowInTabs=true 但不在 dock 里的窗口按 Windows 顺序补回两侧 dock。
    /// dock 状态（local_app_state）与窗口表（local_bar）是两份独立持久化：历史上「以内存为准」的
    /// 全量重写在内存为空时执行，会把 dock 写坏成只剩兜底窗口，而窗口表随后被写回/晋升补齐，
    /// 二者脱节后设置页（读 Windows）能看到窗口、主窗口标签栏（读 dock）却只剩一个。
    /// 以 ShowInTabs 为据不会复活用户主动隐藏的窗口（隐藏时 ShowInTabs=false 且已移出 dock）；
    /// 只补末尾不重建，保留用户拖拽调整过的既有标签顺序。仅修内存，随后续正常 Save 自然落库。
    /// </summary>
    public void RepairDockMembership()
    {
        bool changed = false;

        // 孤儿 id 清理：dock 里指向已不存在窗口的 id（删除/晋升剪枝硬删行时未同步清理 dock state，
        // 孤儿随 dock 晋升进入账号作用域）。孤儿占据索引会造成序号断层（标签只剩 2..N）
        // 且 ActiveIndex 落空无高亮、徽章指向不存在的项；清理后越界的 ActiveIndex 回落到末尾。
        foreach (var dock in new[] { LeftDock, RightDock })
        {
            int before = dock.WindowIds.Count;
            dock.WindowIds.RemoveAll(id => GetById(id) == null);
            if (dock.WindowIds.Count != before) changed = true;
            if (dock.ActiveIndex >= dock.WindowIds.Count)
                dock.ActiveIndex = Math.Max(0, dock.WindowIds.Count - 1);
        }

        foreach (var win in Windows)
        {
            if (!win.ShowInTabs) continue;
            if (!LeftDock.WindowIds.Contains(win.Id)) { LeftDock.WindowIds.Add(win.Id); changed = true; }
            if (!RightDock.WindowIds.Contains(win.Id)) { RightDock.WindowIds.Add(win.Id); changed = true; }
        }
        if (changed)
            System.Diagnostics.Debug.WriteLine($"[WindowConfig] dock membership repaired: {LeftDock.WindowIds.Count} left / {RightDock.WindowIds.Count} right");
    }
}
