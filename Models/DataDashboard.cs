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
    public string TargetUrl { get; set; } = "";      // 目标网址（API 模式时为请求 URL）
    public string Selector { get; set; } = "";       // XPath 或 CSS Selector
    public string SelectorType { get; set; } = "";   // 选择器类型：xpath/css/regex/js（空=旧版自动识别）
    public int RefreshIntervalSeconds { get; set; } = 60; // 刷新间隔（秒）

    // API 请求模式（无模板自定义）：ApiMethod 非空时直接 HTTP 取数，不经浏览器
    public string? ApiMethod { get; set; }           // GET / POST（null/空 = 浏览器+选择器模式）
    public string? ApiBody { get; set; }             // POST 请求内容
    public string? ResponseExtract { get; set; }     // response 提取：JSON 路径（如 data.count）；留空=整个响应

    // 团队归属与变量
    public string? TeamId { get; set; }              // 所属团队ID（null/空=个人）
    public string? TeamName { get; set; }            // 团队名称快照（显示用）
    public string? VarKey { get; set; }              // 变量名（如 qcoder.count，团队/个人内唯一，供其他地方条件判断/显示引用）

    // 引用展示（非空=仅展示同变量名运行中数据项的值，自身不参与轮询）
    public string? RefVarKey { get; set; }           // 引用的变量名（指向实际轮询的数据项）

    // 样式配置
    public string LabelColor { get; set; } = "";         // 标题文字颜色（空=默认）
    public double LabelFontSize { get; set; } = 0;       // 标题字号（0=默认11）
    public bool LabelBold { get; set; } = false;          // 标题是否粗体
    public string ValueColor { get; set; } = "";         // 数据字体颜色（空=默认）
    public double ValueFontSize { get; set; } = 0;       // 数据字号（0=默认16）
    public bool ValueBold { get; set; } = false;          // 数据是否粗体
    public string HorizontalAlignment { get; set; } = ""; // 水平对齐：Left/Center/Right（空=默认Left）
    public string? ValueFormat { get; set; }          // 值格式（可选）：{0} 或「变量」代表值，如 "你好：{0}"；留空=直接显示值
}

/// <summary>
/// 变量集：将多个运行中的变量（显式勾选 + 通配符匹配）按求和/求平均/最大/最小/计数聚合成一个新变量。
/// 变量集名即变量名（如 qoder.count_all），可像普通变量一样被条件判断/显示引用。
/// </summary>
public class VariableSet
{
    public string VarKey { get; set; } = "";               // 变量集名（作用域内唯一，如 qoder.count_all）
    public string Op { get; set; } = "SUM";                // 聚合逻辑：SUM 求和 / AVG 求平均 / MAX 最大 / MIN 最小 / COUNT 计数
    public List<string> SourceKeys { get; set; } = new();  // 显式选择的变量名列表
    public string Pattern { get; set; } = "";              // 通配符（如 qoder.count*，* 匹配任意字符）；空=不启用通配
    public string? TeamId { get; set; }                    // 团队ID加密串（空=个人作用域）
    public string? TeamName { get; set; }                  // 团队显示名（仅本地展示用，不参与同步）
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
    public string SelectorType { get; set; } = "";   // 选择器类型：xpath/css/regex/js（空=旧版自动识别）
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

    // 内置模板删除黑名单：内置文件随版本部署恢复，删除记录单独持久化
    private static readonly string DeletedBuiltinsFile = Path.Combine(TemplateDir, "deleted-builtins.json");

    public List<DataTemplate> Templates { get; set; } = new();

    private static List<string> LoadDeletedIds()
    {
        try
        {
            if (File.Exists(DeletedBuiltinsFile))
                return JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(DeletedBuiltinsFile)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>记录删除（对内置模板生效；用户模板直接改 user-templates.json，同时入黑名单无害）</summary>
    public static void MarkDeleted(string templateId)
    {
        var ids = LoadDeletedIds();
        if (ids.Contains(templateId)) return;
        ids.Add(templateId);
        if (!Directory.Exists(TemplateDir)) Directory.CreateDirectory(TemplateDir);
        File.WriteAllText(DeletedBuiltinsFile, JsonConvert.SerializeObject(ids, Formatting.Indented));
    }

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
                System.Diagnostics.Debug.WriteLine($"[DataTemplate] failed to load template file {file}: {ex.Message}");
            }
        }

        // 过滤已删除的内置模板（deleted-builtins.json 不是模板列表，反序列化会失败被跳过，不影响）
        var deleted = LoadDeletedIds();
        if (deleted.Count > 0)
            manager.Templates.RemoveAll(t => deleted.Contains(t.Id));

        return manager;
    }

    /// <summary>
    /// 仅加载用户自建模板（user-templates.json），不含内置模板
    /// </summary>
    public static List<DataTemplate> LoadUserTemplates()
    {
        var userFile = Path.Combine(TemplateDir, "user-templates.json");
        if (!File.Exists(userFile)) return new();
        try { return JsonConvert.DeserializeObject<List<DataTemplate>>(File.ReadAllText(userFile)) ?? new(); }
        catch { return new(); }
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

        // 重新保存视为恢复：从删除黑名单中移除
        var deleted = LoadDeletedIds();
        if (deleted.Remove(template.Id))
            File.WriteAllText(DeletedBuiltinsFile, JsonConvert.SerializeObject(deleted, Formatting.Indented));

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
