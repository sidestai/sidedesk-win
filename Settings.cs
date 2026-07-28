using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SideDesk
{
    /// <summary>
    /// 单个显示器的停靠配置（停靠模式 + 每侧宽度/浮动独立）
    /// </summary>
    public class MonitorDockSettings
    {
        public LayoutMode Layout { get; set; } = LayoutMode.None;

        public int LeftPanelWidth { get; set; } = 105;
        public bool LeftHoverEnabled { get; set; } = true;
        public int LeftHoverWidth { get; set; } = 200;

        public int RightPanelWidth { get; set; } = 105;
        public bool RightHoverEnabled { get; set; } = true;
        public int RightHoverWidth { get; set; } = 200;
    }

    /// <summary>
    /// 单个虚拟桌面对应的全部显示器停靠配置
    /// </summary>
    public class DesktopDockProfile
    {
        public Guid DesktopId { get; set; }
        public string DesktopName { get; set; } = "";
        public List<MonitorDockSettings> Monitors { get; set; } = new();

        /// <summary>获取指定显示器的停靠配置，不存在时自动扩展列表并填充默认值。</summary>
        public MonitorDockSettings GetMonitor(int index)
        {
            if (index < 0) index = 0;
            while (Monitors.Count <= index)
                Monitors.Add(new MonitorDockSettings());
            return Monitors[index];
        }

        /// <summary>从另一个 profile 复制所有显示器配置</summary>
        public void CopyFrom(DesktopDockProfile source)
        {
            Monitors.Clear();
            foreach (var m in source.Monitors)
            {
                Monitors.Add(new MonitorDockSettings
                {
                    Layout = m.Layout,
                    LeftPanelWidth = m.LeftPanelWidth,
                    LeftHoverEnabled = m.LeftHoverEnabled,
                    LeftHoverWidth = m.LeftHoverWidth,
                    RightPanelWidth = m.RightPanelWidth,
                    RightHoverEnabled = m.RightHoverEnabled,
                    RightHoverWidth = m.RightHoverWidth
                });
            }
        }
    }

    public class Settings
    {
        public LayoutMode Layout { get; set; } = LayoutMode.Left;
        public ScreenType LeftScreen { get; set; } = ScreenType.A;
        public ScreenType RightScreen { get; set; } = ScreenType.B;
        public int PanelWidth { get; set; } = 105;

        // 左侧停靠宽度与浮动配置
        public int LeftPanelWidth { get; set; } = 105;
        public bool LeftHoverEnabled { get; set; } = true;
        public int LeftHoverWidth { get; set; } = 200;

        // 右侧停靠宽度与浮动配置
        public int RightPanelWidth { get; set; } = 105;
        public bool RightHoverEnabled { get; set; } = true;
        public int RightHoverWidth { get; set; } = 200;

        public string ThemeName { get; set; } = "";
        public string ThemeMode { get; set; } = "Dark"; // 支持 "Dark", "Light", "System"
        public double BackgroundOpacity { get; set; } = 50; // 背景透明度 0-100%
        public bool EnableTitleLock { get; set; } = false;
        public bool EnableAutoUpdate { get; set; } = true;
        // 全局窗口切换热键（Ctrl+1~9），默认关闭以避免与浏览器等应用的 Ctrl+数字切标签冲突
        public bool EnableWindowSwitchHotkey { get; set; } = false;

        /// <summary>是否开启 MCP Server（HTTP/SSE，端口 19192）</summary>
        public bool EnableMcpServer { get; set; } = false;

        /// <summary>数据模块连接方式："extension" = 浏览器插件（启动 WS 19191），"cdp" = 直连 CDP（需 Chrome 146+）</summary>
        public string DataConnectionMode { get; set; } = "extension";

        // ========== 账户 / 云同步 ==========

        /// <summary>登录后保存的账户 token（用于云同步鉴权；非空即视为已登录）</summary>
        public string? AccountToken { get; set; }

        /// <summary>账户显示名称</summary>
        public string? AccountName { get; set; }

        /// <summary>最近一次登录时间</summary>
        public DateTime? AccountLoginTime { get; set; }

        /// <summary>是否开启云同步（开启后定时把本地全部配置备份到服务器）</summary>
        public bool EnableCloudSync { get; set; } = false;

        // ========== 团队（Corp）切换 ==========

        /// <summary>当前选中的团队 ID（null 或空表示"个人"，传给后台的 corpid）</summary>
        public string? SelectedCorpId { get; set; }

        /// <summary>当前选中的团队名称（用于 UI 显示）</summary>
        public string? SelectedCorpName { get; set; }

        /// <summary>最近一次云同步成功的时间</summary>
        public DateTime? LastCloudSyncTime { get; set; }

        /// <summary>云同步 localId -> serverId 映射表（本地 Id 不变，上传/同步时查此表）</summary>
        public Dictionary<string, string> CloudIdMap { get; set; } = new();

        /// <summary>用户手动禁用的插件 ID 集合（在设置-插件中关闭）</summary>
        public HashSet<string> DisabledPlugins { get; set; } = new();

        /// <summary>每个物理显示器的停靠配置（下标 = 主显示器优先排序后的显示器序号）</summary>
        public List<MonitorDockSettings> Monitors { get; set; } = new();

        // ========== 虚拟桌面独立配置 ==========

        /// <summary>是否启用按虚拟桌面独立配置停靠布局</summary>
        public bool EnablePerVirtualDesktop { get; set; } = false;

        /// <summary>每个虚拟桌面的显示器停靠配置（仅在 EnablePerVirtualDesktop=true 时使用）</summary>
        public List<DesktopDockProfile> VirtualDesktops { get; set; } = new();

        /// <summary>获取指定显示器的停靠配置，不存在时自动扩展列表并填充默认值。</summary>
        public MonitorDockSettings GetMonitor(int index)
        {
            if (index < 0) index = 0;
            while (Monitors.Count <= index)
                Monitors.Add(new MonitorDockSettings());
            return Monitors[index];
        }

        /// <summary>获取指定虚拟桌面的配置 profile，不存在时自动创建并从全局 Monitors 复制。</summary>
        public DesktopDockProfile GetDesktopProfile(Guid desktopId, bool createIfMissing = true)
        {
            var profile = VirtualDesktops.Find(p => p.DesktopId == desktopId);
            if (profile != null) return profile;

            if (!createIfMissing)
                return new DesktopDockProfile { DesktopId = desktopId };

            // 新建 profile，从全局 Monitors 复制
            profile = new DesktopDockProfile { DesktopId = desktopId };
            foreach (var m in Monitors)
            {
                profile.Monitors.Add(new MonitorDockSettings
                {
                    Layout = m.Layout,
                    LeftPanelWidth = m.LeftPanelWidth,
                    LeftHoverEnabled = m.LeftHoverEnabled,
                    LeftHoverWidth = m.LeftHoverWidth,
                    RightPanelWidth = m.RightPanelWidth,
                    RightHoverEnabled = m.RightHoverEnabled,
                    RightHoverWidth = m.RightHoverWidth
                });
            }
            VirtualDesktops.Add(profile);
            return profile;
        }

        /// <summary>
        /// 获取当前虚拟桌面+指定显示器的停靠配置。
        /// 若未启用分桌面则退化为全局 GetMonitor()。
        /// </summary>
        public MonitorDockSettings GetCurrentMonitor(int monitorIndex, Guid? desktopId = null)
        {
            if (!EnablePerVirtualDesktop || desktopId == null || desktopId == Guid.Empty)
                return GetMonitor(monitorIndex);

            var profile = GetDesktopProfile(desktopId.Value);
            return profile.GetMonitor(monitorIndex);
        }

        /// <summary>
        /// 获取指定虚拟桌面 profile 中的显示器配置（用于设置 UI 绑定指定桌面）。
        /// </summary>
        public MonitorDockSettings GetMonitorForDesktop(Guid desktopId, int monitorIndex)
        {
            var profile = GetDesktopProfile(desktopId);
            return profile.GetMonitor(monitorIndex);
        }

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 保存失败: {ex.Message}");
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    // 判断是否为旧配置（旧版本仅有 PanelWidth，无 Left/RightPanelWidth 键）
                    bool hasPerSideWidth = json.Contains("LeftPanelWidth");
                    var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                    // 一次性迁移：旧配置将 PanelWidth 回填到左右两侧宽度，避免老用户自定义宽度丢失
                    if (!hasPerSideWidth)
                    {
                        settings.LeftPanelWidth = settings.RightPanelWidth = settings.PanelWidth;
                    }
                    // 一次性迁移：旧配置无 Monitors 列表，将全局停靠/宽度/浮动配置迁移到主显示器（下标 0）
                    if (settings.Monitors == null || settings.Monitors.Count == 0)
                    {
                        settings.Monitors = new List<MonitorDockSettings>
                        {
                            new MonitorDockSettings
                            {
                                Layout = settings.Layout,
                                LeftPanelWidth = settings.LeftPanelWidth,
                                LeftHoverEnabled = settings.LeftHoverEnabled,
                                LeftHoverWidth = settings.LeftHoverWidth,
                                RightPanelWidth = settings.RightPanelWidth,
                                RightHoverEnabled = settings.RightHoverEnabled,
                                RightHoverWidth = settings.RightHoverWidth
                            }
                        };
                    }
                    return settings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 加载失败: {ex.Message}");
            }
            return new Settings();
        }
    }
}
