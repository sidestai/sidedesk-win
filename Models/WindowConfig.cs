using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models;

public enum WindowType { Native, WebView }

public enum PanelType
{
    QuickGroup,      // 快捷分组
    StickyNotes,     // 便签
    Clipboard,       // 剪贴板历史
    SystemMonitor,   // 系统信息/计数器
    UsageStats,      // 使用统计
    AIChat,          // AI 对话
    FileArea         // 文件区
}

public class PanelItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public PanelType Type { get; set; }
    public string? GroupId { get; set; }  // 仅 QuickGroup 类型使用
    public string? Title { get; set; }    // 可选自定义标题
    public List<ClipboardSavedItem>? ClipboardItems { get; set; } // 剪贴板收藏数据
    public List<FileAreaItem>? FileItems { get; set; } // 文件区数据
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

public class WindowConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新窗口";
    public WindowType Type { get; set; } = WindowType.Native;
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<PanelItem> Panels { get; set; } = new();
    public bool IsEditMode { get; set; } = false;
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
