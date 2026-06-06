using Newtonsoft.Json;

namespace SideDesk.Theming
{
    /// <summary>
    /// JSON 主题文件的根对象
    /// </summary>
    public class ThemeDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "Default";

        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("color")]
        public ThemeColorConfig Color { get; set; } = new ThemeColorConfig();

        [JsonProperty("font")]
        public ThemeFontConfig Font { get; set; } = new ThemeFontConfig();

        [JsonProperty("layout")]
        public ThemeLayoutConfig Layout { get; set; } = new ThemeLayoutConfig();
    }

    /// <summary>
    /// 主题颜色配置（HEX 字符串）
    /// </summary>
    public class ThemeColorConfig
    {
        [JsonProperty("background")]
        public string Background { get; set; } = "#000000";

        [JsonProperty("backgroundOpacity")]
        public double BackgroundOpacity { get; set; } = 0.1d;

        [JsonProperty("textPrimary")]
        public string TextPrimary { get; set; } = "#FFFFFF";

        [JsonProperty("textAlert")]
        public string TextAlert { get; set; } = "#FF4136";

        [JsonProperty("groupBackground")]
        public string GroupBackground { get; set; } = "#333333";

        [JsonProperty("accent")]
        public string Accent { get; set; } = "#7C3AED";

        [JsonProperty("accentBookmark")]
        public string AccentBookmark { get; set; } = "#2ECC71";

        [JsonProperty("accentAITool")]
        public string AccentAITool { get; set; } = "#7C3AED";

        [JsonProperty("accentBrowser")]
        public string AccentBrowser { get; set; } = "#4A90D9";

        [JsonProperty("accentProgram")]
        public string AccentProgram { get; set; } = "#9B59B6";

        [JsonProperty("accentCounter")]
        public string AccentCounter { get; set; } = "#E74C3C";

        [JsonProperty("accentTaskbar")]
        public string AccentTaskbar { get; set; } = "#FF8C00";

        [JsonProperty("separator")]
        public string Separator { get; set; } = "#555555";
    }

    /// <summary>
    /// 主题字体配置
    /// </summary>
    public class ThemeFontConfig
    {
        [JsonProperty("family")]
        public string Family { get; set; } = "Microsoft YaHei UI";

        [JsonProperty("size")]
        public int Size { get; set; } = 14;

        [JsonProperty("bold")]
        public bool Bold { get; set; } = false;
    }

    /// <summary>
    /// 主题布局配置（圆角/间距）
    /// </summary>
    public class ThemeLayoutConfig
    {
        [JsonProperty("cornerRadius")]
        public int CornerRadius { get; set; } = 8;

        [JsonProperty("buttonPadding")]
        public int ButtonPadding { get; set; } = 8;

        [JsonProperty("groupSpacing")]
        public int GroupSpacing { get; set; } = 5;

        [JsonProperty("itemSpacing")]
        public int ItemSpacing { get; set; } = 2;

        [JsonProperty("sidebarPadding")]
        public int SidebarPadding { get; set; } = 5;
    }
}
