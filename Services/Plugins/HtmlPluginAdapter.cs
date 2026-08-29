using System.Collections.Generic;
using System.Windows;

namespace SideDesk.Services.Plugins;

/// <summary>
/// HTML 组件模式插件的适配器：把 HtmlPluginHost 包装成 ISideDeskPlugin，
/// 使安装/市场/挂载/启停/卸载全部复用现有 DLL 插件链路（零改动）。
/// </summary>
public class HtmlPluginAdapter : ISideDeskPlugin
{
    private readonly PluginConfig _config;
    private readonly string _pluginDirectory;
    private readonly List<HtmlPluginHost> _hosts = new();

    public HtmlPluginAdapter(PluginConfig config, string pluginDirectory)
    {
        _config = config;
        _pluginDirectory = pluginDirectory;
    }

    public string Id => _config.Id;

    public string Name => _config.Name;

    public string Version => _config.Version;

    /// <summary>
    /// 创建模块页 UI：每次调用返回一个独立的 HtmlPluginHost 实例
    /// （同一插件可被添加到多个面板/多次，互不共享状态，状态经 getConfig/setConfig 持久化）
    /// </summary>
    public UIElement CreateUI()
    {
        var host = new HtmlPluginHost(_config, _config.ModuleHtml ?? "module.html", _pluginDirectory);
        _hosts.Add(host);
        return host;
    }

    public void OnUnloaded()
    {
        foreach (var host in _hosts)
        {
            try { host.Dispose(); }
            catch { /* ignore */ }
        }
        _hosts.Clear();
    }
}
