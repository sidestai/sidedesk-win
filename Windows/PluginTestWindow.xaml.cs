using System.Windows;
using System.Windows.Controls;
using SideDesk.Services.Plugins;

namespace SideDesk.Windows;

/// <summary>
/// 插件测试运行窗口：加载指定插件的 UI 并展示效果
/// </summary>
public partial class PluginTestWindow : Window
{
    private readonly string _pluginId;

    public PluginTestWindow(string pluginId)
    {
        InitializeComponent();
        _pluginId = pluginId;
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += PluginTestWindow_Loaded;
        Closed += PluginTestWindow_Closed;
    }

    private void PluginTestWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadPluginUI();
    }

    private void LoadPluginUI()
    {
        var loader = PluginLoaderService.Instance;
        var plugin = loader.GetPlugin(_pluginId);

        if (plugin == null)
        {
            txtPlaceholder.Text = Framework.Lang.TF("market.pluginNotLoadedPlease", _pluginId);
            return;
        }

        txtTitle.Text = $"🧪 {plugin.Config.Icon} {plugin.Config.Name} v{plugin.Config.Version}";

        var ui = loader.CreatePluginUI(_pluginId);
        if (ui == null)
        {
            txtPlaceholder.Text = Framework.Lang.T("market.createPluginUiFailed");
            return;
        }

        // 移除占位文字，嵌入插件 UI
        pluginContainer.Child = ui;
    }

    /// <summary>
    /// 关闭窗口时卸载插件 DLL，释放文件锁，避免下次编译失败
    /// </summary>
    private void PluginTestWindow_Closed(object? sender, EventArgs e)
    {
        // 先清空容器，断开可视化树引用
        pluginContainer.Child = null;

        // 卸载插件（DetachPluginUI + AssemblyLoadContext.Unload）
        PluginLoaderService.Instance.UnloadPlugin(_pluginId);

        // 触发 GC 加速释放 DLL 文件锁
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
