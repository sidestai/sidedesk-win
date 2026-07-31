using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Newtonsoft.Json;

namespace SideDesk.Services.Plugins;

/// <summary>
/// 插件加载与管理服务
/// 扫描 plugins 目录，读取 plugin.json 配置，加载 DLL 并实例化插件
/// </summary>
public class PluginLoaderService
{
    private static readonly Lazy<PluginLoaderService> _instance = new(() => new PluginLoaderService());
    public static PluginLoaderService Instance => _instance.Value;

    private readonly List<LoadedPlugin> _plugins = new();
    private readonly Dictionary<string, LoadedPlugin> _pluginMap = new();

    /// <summary>所有已成功加载的插件</summary>
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>插件根目录（exe 同级 plugins 文件夹）</summary>
    public string PluginsDirectory { get; }

    private PluginLoaderService()
    {
        // 插件目录：优先使用 exe 同级 plugins，其次 AppData
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        PluginsDirectory = Path.Combine(exeDir, "plugins");
        if (!Directory.Exists(PluginsDirectory))
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDesk", "plugins");
            if (Directory.Exists(appData))
                PluginsDirectory = appData;
        }
    }

    /// <summary>
    /// 扫描并加载所有插件（应用启动时调用）
    /// </summary>
    /// <param name="disabledIds">用户手动禁用的插件 ID 集合（来自 Settings.DisabledPlugins）</param>
    public void LoadAllPlugins(HashSet<string>? disabledIds = null)
    {
        _plugins.Clear();
        _pluginMap.Clear();

        if (!Directory.Exists(PluginsDirectory))
        {
            Debug.WriteLine($"[PluginLoader] 插件目录不存在: {PluginsDirectory}");
            return;
        }

        foreach (var dir in Directory.GetDirectories(PluginsDirectory))
        {
            try
            {
                var plugin = LoadPluginFromDirectory(dir);
                if (plugin != null)
                {
                    // 跳过用户手动禁用的插件
                    if (disabledIds != null && disabledIds.Contains(plugin.Config.Id))
                    {
                        Debug.WriteLine($"[PluginLoader] 插件已被用户禁用，跳过: {plugin.Config.Name}");
                        continue;
                    }
                    _plugins.Add(plugin);
                    _pluginMap[plugin.Config.Id] = plugin;
                    Debug.WriteLine($"[PluginLoader] 已加载插件: {plugin.Config.Name} v{plugin.Config.Version}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginLoader] 加载插件失败 ({dir}): {ex.Message}");
            }
        }

        Debug.WriteLine($"[PluginLoader] 共加载 {_plugins.Count} 个插件");
    }

    /// <summary>
    /// 扫描插件目录下所有插件的配置（不加载 DLL，供设置页面展示用）
    /// </summary>
    public List<PluginConfig> ScanAllPluginConfigs()
    {
        var configs = new List<PluginConfig>();
        if (!Directory.Exists(PluginsDirectory)) return configs;

        foreach (var dir in Directory.GetDirectories(PluginsDirectory))
        {
            try
            {
                var configPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(configPath)) continue;
                var json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<PluginConfig>(json);
                if (config != null && !string.IsNullOrEmpty(config.Id))
                {
                    config.SourceDirectory = dir;
                    configs.Add(config);
                }
            }
            catch { /* 忽略解析失败的插件 */ }
        }
        return configs;
    }

    /// <summary>
    /// 从指定目录加载单个插件并注册（供插件编辑器编译后测试用）
    /// 若同 ID 插件已加载则先卸载
    /// </summary>
    public LoadedPlugin? LoadDevPlugin(string dir)
    {
        // 先读取配置获取 ID，卸载同名旧插件
        var configPath = Path.Combine(dir, "plugin.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var cfg = JsonConvert.DeserializeObject<PluginConfig>(json);
                if (cfg != null && !string.IsNullOrEmpty(cfg.Id))
                    UnloadPlugin(cfg.Id);
            }
            catch { /* ignore */ }
        }

        var plugin = LoadPluginFromDirectory(dir);
        if (plugin != null)
        {
            _plugins.Add(plugin);
            _pluginMap[plugin.Config.Id] = plugin;
            Debug.WriteLine($"[PluginLoader] 已加载开发插件: {plugin.Config.Name} v{plugin.Config.Version}");
        }
        return plugin;
    }

    /// <summary>
    /// 从指定目录加载单个插件
    /// </summary>
    private LoadedPlugin? LoadPluginFromDirectory(string dir)
    {
        var configPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(configPath))
            return null;

        var json = File.ReadAllText(configPath);
        var config = JsonConvert.DeserializeObject<PluginConfig>(json);
        if (config == null || string.IsNullOrEmpty(config.Id) || !config.Enabled)
            return null;

        // 如果已存在同 ID 插件，跳过
        if (_pluginMap.ContainsKey(config.Id))
            return null;

        ISideDeskPlugin? instance = null;

        // 方式1：通过 DLL 入口加载
        if (!string.IsNullOrEmpty(config.Entry))
        {
            var dllPath = Path.Combine(dir, config.Entry);
            if (!File.Exists(dllPath))
            {
                Debug.WriteLine($"[PluginLoader] DLL 不存在: {dllPath}");
                return null;
            }

            var loadContext = new PluginAssemblyLoadContext(dir);
            // 使用 LoadFromStream 代替 LoadFromAssemblyPath：
            // LoadFromAssemblyPath 会对 DLL 建立内存映射（文件锁），即使 Unload 后锁释放时机也不可控；
            // LoadFromStream 从字节流加载，DLL 文件不会被锁定，随时可重新编译覆盖
            var dllBytes = File.ReadAllBytes(dllPath);
            var assembly = loadContext.LoadFromStream(new MemoryStream(dllBytes));

            // 查找实现 ISideDeskPlugin 的类型
            Type? pluginType = null;
            if (!string.IsNullOrEmpty(config.PluginClass))
            {
                pluginType = assembly.GetType(config.PluginClass);
            }
            pluginType ??= assembly.GetTypes()
                .FirstOrDefault(t => typeof(ISideDeskPlugin).IsAssignableFrom(t)
                                     && !t.IsInterface && !t.IsAbstract);

            if (pluginType == null)
            {
                Debug.WriteLine($"[PluginLoader] 未找到 ISideDeskPlugin 实现: {dllPath}");
                loadContext.Unload();
                return null;
            }

            instance = Activator.CreateInstance(pluginType) as ISideDeskPlugin;

            if (instance == null)
            {
                loadContext.Unload();
                return null;
            }

            instance.OnLoaded();

            return new LoadedPlugin
            {
                Config = config,
                Instance = instance,
                PluginDirectory = dir,
                LoadContext = loadContext
            };
        }

        return null;
    }

    /// <summary>
    /// 根据插件 ID 获取已加载的插件
    /// </summary>
    public LoadedPlugin? GetPlugin(string pluginId)
    {
        _pluginMap.TryGetValue(pluginId, out var plugin);
        return plugin;
    }

    /// <summary>
    /// 为指定插件创建 UI 元素
    /// 支持两种模式：DLL 内编译 XAML（CreateUI）或松散 XAML 文件（XamlReader）
    /// </summary>
    public UIElement? CreatePluginUI(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin == null) return null;

        try
        {
            UIElement? ui = null;

            // 优先使用松散 XAML（若配置了）
            if (!string.IsNullOrEmpty(plugin.Config.Xaml))
            {
                var xamlPath = Path.Combine(plugin.PluginDirectory, plugin.Config.Xaml);
                if (File.Exists(xamlPath))
                {
                    var xamlText = File.ReadAllText(xamlPath);
                    // 自动补全缺失的 xmlns:x 声明（避免 "'x' is an undeclared prefix" 错误）
                    if (!xamlText.Contains("xmlns:x"))
                    {
                        var insertIdx = xamlText.IndexOf("xmlns", StringComparison.Ordinal);
                        if (insertIdx >= 0)
                        {
                            var quoteStart = xamlText.IndexOf('"', insertIdx);
                            var quoteEnd = xamlText.IndexOf('"', quoteStart + 1);
                            if (quoteEnd > 0)
                                xamlText = xamlText.Insert(quoteEnd + 1,
                                    " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
                        }
                    }
                    ui = XamlReader.Parse(xamlText) as UIElement;
                    if (ui != null && ui is FrameworkElement fe)
                        fe.DataContext = plugin.Instance;
                }
            }

            // 回退：调用插件的 CreateUI()
            ui ??= plugin.Instance.CreateUI();

            // 追踪创建的 UI 元素（卸载时需从可视化树移除）
            if (ui != null)
                plugin.CreatedUIElements.Add(new WeakReference<UIElement>(ui));

            return ui;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginLoader] 创建插件 UI 失败 ({pluginId}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 卸载指定插件（释放 DLL 文件锁，供重新编译前调用）
    /// 会先从可视化树移除插件 UI，断开 WPF 对插件实例的强引用
    /// </summary>
    public void UnloadPlugin(string pluginId)
    {
        if (!_pluginMap.TryGetValue(pluginId, out var plugin))
            return;

        // 1. 从可视化树移除插件创建的所有 UI 元素
        DetachPluginUI(plugin);

        // 2. 通知插件卸载
        try { plugin.Instance.OnUnloaded(); }
        catch { /* ignore */ }

        // 3. 卸载程序集加载上下文
        plugin.LoadContext?.Unload();

        _plugins.Remove(plugin);
        _pluginMap.Remove(pluginId);
        Debug.WriteLine($"[PluginLoader] 已卸载插件: {pluginId}");
    }

    /// <summary>
    /// 从可视化树移除插件创建的所有 UI 元素，断开 WPF 强引用
    /// </summary>
    private void DetachPluginUI(LoadedPlugin plugin)
    {
        foreach (var weakRef in plugin.CreatedUIElements)
        {
            if (!weakRef.TryGetTarget(out var ui)) continue;

            // 清除 DataContext（断开对插件实例的绑定引用）
            if (ui is FrameworkElement fe)
                fe.DataContext = null;

            // 从父容器移除
            if (VisualTreeHelper.GetParent(ui) is Panel parentPanel)
                parentPanel.Children.Remove(ui);
            else if (VisualTreeHelper.GetParent(ui) is ContentControl contentControl)
                contentControl.Content = null;
            else if (VisualTreeHelper.GetParent(ui) is Decorator decorator)
                decorator.Child = null;
        }
        plugin.CreatedUIElements.Clear();
    }

    /// <summary>
    /// 卸载所有插件
    /// </summary>
    public void UnloadAll()
    {
        foreach (var plugin in _plugins)
        {
            try { plugin.Instance.OnUnloaded(); }
            catch { /* ignore */ }
            plugin.LoadContext?.Unload();
        }
        _plugins.Clear();
        _pluginMap.Clear();
    }
}

/// <summary>
/// 插件专用 AssemblyLoadContext，支持从插件目录解析依赖
/// </summary>
internal class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDir;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginDir) : base(isCollectible: true)
    {
        _pluginDir = pluginDir;
        // 尝试查找插件目录下的 deps.json
        var depsJson = Directory.GetFiles(pluginDir, "*.deps.json").FirstOrDefault();
        _resolver = depsJson != null ? new AssemblyDependencyResolver(depsJson) : null!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 优先从宿主已加载的程序集中解析（共享框架）
        var hostAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
        if (hostAssembly != null)
            return hostAssembly;

        // 通过 deps.json 解析
        if (_resolver != null)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
                return LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
        }

        // 回退：在插件目录中查找同名 DLL
        var dllPath = Path.Combine(_pluginDir, $"{assemblyName.Name}.dll");
        if (File.Exists(dllPath))
            return LoadFromStream(new MemoryStream(File.ReadAllBytes(dllPath)));

        return null;
    }
}
