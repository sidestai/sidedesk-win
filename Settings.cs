using System;
using System.IO;
using System.Text.Json;

namespace SideDesk
{
    public class Settings
    {
        public LayoutMode Layout { get; set; } = LayoutMode.Left;
        public ScreenType LeftScreen { get; set; } = ScreenType.A;
        public ScreenType RightScreen { get; set; } = ScreenType.B;
        public int PanelWidth { get; set; } = 105;
        public string ThemeName { get; set; } = "";
        public string ThemeMode { get; set; } = "Dark"; // 支持 "Dark", "Light", "System"
        public bool EnableTitleLock { get; set; } = false;
        public bool EnableAutoUpdate { get; set; } = true;

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
                    return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
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
