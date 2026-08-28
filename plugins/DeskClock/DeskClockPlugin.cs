using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SideDesk.Services.Plugins;

namespace DeskClock;

/// <summary>
/// 桌面时钟插件入口（插件市场付费示例）
/// 纯代码构建 UI：大字体时钟 + 日期显示
/// </summary>
public class DeskClockPlugin : ISideDeskPlugin
{
    public string Id => "desk-clock";
    public string Name => "桌面时钟";
    public string Version => "1.0.0";

    private DispatcherTimer? _timer;
    private TextBlock? _timeText;
    private TextBlock? _dateText;

    public UIElement CreateUI()
    {
        _timeText = new TextBlock
        {
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Consolas")
        };
        _dateText = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(_timeText);
        panel.Children.Add(_dateText);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTime();
        _timer.Start();
        RefreshTime();

        return panel;
    }

    private void RefreshTime()
    {
        var now = DateTime.Now;
        if (_timeText != null) _timeText.Text = now.ToString("HH:mm:ss");
        if (_dateText != null) _dateText.Text = now.ToString("D"); // 长日期格式，随 Lang.ApplyCulture 设置的线程文化本地化
    }

    public void OnLoaded()
    {
        System.Diagnostics.Debug.WriteLine("[DeskClock] plugin loaded");
    }

    public void OnUnloaded()
    {
        _timer?.Stop();
        _timer = null;
        System.Diagnostics.Debug.WriteLine("[DeskClock] plugin unloaded");
    }
}
