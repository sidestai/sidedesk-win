using Newtonsoft.Json;

namespace SideDesk.Services.Plugins;

/// <summary>
/// 插件 JSON 配置文件模型（对应 plugin.json）
/// </summary>
public class PluginConfig
{
    /// <summary>插件唯一标识</summary>
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    /// <summary>插件显示名称</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>插件版本</summary>
    [JsonProperty("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>插件描述</summary>
    [JsonProperty("description")]
    public string Description { get; set; } = "";

    /// <summary>作者</summary>
    [JsonProperty("author")]
    public string Author { get; set; } = "";

    /// <summary>入口 DLL 文件名（相对于插件目录）</summary>
    [JsonProperty("entry")]
    public string Entry { get; set; } = "";

    /// <summary>实现 ISideDeskPlugin 的完整类型名（命名空间.类名）</summary>
    [JsonProperty("pluginClass")]
    public string PluginClass { get; set; } = "";

    /// <summary>可选：松散 XAML 文件路径（相对于插件目录，若提供则优先用 XamlReader 加载 UI）</summary>
    [JsonProperty("xaml")]
    public string? Xaml { get; set; }

    /// <summary>插件图标（emoji 或图片路径）</summary>
    [JsonProperty("icon")]
    public string Icon { get; set; } = "🧩";

    /// <summary>最低宿主版本要求</summary>
    [JsonProperty("minHostVersion")]
    public string? MinHostVersion { get; set; }

    /// <summary>是否启用</summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>加载模式："native"（默认，DLL 插件）或 "html"（HTML 组件模式，经 WebView2 沙箱加载）</summary>
    [JsonProperty("mode")]
    public string Mode { get; set; } = "native";

    /// <summary>HTML 模式的模块页文件（相对于插件目录）</summary>
    [JsonProperty("moduleHtml")]
    public string? ModuleHtml { get; set; }

    /// <summary>HTML 模式的设置页文件（可选，相对于插件目录；提供后设置页出现「设置」入口）</summary>
    [JsonProperty("settingsHtml")]
    public string? SettingsHtml { get; set; }

    /// <summary>声明式权限列表（预留字段，当前版本不做强制网关）</summary>
    [JsonProperty("permissions")]
    public List<string> Permissions { get; set; } = new();

    /// <summary>插件依赖的宿主 API 版本（当前宿主支持 1；未知版本拒绝加载）</summary>
    [JsonProperty("apiVersion")]
    public int ApiVersion { get; set; } = 1;

    /// <summary>插件所在目录（扫描时填充，不参与序列化）</summary>
    [JsonIgnore]
    public string? SourceDirectory { get; set; }
}

/// <summary>
/// 已加载的插件实例信息
/// </summary>
public class LoadedPlugin
{
    public PluginConfig Config { get; set; } = null!;
    public ISideDeskPlugin Instance { get; set; } = null!;
    public string PluginDirectory { get; set; } = "";
    /// <summary>加载上下文（用于卸载时释放 DLL 文件锁）</summary>
    internal System.Runtime.Loader.AssemblyLoadContext? LoadContext { get; set; }
    /// <summary>追踪由此插件创建的 UI 元素（卸载时需从可视化树移除）</summary>
    internal List<WeakReference<System.Windows.UIElement>> CreatedUIElements { get; } = new();
}
