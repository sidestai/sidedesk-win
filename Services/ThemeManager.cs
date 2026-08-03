using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;

namespace SideDesk.Services
{
    /// <summary>
    /// JSON 自定义主题管理器。
    /// 扫描 resources/themes/*.json，把其中定义的颜色解析并覆盖到应用程序级资源字典，
    /// 从而在保留 XAML 主题底座（DarkTheme/LightTheme）的前提下实现"一套 JSON = 一套自定义主题"。
    /// 覆盖采用"根级资源键覆盖 + 记录已覆盖键"的方式：根级键优先于合并字典，清除时按键移除即可还原。
    /// </summary>
    public static class ThemeManager
    {
        #region JSON 主题模型

        public class JsonTheme
        {
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("version")] public int Version { get; set; } = 1;
            [JsonProperty("color")] public JsonThemeColor Color { get; set; } = new();
            [JsonProperty("font")] public JsonThemeFont Font { get; set; } = new();
            [JsonProperty("layout")] public JsonThemeLayout Layout { get; set; } = new();

            /// <summary>来源文件名（不含扩展名），用于持久化标识</summary>
            [JsonIgnore] public string FileName { get; set; } = "";
        }

        public class JsonThemeColor
        {
            [JsonProperty("background")] public string Background { get; set; } = "";
            [JsonProperty("backgroundOpacity")] public double BackgroundOpacity { get; set; } = 1.0;
            [JsonProperty("textPrimary")] public string TextPrimary { get; set; } = "";
            [JsonProperty("textAlert")] public string TextAlert { get; set; } = "";
            [JsonProperty("groupBackground")] public string GroupBackground { get; set; } = "";
            [JsonProperty("accent")] public string Accent { get; set; } = "";
            [JsonProperty("accentBookmark")] public string AccentBookmark { get; set; } = "";
            [JsonProperty("accentAITool")] public string AccentAITool { get; set; } = "";
            [JsonProperty("accentBrowser")] public string AccentBrowser { get; set; } = "";
            [JsonProperty("accentProgram")] public string AccentProgram { get; set; } = "";
            [JsonProperty("accentCounter")] public string AccentCounter { get; set; } = "";
            [JsonProperty("accentTaskbar")] public string AccentTaskbar { get; set; } = "";
            [JsonProperty("separator")] public string Separator { get; set; } = "";
        }

        public class JsonThemeFont
        {
            [JsonProperty("family")] public string Family { get; set; } = "";
            [JsonProperty("size")] public double Size { get; set; } = 14;
            [JsonProperty("bold")] public bool Bold { get; set; } = false;
        }

        public class JsonThemeLayout
        {
            [JsonProperty("cornerRadius")] public int CornerRadius { get; set; } = 8;
            [JsonProperty("buttonPadding")] public int ButtonPadding { get; set; } = 8;
            [JsonProperty("groupSpacing")] public int GroupSpacing { get; set; } = 5;
            [JsonProperty("itemSpacing")] public int ItemSpacing { get; set; } = 2;
            [JsonProperty("sidebarPadding")] public int SidebarPadding { get; set; } = 5;
        }

        #endregion

        /// <summary>主题目录（输出目录下的 resources/themes）</summary>
        public static string ThemesDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "themes");

        /// <summary>记录当前被 JSON 主题覆盖过的资源键，清除时按键还原</summary>
        private static readonly List<object> _overriddenKeys = new();

        /// <summary>当前已应用的 JSON 主题（null 表示使用默认 XAML 主题）</summary>
        public static JsonTheme? Current { get; private set; }

        #region 加载

        /// <summary>扫描主题目录，解析全部合法 JSON 主题（按名称排序）</summary>
        public static List<JsonTheme> LoadThemes()
        {
            var list = new List<JsonTheme>();
            try
            {
                if (!Directory.Exists(ThemesDir)) return list;
                foreach (var file in Directory.GetFiles(ThemesDir, "*.json"))
                {
                    var theme = TryParse(file);
                    if (theme != null) list.Add(theme);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] 扫描主题目录失败: {ex.Message}");
            }
            return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>按主题名查找（不区分大小写）</summary>
        public static JsonTheme? GetThemeByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return LoadThemes().FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.FileName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static JsonTheme? TryParse(string file)
        {
            try
            {
                var json = File.ReadAllText(file);
                var theme = JsonConvert.DeserializeObject<JsonTheme>(json);
                if (theme == null || string.IsNullOrWhiteSpace(theme.Name)) return null;
                theme.FileName = Path.GetFileNameWithoutExtension(file);
                return theme;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] 解析主题失败 {Path.GetFileName(file)}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 应用 / 清除

        /// <summary>
        /// 应用一个 JSON 主题：把其颜色派生出的全套资源键覆盖到应用程序级资源。
        /// 不会触发 ThemeChanged，调用方负责在应用后刷新 UI。
        /// </summary>
        public static void ApplyTheme(JsonTheme theme)
        {
            ClearOverrides(); // 先清掉上一套覆盖，避免残留

            var res = Application.Current.Resources;
            var c = theme.Color;

            var bg = ParseColor(c.Background) ?? (Color)ColorConverter.ConvertFromString("#FF161616");
            var text = ParseColor(c.TextPrimary) ?? (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
            var accent = ParseColor(c.Accent) ?? (Color)ColorConverter.ConvertFromString("#FF3B82F6");
            var groupBG = ParseColor(c.GroupBackground);
            var sep = ParseColor(c.Separator);

            bool isLight = Luminance(bg) > 0.5;
            var white = Colors.White;
            var black = Colors.Black;

            // —— 派生次要颜色 ——
            var border = sep ?? Blend(bg, text, 0.15);
            var textSec = Blend(text, bg, 0.35);
            var textMuted = Blend(text, bg, 0.60);
            var inputBG = Blend(bg, white, isLight ? 0.70 : 0.08);
            var dialogBG = Blend(bg, white, isLight ? 0.50 : 0.04);
            var secondaryBtnBG = groupBG ?? inputBG;

            // —— 基础 Color 键 ——
            Set(res, "ColorBG", bg);
            Set(res, "ColorBorder", border);
            Set(res, "ColorAccent", accent);
            Set(res, "ColorTextMain", text);
            Set(res, "ColorTextSec", textSec);
            Set(res, "ColorTextMuted", textMuted);
            Set(res, "ColorInputBG", inputBG);

            // —— 主笔刷 ——
            Set(res, "BrushBG", Brush(bg));
            Set(res, "BrushBorder", Brush(border));
            Set(res, "BrushAccent", Brush(accent));
            Set(res, "BrushTextMain", Brush(text));
            Set(res, "BrushTextSec", Brush(textSec));
            Set(res, "BrushTextMuted", Brush(textMuted));
            Set(res, "BrushInputBG", Brush(inputBG));

            // —— 按钮 ——
            Set(res, "BrushAccentText", Brush(Luminance(accent) > 0.6 ? black : white));
            Set(res, "BrushItemButtonText", Brush(isLight ? black : white));
            Set(res, "BrushTagSelectedText", Brush(isLight ? accent : white));
            Set(res, "BrushSecondaryBtnBG", Brush(secondaryBtnBG));
            Set(res, "BrushSecondaryBtnBorder", Brush(border));
            Set(res, "BrushSecondaryBtnText", Brush(textSec));

            // —— 列表 ——
            Set(res, "BrushListViewItemMouseOver", Brush(WithAlpha(isLight ? black : white, isLight ? 0.05 : 0.08)));
            Set(res, "BrushListViewItemHeaderBG", Brush(secondaryBtnBG));
            Set(res, "BrushListViewItemSelectedBG", Brush(WithAlpha(accent, 0.20)));
            Set(res, "BrushListViewItemSelectedBorder", Brush(WithAlpha(accent, 0.40)));

            // —— 对话框 / 右键菜单 / 分隔线 ——
            Set(res, "BrushDialogBG", Brush(dialogBG));
            Set(res, "BrushContextMenuBG", Brush(WithAlpha(dialogBG, 0.95)));
            Set(res, "BrushContextMenuBorder", Brush(WithAlpha(isLight ? black : white, 0.15)));
            Set(res, "BrushSeparator", Brush(WithAlpha(sep ?? border, 0.50)));
            Set(res, "BrushMenuItemHighlightBG", Brush(WithAlpha(isLight ? black : white, isLight ? 0.06 : 0.15)));
            Set(res, "BrushMenuItemGestureText", Brush(WithAlpha(isLight ? black : white, 0.50)));
            Set(res, "BrushMenuItemDisabledText", Brush(WithAlpha(isLight ? black : white, 0.25)));

            Current = theme;

            // 通知 App 同步标题栏明暗（自定义主题背景可能与默认深浅模式不一致）
            App.SetCustomThemeLightOverride(isLight);

            // 驱动各面板/窗口重绘以应用新颜色
            App.RaiseThemeChanged();
        }

        /// <summary>清除 JSON 主题覆盖，还原为纯 XAML 主题（DarkTheme/LightTheme）</summary>
        public static void ClearTheme()
        {
            ClearOverrides();
            Current = null;
            App.SetCustomThemeLightOverride(null);
            App.RaiseThemeChanged();
        }

        /// <summary>根据持久化的主题名恢复应用（启动时调用）。返回是否成功应用。</summary>
        public static bool RestoreFromName(string? name)
        {
            var theme = GetThemeByName(name);
            if (theme == null) return false;
            ApplyTheme(theme);
            return true;
        }

        private static void ClearOverrides()
        {
            var res = Application.Current.Resources;
            foreach (var key in _overriddenKeys)
                res.Remove(key);
            _overriddenKeys.Clear();
        }

        private static void Set(ResourceDictionary res, string key, object value)
        {
            res[key] = value;
            _overriddenKeys.Add(key);
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        #endregion

        #region 颜色工具

        /// <summary>解析 #RGB / #RRGGBB / #AARRGGBB，失败返回 null</summary>
        public static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                var h = hex.Trim().TrimStart('#');
                return h.Length switch
                {
                    3 => Color.FromRgb(
                        (byte)(Convert.ToByte(h[..1] + h[..1], 16)),
                        (byte)(Convert.ToByte(h[1..2] + h[1..2], 16)),
                        (byte)(Convert.ToByte(h[2..3] + h[2..3], 16))),
                    6 => Color.FromRgb(
                        Convert.ToByte(h[..2], 16),
                        Convert.ToByte(h[2..4], 16),
                        Convert.ToByte(h[4..6], 16)),
                    8 => Color.FromArgb(
                        Convert.ToByte(h[..2], 16),
                        Convert.ToByte(h[2..4], 16),
                        Convert.ToByte(h[4..6], 16),
                        Convert.ToByte(h[6..8], 16)),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>相对亮度（0~1），用于判断背景深浅</summary>
        public static double Luminance(Color c)
        {
            double R(double v) { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * R(c.R) + 0.7152 * R(c.G) + 0.0722 * R(c.B);
        }

        /// <summary>把颜色 a 向颜色 b 线性混合，t 为 b 的占比（0~1）</summary>
        public static Color Blend(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>设置颜色透明度（alpha 0~1），保留原 RGB</summary>
        public static Color WithAlpha(Color c, double alpha)
        {
            return Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);
        }

        #endregion
    }
}
