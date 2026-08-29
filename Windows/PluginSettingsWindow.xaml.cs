using System.Windows;
using SideDesk.Services.Plugins;

namespace SideDesk.Windows;

/// <summary>
/// HTML 模式插件设置窗口（以 WebViewWindow 为模板）：
/// 以 HtmlPluginHost 沙箱装载插件的 settings.html，配置读写经 window.sideDesk 桥落盘到插件私有存储。
/// </summary>
public partial class PluginSettingsWindow : Window
{
    private readonly HtmlPluginHost _host;

    /// <summary>
    /// 创建插件设置窗口
    /// </summary>
    /// <param name="config">插件配置（须含有效的 settingsHtml 与 SourceDirectory）</param>
    public PluginSettingsWindow(PluginConfig config)
    {
        InitializeComponent();
        Title = $"{config.Icon} {config.Name} · {Framework.Lang.T("settings.pluginSettings")}";

        _host = new HtmlPluginHost(config, config.SettingsHtml!, config.SourceDirectory!);
        hostContainer.Children.Add(_host);

        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Closed += (s, e) => _host.Dispose();
    }
}
