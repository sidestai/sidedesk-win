using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SideDesk.Services.Plugins;

namespace HelloWorld;

/// <summary>
/// 问候卡片插件入口（插件市场开源示例）
/// UI 由松散 XAML（ui.xaml）提供，本类作为 DataContext 提供数据与命令。
/// 拉取源码后可在插件编辑器中直接修改编译。
/// </summary>
public class HelloWorldPlugin : ISideDeskPlugin, INotifyPropertyChanged
{
    public string Id => "hello-world";
    public string Name => "问候卡片";
    public string Version => "0.1.0";

    private static readonly string[] Greetings =
    {
        "你好，元桌！",
        "今天也要加油鸭～",
        "写插件其实很简单！",
        "改改这段文案，变成你的插件吧"
    };

    private int _index;
    private string _greeting = Greetings[0];

    /// <summary>显示给用户的问候语（绑定到 ui.xaml）</summary>
    public string Greeting
    {
        get => _greeting;
        private set { _greeting = value; OnPropertyChanged(); }
    }

    /// <summary>换一句命令（绑定到 ui.xaml 的按钮）</summary>
    public ICommand NextCommand { get; }

    public HelloWorldPlugin()
    {
        NextCommand = new RelayCommand(_ =>
        {
            _index = (_index + 1) % Greetings.Length;
            Greeting = Greetings[_index];
        });
    }

    public UIElement CreateUI()
    {
        // 松散 XAML 模式下宿主会优先加载 ui.xaml 并把本实例设为 DataContext，
        // 此方法仅作为无 ui.xaml 时的兜底。
        return new System.Windows.Controls.TextBlock { Text = Greeting };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>最小可用的 ICommand 实现（示例用）</summary>
internal class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged;
}
