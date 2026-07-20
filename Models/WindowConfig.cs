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
    DailyActivity    // 每日活跃
}

public class PanelItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public PanelType Type { get; set; }
    public string? GroupId { get; set; }  // 仅 QuickGroup 类型使用
    public string? Title { get; set; }    // 可选自定义标题
    public List<ClipboardSavedItem>? ClipboardItems { get; set; } // 剪贴板收藏数据
    public List<FileAreaItem>? FileItems { get; set; } // 文件区数据
    public List<DataDashboardItem>? DashboardItems { get; set; } // 数据看板项
    public List<TimerItem>? TimerItems { get; set; } // 计时器数据
    public List<PanelItem>? ChildPanels { get; set; } // 分组子面板
    public bool IsExpanded { get; set; } = true;      // 分组是否展开
    public List<StickyNote>? StickyNotes { get; set; } // 便签数据（per-panel）
    public List<TodoItem>? TodoItems { get; set; } // 今日待办数据（per-panel）
    public bool SkipWrapper { get; set; } = false;     // 是否跳过外层容器包装
    public bool HideFrame { get; set; } = false;       // 单个模块独立隐藏外框（边框/标题/背景/内边距）
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
    public string? CarouselFolder { get; set; }                  // 图片轮播：图片所在目录
    public int CarouselIntervalSeconds { get; set; } = 5;        // 图片轮播：切换间隔（秒）
    public bool CarouselRandom { get; set; } = false;            // 图片轮播：随机播放（否则顺序）
    public bool CarouselIncludeSubfolders { get; set; } = false; // 图片轮播：包含子目录
    public string CarouselStretch { get; set; } = "Uniform";     // 图片轮播：填充方式 Uniform/UniformToFill/Fill
    public double CarouselHeight { get; set; } = 140;            // 图片轮播：显示区高度（px）
    public bool CarouselAutoPlay { get; set; } = true;           // 图片轮播：自动播放
}

public class FileAreaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Path { get; set; } = "";      // 文件或文件夹完整路径
    public string Name { get; set; } = "";      // 显示名称
    public bool IsFolder { get; set; }          // 是否为文件夹
    public DateTime AddedAt { get; set; } = DateTime.Now;
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

public class WindowConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新窗口";
    public WindowType Type { get; set; } = WindowType.Native;
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<PanelItem> Panels { get; set; } = new();
    public bool IsEditMode { get; set; } = false;
    public bool IsAppMode { get; set; } = false;
    /// <summary>窗口级启用的工具列表（显示在底部工具栏）</summary>
    public List<string> EnabledTools { get; set; } = new();
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

    public static WindowConfigManager Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonConvert.DeserializeObject<WindowConfigManager>(json) ?? new WindowConfigManager();
            }
        }
        catch { }
        return new WindowConfigManager();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }
        catch { }
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
