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

        // ========== 账户 / 云同步 ==========

        /// <summary>登录后保存的账户 token（用于云同步鉴权；非空即视为已登录）</summary>
        public string? AccountToken { get; set; }

        /// <summary>账户显示名称</summary>
        public string? AccountName { get; set; }

        /// <summary>最近一次登录时间</summary>
        public DateTime? AccountLoginTime { get; set; }

        /// <summary>是否开启云同步（开启后定时把本地全部配置备份到服务器）</summary>
        public bool EnableCloudSync { get; set; } = false;

        /// <summary>最近一次云同步成功的时间</summary>
        public DateTime? LastCloudSyncTime { get; set; }

        /// <summary>每个物理显示器的停靠配置（下标 = 主显示器优先排序后的显示器序号）</summary>
        public List<MonitorDockSettings> Monitors { get; set; } = new();

        /// <summary>获取指定显示器的停靠配置，不存在时自动扩展列表并填充默认值。</summary>
        public MonitorDockSettings GetMonitor(int index)
        {
            if (index < 0) index = 0;
            while (Monitors.Count <= index)
                Monitors.Add(new MonitorDockSettings());
            return Monitors[index];
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
