using System.Windows;

namespace SideDesk.Services.Plugins;

/// <summary>
/// SideDesk 插件契约接口
/// 所有外部插件 DLL 必须实现此接口，宿主通过该接口加载 UI 和管理生命周期
/// </summary>
public interface ISideDeskPlugin
{
    /// <summary>插件唯一标识（与 plugin.json 中的 id 一致）</summary>
    string Id { get; }

    /// <summary>插件显示名称</summary>
    string Name { get; }

    /// <summary>插件版本</summary>
    string Version { get; }

    /// <summary>
    /// 创建插件 UI 元素（宿主调用此方法获取插件的可视化内容）
    /// </summary>
    /// <returns>插件的 WPF UI 元素</returns>
    UIElement CreateUI();

    /// <summary>插件被加载时调用（可选初始化逻辑）</summary>
    void OnLoaded() { }

    /// <summary>插件被卸载时调用（释放资源）</summary>
    void OnUnloaded() { }
}
