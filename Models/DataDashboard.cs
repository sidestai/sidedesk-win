using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SideDesk.Models;

/// <summary>
/// 数据看板条目
/// </summary>
public class DataDashboardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Label { get; set; } = "";          // 数据项文字（显示标签）
    public string Value { get; set; } = "--";        // 当前抓取到的值
    public string TemplateId { get; set; } = "";     // 关联模板ID（空=自定义）
    // 自定义配置（模板为空时使用，或覆盖模板默认值）
    public string? BrowserId { get; set; }           // 选择的浏览器ID（null=默认第一个）
    public string? ProfileId { get; set; }           // 选择的Profile ID
    public string TargetUrl { get; set; } = "";      // 目标网址
    public string Selector { get; set; } = "";       // XPath 或 CSS Selector
    public int RefreshIntervalSeconds { get; set; } = 60; // 刷新间隔（秒）

    // 团队归属与变量
    public string? TeamId { get; set; }              // 所属团队ID（null/空=个人）
    public string? TeamName { get; set; }            // 团队名称快照（显示用）
    public string? VarKey { get; set; }              // 变量名（如 qcoder.count，团队/个人内唯一，供其他地方条件判断/显示引用）

    // 样式配置
    public string LabelColor { get; set; } = "";         // 标题文字颜色（空=默认）
    public double LabelFontSize { get; set; } = 0;       // 标题字号（0=默认11）
    public bool LabelBold { get; set; } = false;          // 标题是否粗体
    public string ValueColor { get; set; } = "";         // 数据字体颜色（空=默认）
    public double ValueFontSize { get; set; } = 0;       // 数据字号（0=默认16）
    public bool ValueBold { get; set; } = false;          // 数据是否粗体
    public string HorizontalAlignment { get; set; } = ""; // 水平对齐：Left/Center/Right（空=默认Left）
}

/// <summary>
/// 数据模板定义（预定义或用户自建）
/// </summary>
public class DataTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";           // 模板名称
    public string Description { get; set; } = "";    // 描述
    public string Category { get; set; } = "";       // 分类（如"社交媒体","开发者"）
    public string TargetUrl { get; set; } = "";      // 目标网址（可含占位符如 {owner}）
    public string Selector { get; set; } = "";       // XPath 或 CSS Selector
    public int RefreshIntervalSeconds { get; set; } = 60;
    public string? DefaultBrowserId { get; set; }    // 可选默认浏览器
    public string? DefaultProfileId { get; set; }     // 可选默认Profile
    public string? TeamId { get; set; }              // 所属团队ID（null=个人）
    public string? TeamName { get; set; }            // 团队名称快照（显示用）
}

/// <summary>
/// 数据模板管理器 - 从 data-templates/ 目录加载模板
/// </summary>
public class DataTemplateManager
{
    private static readonly string TemplateDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "data-templates");
    
    public List<DataTemplate> Templates { get; set; } = new();

    /// <summary>
    /// 从 data-templates/ 目录加载所有 .json 模板文件
    /// </summary>
    public static DataTemplateManager Load()
    {
        var manager = new DataTemplateManager();
        
        if (!Directory.Exists(TemplateDir))
        {
            Directory.CreateDirectory(TemplateDir);
            return manager;
        }

        foreach (var file in Directory.GetFiles(TemplateDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var templates = JsonConvert.DeserializeObject<List<DataTemplate>>(json);
                if (templates != null)
                    manager.Templates.AddRange(templates);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataTemplate] 加载模板文件失败 {file}: {ex.Message}");
            }
        }

        return manager;
    }

    /// <summary>
    /// 保存用户自建模板到 user-templates.json
    /// </summary>
    public void SaveUserTemplate(DataTemplate template)
    {
        if (!Directory.Exists(TemplateDir))
            Directory.CreateDirectory(TemplateDir);

        var userFile = Path.Combine(TemplateDir, "user-templates.json");
        List<DataTemplate> userTemplates = new();

        if (File.Exists(userFile))
        {
            try
            {
                var json = File.ReadAllText(userFile);
                userTemplates = JsonConvert.DeserializeObject<List<DataTemplate>>(json) ?? new();
            }
            catch { }
        }

        // 更新或添加
        var existing = userTemplates.FindIndex(t => t.Id == template.Id);
        if (existing >= 0)
            userTemplates[existing] = template;
        else
            userTemplates.Add(template);

        File.WriteAllText(userFile, JsonConvert.SerializeObject(userTemplates, Formatting.Indented));

        // 刷新内存模板列表
        if (!Templates.Any(t => t.Id == template.Id))
            Templates.Add(template);
        else
        {
            var idx = Templates.FindIndex(t => t.Id == template.Id);
            if (idx >= 0) Templates[idx] = template;
        }
    }
}
