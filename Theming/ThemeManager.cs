using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SideDesk.Framework;

namespace SideDesk.Theming
{
    /// <summary>
    /// 主题管理器：加载/保存/应用 JSON 主题文件，写入 AppSettings.Instance 属性触发 UI 更新。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>
        /// 内置主题文件目录（随应用发布）
        /// </summary>
        public static string BuiltInThemeDir
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "themes");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 用户自定义主题目录
        /// </summary>
        public static string UserThemeDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk", "themes");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 列出所有可用主题名称（内置 + 用户自定义，不含扩展名）
        /// </summary>
        public static IEnumerable<string> GetAvailableThemes()
        {
            try
            {
                var builtIn = Directory.EnumerateFiles(BuiltInThemeDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension);

                var user = Directory.Exists(UserThemeDir)
                    ? Directory.EnumerateFiles(UserThemeDir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                    : Enumerable.Empty<string>();

                return builtIn.Concat(user)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 从 JSON 文件加载主题定义（优先从用户目录加载）
        /// </summary>
        public static ThemeDefinition LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Theme file not found: " + path);

            string json = File.ReadAllText(path);
            var theme = JsonConvert.DeserializeObject<ThemeDefinition>(json);
            if (theme == null)
                throw new InvalidOperationException("Failed to parse theme file: " + path);

            return theme;
        }

        /// <summary>
        /// 按名称加载主题（优先用户目录，然后内置目录）
        /// </summary>
        public static ThemeDefinition Load(string name)
        {
            // 优先从用户目录查找
            string userPath = Path.Combine(UserThemeDir, name + ".json");
            if (File.Exists(userPath))
                return LoadFromFile(userPath);

            // 然后从内置目录查找
            string builtInPath = Path.Combine(BuiltInThemeDir, name + ".json");
            if (File.Exists(builtInPath))
                return LoadFromFile(builtInPath);

            throw new FileNotFoundException("Theme not found: " + name);
        }

        /// <summary>
        /// 将主题定义保存为 JSON 文件（保存到用户自定义目录）
        /// </summary>
        public static void Save(string name, ThemeDefinition theme)
        {
            string path = Path.Combine(UserThemeDir, name + ".json");
            string json = JsonConvert.SerializeObject(theme, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// 将 ThemeDefinition 应用到 AppSettings.Instance 属性（触发 INotifyPropertyChanged → UI 实时更新）
        /// </summary>
        public static void Apply(ThemeDefinition theme)
        {
            var s = AppSettings.Instance;

            // 颜色
            s.BGColor = theme.Color.Background;
            s.BGOpacity = theme.Color.BackgroundOpacity;
            s.FontColor = theme.Color.TextPrimary;
            s.AlertFontColor = theme.Color.TextAlert;
            s.GroupBGColor = theme.Color.GroupBackground;
            s.AccentColor = theme.Color.Accent;
            s.AccentBookmark = theme.Color.AccentBookmark;
            s.AccentAITool = theme.Color.AccentAITool;
            s.AccentBrowser = theme.Color.AccentBrowser;
            s.AccentProgram = theme.Color.AccentProgram;
            s.AccentCounter = theme.Color.AccentCounter;
            s.AccentTaskbar = theme.Color.AccentTaskbar;
            s.SeparatorColor = theme.Color.Separator;

            // 字体
            s.FontFamily = theme.Font.Family;
            s.FontBold = theme.Font.Bold;
            // FontSetting 通过预设大小匹配
            s.FontSetting = GetFontSetting(theme.Font.Size);

            // 布局
            s.ThemeCornerRadius = theme.Layout.CornerRadius;
            s.ThemeButtonPadding = theme.Layout.ButtonPadding;
            s.ThemeGroupSpacing = theme.Layout.GroupSpacing;
            s.ThemeItemSpacing = theme.Layout.ItemSpacing;
            s.ThemeSidebarPadding = theme.Layout.SidebarPadding;
        }

        /// <summary>
        /// 按名称加载并应用主题
        /// </summary>
        public static void ApplyByName(string name)
        {
            var theme = Load(name);
            Apply(theme);
            AppSettings.Instance.CurrentTheme = name;
        }

        /// <summary>
        /// 从当前 AppSettings.Instance 属性生成 ThemeDefinition（用于保存）
        /// </summary>
        public static ThemeDefinition CreateFromCurrentSettings()
        {
            var s = AppSettings.Instance;
            return new ThemeDefinition
            {
                Name = string.IsNullOrEmpty(s.CurrentTheme) ? "Custom" : s.CurrentTheme,
                Version = 1,
                Color = new ThemeColorConfig
                {
                    Background = s.BGColor,
                    BackgroundOpacity = s.BGOpacity,
                    TextPrimary = s.FontColor,
                    TextAlert = s.AlertFontColor,
                    GroupBackground = s.GroupBGColor,
                    Accent = s.AccentColor,
                    AccentBookmark = s.AccentBookmark,
                    AccentAITool = s.AccentAITool,
                    AccentBrowser = s.AccentBrowser,
                    AccentProgram = s.AccentProgram,
                    AccentCounter = s.AccentCounter,
                    AccentTaskbar = s.AccentTaskbar,
                    Separator = s.SeparatorColor
                },
                Font = new ThemeFontConfig
                {
                    Family = s.FontFamily,
                    Size = s.FontSetting != null ? s.FontSetting.FontSize : 14,
                    Bold = s.FontBold
                },
                Layout = new ThemeLayoutConfig
                {
                    CornerRadius = s.ThemeCornerRadius,
                    ButtonPadding = s.ThemeButtonPadding,
                    GroupSpacing = s.ThemeGroupSpacing,
                    ItemSpacing = s.ThemeItemSpacing,
                    SidebarPadding = s.ThemeSidebarPadding
                }
            };
        }

        /// <summary>
        /// 将字号映射到最近的 FontSetting 预设
        /// </summary>
        private static FontSetting GetFontSetting(int size)
        {
            if (size <= 10) return FontSetting.x10;
            if (size <= 12) return FontSetting.x12;
            if (size <= 14) return FontSetting.x14;
            if (size <= 16) return FontSetting.x16;
            return FontSetting.x18;
        }
    }
}
