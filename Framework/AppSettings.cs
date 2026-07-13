using System;
using System.IO;
using System.ComponentModel;
using Newtonsoft.Json;

namespace SideDesk.Framework
{
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class AppSettings : INotifyPropertyChanged
    {
        private static readonly string LocalAppFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");

        private static readonly string SettingsFilePath = Path.Combine(LocalAppFolder, "settings.json");

        private AppSettings() { }

        public void Save()
        {
            if (!Directory.Exists(LocalAppFolder))
            {
                Directory.CreateDirectory(LocalAppFolder);
            }

            using (StreamWriter writer = File.CreateText(SettingsFilePath))
            {
                new JsonSerializer() { Formatting = Formatting.Indented }.Serialize(writer, this);
            }
        }

        public void Reload()
        {
            _instance = Load();
        }

        private static AppSettings Load()
        {
            AppSettings result = null;

            if (File.Exists(SettingsFilePath))
            {
                using (StreamReader reader = File.OpenText(SettingsFilePath))
                {
                    result = (AppSettings)new JsonSerializer().Deserialize(reader, typeof(AppSettings));
                }
            }

            return result ?? new AppSettings();
        }

        public void NotifyPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // ========== 通用设置 ==========

        private bool _initialSetup { get; set; } = true;

        [JsonProperty]
        public bool InitialSetup
        {
            get { return _initialSetup; }
            set { _initialSetup = value; NotifyPropertyChanged("InitialSetup"); }
        }

        private bool _alwaysTop { get; set; } = true;

        [JsonProperty]
        public bool AlwaysTop
        {
            get { return _alwaysTop; }
            set { _alwaysTop = value; NotifyPropertyChanged("AlwaysTop"); }
        }

        private bool _runAtStartup { get; set; } = true;

        [JsonProperty]
        public bool RunAtStartup
        {
            get { return _runAtStartup; }
            set { _runAtStartup = value; NotifyPropertyChanged("RunAtStartup"); }
        }

        private double _uiScale { get; set; } = 1d;

        [JsonProperty]
        public double UIScale
        {
            get { return _uiScale; }
            set { _uiScale = value; NotifyPropertyChanged("UIScale"); }
        }

        private int _pollingInterval { get; set; } = 1000;

        [JsonProperty]
        public int PollingInterval
        {
            get { return _pollingInterval; }
            set { _pollingInterval = value; NotifyPropertyChanged("PollingInterval"); }
        }

        private bool _toolbarMode { get; set; } = true;

        [JsonProperty]
        public bool ToolbarMode
        {
            get { return _toolbarMode; }
            set { _toolbarMode = value; NotifyPropertyChanged("ToolbarMode"); }
        }

        private bool _clickThrough { get; set; } = false;

        [JsonProperty]
        public bool ClickThrough
        {
            get { return _clickThrough; }
            set { _clickThrough = value; NotifyPropertyChanged("ClickThrough"); }
        }

        private bool _pwaMaximized { get; set; } = true;

        [JsonProperty]
        public bool PwaMaximized
        {
            get { return _pwaMaximized; }
            set { _pwaMaximized = value; NotifyPropertyChanged("PwaMaximized"); }
        }

        private bool _showInTaskbar { get; set; } = true;

        [JsonProperty]
        public bool ShowInTaskbar
        {
            get { return _showInTaskbar; }
            set { _showInTaskbar = value; NotifyPropertyChanged("ShowInTaskbar"); }
        }

        private bool _mouseHoverExpand { get; set; } = true;

        [JsonProperty]
        public bool MouseHoverExpand
        {
            get { return _mouseHoverExpand; }
            set { _mouseHoverExpand = value; NotifyPropertyChanged("MouseHoverExpand"); }
        }

        private bool _enableWindowLogging { get; set; } = true;

        [JsonProperty]
        public bool EnableWindowLogging
        {
            get { return _enableWindowLogging; }
            set { _enableWindowLogging = value; NotifyPropertyChanged("EnableWindowLogging"); }
        }

        // 任务栏标题锁定看门狗刷新间隔（毫秒）。默认 5000ms。
        private int _titleLockWatchdogInterval { get; set; } = 5000;

        [JsonProperty]
        public int TitleLockWatchdogInterval
        {
            get { return _titleLockWatchdogInterval; }
            set
            {
                if (value < 500) value = 500;
                if (value > 60000) value = 60000;
                _titleLockWatchdogInterval = value;
                NotifyPropertyChanged("TitleLockWatchdogInterval");
            }
        }

        // 任务栏标题锁定重扫全部窗口间隔（毫秒）。默认 15000ms。
        private int _titleLockRescanInterval { get; set; } = 15000;

        [JsonProperty]
        public int TitleLockRescanInterval
        {
            get { return _titleLockRescanInterval; }
            set
            {
                if (value < 2000) value = 2000;
                if (value > 600000) value = 600000;
                _titleLockRescanInterval = value;
                NotifyPropertyChanged("TitleLockRescanInterval");
            }
        }

        private bool _showTrayIcon { get; set; } = true;

        [JsonProperty]
        public bool ShowTrayIcon
        {
            get { return _showTrayIcon; }
            set { _showTrayIcon = value; NotifyPropertyChanged("ShowTrayIcon"); }
        }

        private bool _collapseMenuBar { get; set; } = false;

        [JsonProperty]
        public bool CollapseMenuBar
        {
            get { return _collapseMenuBar; }
            set { _collapseMenuBar = value; NotifyPropertyChanged("CollapseMenuBar"); }
        }

        private bool _initiallyHidden { get; set; } = false;

        [JsonProperty]
        public bool InitiallyHidden
        {
            get { return _initiallyHidden; }
            set { _initiallyHidden = value; NotifyPropertyChanged("InitiallyHidden"); }
        }

        private int _sidebarWidth { get; set; } = 50;

        [JsonProperty]
        public int SidebarWidth
        {
            get { return _sidebarWidth; }
            set { _sidebarWidth = value < 50 ? 50 : value; NotifyPropertyChanged("SidebarWidth"); }
        }

        #region 显示设置

        private bool _showStickyNotes { get; set; } = true;
        [JsonProperty]
        public bool ShowStickyNotes
        {
            get { return _showStickyNotes; }
            set { _showStickyNotes = value; NotifyPropertyChanged("ShowStickyNotes"); }
        }

        private bool _showQuickGroups { get; set; } = true;
        [JsonProperty]
        public bool ShowQuickGroups
        {
            get { return _showQuickGroups; }
            set { _showQuickGroups = value; NotifyPropertyChanged("ShowQuickGroups"); }
        }

        private bool _showCounter { get; set; } = true;
        [JsonProperty]
        public bool ShowCounter
        {
            get { return _showCounter; }
            set { _showCounter = value; NotifyPropertyChanged("ShowCounter"); }
        }

        private bool _showAIChat { get; set; } = true;
        [JsonProperty]
        public bool ShowAIChat
        {
            get { return _showAIChat; }
            set { _showAIChat = value; NotifyPropertyChanged("ShowAIChat"); }
        }

        private bool _showUsageStats { get; set; } = true;
        [JsonProperty]
        public bool ShowUsageStats
        {
            get { return _showUsageStats; }
            set { _showUsageStats = value; NotifyPropertyChanged("ShowUsageStats"); }
        }

        private bool _showClipboard { get; set; } = true;
        [JsonProperty]
        public bool ShowClipboard
        {
            get { return _showClipboard; }
            set { _showClipboard = value; NotifyPropertyChanged("ShowClipboard"); }
        }

        private bool _showClipboardHistory { get; set; } = true;
        [JsonProperty]
        public bool ShowClipboardHistory
        {
            get { return _showClipboardHistory; }
            set { _showClipboardHistory = value; NotifyPropertyChanged("ShowClipboardHistory"); }
        }

        private bool _showMachineName { get; set; } = false;

        [JsonProperty]
        public bool ShowMachineName
        {
            get { return _showMachineName; }
            set { _showMachineName = value; NotifyPropertyChanged("ShowMachineName"); }
        }

        private bool _showClock { get; set; } = true;

        [JsonProperty]
        public bool ShowClock
        {
            get { return _showClock; }
            set { _showClock = value; NotifyPropertyChanged("ShowClock"); }
        }

        private bool _clock24HR { get; set; } = false;

        [JsonProperty]
        public bool Clock24HR
        {
            get { return _clock24HR; }
            set { _clock24HR = value; NotifyPropertyChanged("Clock24HR"); }
        }

        private bool _hideModuleFrame { get; set; } = false;

        [JsonProperty]
        public bool HideModuleFrame
        {
            get { return _hideModuleFrame; }
            set { _hideModuleFrame = value; NotifyPropertyChanged("HideModuleFrame"); }
        }

        #endregion

        #region 主题/外观属性

        private bool _autoBGColor { get; set; } = false;

        [JsonProperty]
        public bool AutoBGColor
        {
            get { return _autoBGColor; }
            set { _autoBGColor = value; NotifyPropertyChanged("AutoBGColor"); }
        }

        private string _bgColor { get; set; } = "#000000";

        [JsonProperty]
        public string BGColor
        {
            get { return _bgColor; }
            set { _bgColor = value; NotifyPropertyChanged("BGColor"); }
        }

        private double _bgOpacity { get; set; } = 0.1d;

        [JsonProperty]
        public double BGOpacity
        {
            get { return _bgOpacity; }
            set { _bgOpacity = value; NotifyPropertyChanged("BGOpacity"); }
        }

        private TextAlign _textAlign { get; set; } = TextAlign.Left;

        [JsonProperty]
        public TextAlign TextAlign
        {
            get { return _textAlign; }
            set { _textAlign = value; NotifyPropertyChanged("TextAlign"); }
        }

        private FontSetting _fontSetting { get; set; } = FontSetting.x14;

        [JsonProperty]
        public FontSetting FontSetting
        {
            get { return _fontSetting; }
            set { _fontSetting = value; NotifyPropertyChanged("FontSetting"); }
        }

        private string _fontColor { get; set; } = "#FFFFFF";

        [JsonProperty]
        public string FontColor
        {
            get { return _fontColor; }
            set { _fontColor = value; NotifyPropertyChanged("FontColor"); }
        }

        private string _alertFontColor { get; set; } = "#FF4136";

        [JsonProperty]
        public string AlertFontColor
        {
            get { return _alertFontColor; }
            set { _alertFontColor = value; NotifyPropertyChanged("AlertFontColor"); }
        }

        private bool _alertBlink { get; set; } = true;

        [JsonProperty]
        public bool AlertBlink
        {
            get { return _alertBlink; }
            set { _alertBlink = value; NotifyPropertyChanged("AlertBlink"); }
        }

        private string _groupBGColor { get; set; } = "#333333";

        [JsonProperty]
        public string GroupBGColor
        {
            get { return _groupBGColor; }
            set { _groupBGColor = value; NotifyPropertyChanged("GroupBGColor"); }
        }

        private string _accentColor { get; set; } = "#7C3AED";

        [JsonProperty]
        public string AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; NotifyPropertyChanged("AccentColor"); }
        }

        private string _accentBookmark { get; set; } = "#2ECC71";

        [JsonProperty]
        public string AccentBookmark
        {
            get { return _accentBookmark; }
            set { _accentBookmark = value; NotifyPropertyChanged("AccentBookmark"); }
        }

        private string _accentAITool { get; set; } = "#7C3AED";

        [JsonProperty]
        public string AccentAITool
        {
            get { return _accentAITool; }
            set { _accentAITool = value; NotifyPropertyChanged("AccentAITool"); }
        }

        private string _accentBrowser { get; set; } = "#4A90D9";

        [JsonProperty]
        public string AccentBrowser
        {
            get { return _accentBrowser; }
            set { _accentBrowser = value; NotifyPropertyChanged("AccentBrowser"); }
        }

        private string _accentProgram { get; set; } = "#9B59B6";

        [JsonProperty]
        public string AccentProgram
        {
            get { return _accentProgram; }
            set { _accentProgram = value; NotifyPropertyChanged("AccentProgram"); }
        }

        private string _accentCounter { get; set; } = "#E74C3C";

        [JsonProperty]
        public string AccentCounter
        {
            get { return _accentCounter; }
            set { _accentCounter = value; NotifyPropertyChanged("AccentCounter"); }
        }

        private string _accentTaskbar { get; set; } = "#FF8C00";

        [JsonProperty]
        public string AccentTaskbar
        {
            get { return _accentTaskbar; }
            set { _accentTaskbar = value; NotifyPropertyChanged("AccentTaskbar"); }
        }

        private string _separatorColor { get; set; } = "#555555";

        [JsonProperty]
        public string SeparatorColor
        {
            get { return _separatorColor; }
            set { _separatorColor = value; NotifyPropertyChanged("SeparatorColor"); }
        }

        private string _fontFamily { get; set; } = "Microsoft YaHei UI";

        [JsonProperty]
        public string FontFamily
        {
            get { return _fontFamily; }
            set { _fontFamily = value; NotifyPropertyChanged("FontFamily"); }
        }

        private bool _fontBold { get; set; } = false;

        [JsonProperty]
        public bool FontBold
        {
            get { return _fontBold; }
            set { _fontBold = value; NotifyPropertyChanged("FontBold"); }
        }

        private int _themeCornerRadius { get; set; } = 8;

        [JsonProperty]
        public int ThemeCornerRadius
        {
            get { return _themeCornerRadius; }
            set { _themeCornerRadius = value; NotifyPropertyChanged("ThemeCornerRadius"); }
        }

        private int _themeButtonPadding { get; set; } = 8;

        [JsonProperty]
        public int ThemeButtonPadding
        {
            get { return _themeButtonPadding; }
            set { _themeButtonPadding = value; NotifyPropertyChanged("ThemeButtonPadding"); }
        }

        private int _themeGroupSpacing { get; set; } = 5;

        [JsonProperty]
        public int ThemeGroupSpacing
        {
            get { return _themeGroupSpacing; }
            set { _themeGroupSpacing = value; NotifyPropertyChanged("ThemeGroupSpacing"); }
        }

        private int _themeItemSpacing { get; set; } = 2;

        [JsonProperty]
        public int ThemeItemSpacing
        {
            get { return _themeItemSpacing; }
            set { _themeItemSpacing = value; NotifyPropertyChanged("ThemeItemSpacing"); }
        }

        private int _themeSidebarPadding { get; set; } = 5;

        [JsonProperty]
        public int ThemeSidebarPadding
        {
            get { return _themeSidebarPadding; }
            set { _themeSidebarPadding = value; NotifyPropertyChanged("ThemeSidebarPadding"); }
        }

        private string _currentTheme { get; set; } = "";

        [JsonProperty]
        public string CurrentTheme
        {
            get { return _currentTheme; }
            set { _currentTheme = value; NotifyPropertyChanged("CurrentTheme"); }
        }

        #endregion

        #region 日期设置

        private DateSetting _dateSetting { get; set; } = DateSetting.Short;

        [JsonProperty]
        public DateSetting DateSetting
        {
            get { return _dateSetting; }
            set { _dateSetting = value; NotifyPropertyChanged("DateSetting"); }
        }

        #endregion

        // ========== 单例 ==========

        private static AppSettings _instance { get; set; } = null;

        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }
    }

    public enum TextAlign : byte
    {
        Left,
        Right
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class FontSetting
    {
        internal FontSetting() { }

        private FontSetting(int fontSize)
        {
            FontSize = fontSize;
        }

        public override bool Equals(object obj)
        {
            FontSetting that = obj as FontSetting;
            if (that == null) return false;
            return this.FontSize == that.FontSize;
        }

        public override int GetHashCode()
        {
            return FontSize.GetHashCode();
        }

        public static FontSetting x10 => new FontSetting(10);
        public static FontSetting x12 => new FontSetting(12);
        public static FontSetting x14 => new FontSetting(14);
        public static FontSetting x16 => new FontSetting(16);
        public static FontSetting x18 => new FontSetting(18);

        [JsonProperty]
        public int FontSize { get; set; }

        public int TitleFontSize => FontSize + 2;
        public int SmallFontSize => FontSize - 2;

        public int IconSize
        {
            get
            {
                switch (FontSize)
                {
                    case 10: return 18;
                    case 12: return 22;
                    case 14: default: return 24;
                    case 16: return 28;
                    case 18: return 32;
                }
            }
        }

        public int BarHeight => FontSize - 3;
        public int BarWidth => BarHeight * 6;
        public int BarWidthWide => BarHeight * 8;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class DateSetting
    {
        internal DateSetting() { }

        private DateSetting(string format)
        {
            Format = format;
        }

        [JsonProperty]
        public string Format { get; set; }

        public string Display
        {
            get
            {
                if (string.Equals(Format, "Disabled", StringComparison.Ordinal))
                {
                    return "Disabled";
                }
                return DateTime.Today.ToString(Format);
            }
        }

        public override bool Equals(object obj)
        {
            DateSetting that = obj as DateSetting;
            if (that == null) return false;
            return string.Equals(this.Format, that.Format, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            return (Format ?? "").GetHashCode();
        }

        public static readonly DateSetting Disabled = new DateSetting("Disabled");
        public static readonly DateSetting Short = new DateSetting("M");
        public static readonly DateSetting Normal = new DateSetting("d");
        public static readonly DateSetting Long = new DateSetting("D");
    }
}
