using System.Windows;
using SideDesk.Services.Plugins;

namespace SystemCleaner;

/// <summary>
/// SystemCleaner 插件入口
/// 实现 ISideDeskPlugin 接口，提供系统内存/CPU 监控和内存清理功能
/// </summary>
public class SystemCleanerPlugin : ISideDeskPlugin
{
    public string Id => "system-cleaner";
    public string Name => "系统清理";
    public string Version => "1.0.0";

    public UIElement CreateUI()
    {
        return new SystemCleanerControl();
    }

    public void OnLoaded()
    {
        System.Diagnostics.Debug.WriteLine("[SystemCleaner] 插件已加载");
    }

    public void OnUnloaded()
    {
        System.Diagnostics.Debug.WriteLine("[SystemCleaner] 插件已卸载");
    }
}
