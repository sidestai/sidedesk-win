using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideDesk
{
    /// <summary>
    /// 设置-插件页-HTML 组件模式插件支持：
    /// 为提供 settingsHtml 的插件卡片追加「设置」按钮，打开 PluginSettingsWindow 装载 settings.html。
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>
        /// 为插件卡片创建「设置」按钮；非 HTML 模式或未提供有效 settingsHtml 时返回 null。
        /// </summary>
        private Button? CreatePluginSettingsButton(Services.Plugins.PluginConfig cfg)
        {
            if (!string.Equals(cfg.Mode, "html", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrEmpty(cfg.SettingsHtml)) return null;
            if (string.IsNullOrEmpty(cfg.SourceDirectory)
                || !File.Exists(Path.Combine(cfg.SourceDirectory, cfg.SettingsHtml)))
                return null;

            var btn = new Button
            {
                Content = Framework.Lang.T("settings.pluginSettings"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("settings.pluginSettingsHint"),
                Tag = cfg
            };
            btn.Click += PluginSettings_Click;
            return btn;
        }

        /// <summary>打开插件设置窗口（HtmlPluginHost 装载 settings.html，读写经 window.sideDesk 桥）</summary>
        private void PluginSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Services.Plugins.PluginConfig cfg) return;
            if (string.IsNullOrEmpty(cfg.SourceDirectory) || string.IsNullOrEmpty(cfg.SettingsHtml)) return;

            var window = new Windows.PluginSettingsWindow(cfg);
            window.Owner = this;
            window.Show();
        }
    }
}
