using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk;

public partial class AddShortcutDialog : Window
{
    private bool _isBatchMode = false;
    private string _selectedColor = "#3498DB";
    /// <summary>Profile 下拉中"新建"项的索引（-1 表示未构建）</summary>
    private int _profileCreateNewIndex = -1;
    /// <summary>系统默认浏览器 exe 路径（注册表 UserChoice 探测，空=未识别）</summary>
    private readonly string _defaultBrowserExe = "";
    /// <summary>窗口是否已关闭：async void 续体在关闭后禁止再动 DialogResult/Close</summary>
    private bool _closed;
    /// <summary>Ok_Click 重入保护：防止 await 期间用户重复点击确定</summary>
    private bool _okInProgress;
    private readonly string[] _presetColors =
    {
        "#3498DB", "#2ECC71", "#E74C3C", "#9B59B6",
        "#F39C12", "#1ABC9C", "#E67E22", "#34495E",
        "#2980B9", "#27AE60", "#C0392B", "#8E44AD"
    };

    /// <summary>单个模式结果</summary>
    public ShortcutResult? SingleResult { get; private set; }
    /// <summary>批量模式结果</summary>
    public List<ShortcutResult>? BatchResults { get; private set; }

    public AddShortcutDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Closed += (s, e) => _closed = true;

        // 加载浏览器列表（首项标注系统默认浏览器名字）
        var app = (App)Application.Current;
        _defaultBrowserExe = DataPollingService.FindDefaultBrowserExe();
        var defaultLabel = Framework.Lang.T("common.defaultBrowser");
        if (!string.IsNullOrEmpty(_defaultBrowserExe))
        {
            var dn = Path.GetFileNameWithoutExtension(_defaultBrowserExe);
            if (dn.Length > 0) dn = char.ToUpperInvariant(dn[0]) + dn.Substring(1);
            defaultLabel = Framework.Lang.TF("common.defaultBrowserNamed", dn);
        }
        browserCombo.Items.Add(defaultLabel);
        foreach (var browser in app.ProgramRegistry.Browsers)
        {
            browserCombo.Items.Add(browser.Name);
        }
        browserCombo.SelectedIndex = 0;

        // 初始化颜色选择器
        InitColorPicker();

        // 图标预览（emoji 或路径图标）
        iconBox.TextChanged += (s, e) => UpdateIconPreview();

        // 输入网址后自动获取 favicon 填充图标字段（图标已有内容时不覆盖；缓存命中立即返回）
        urlBox.LostFocus += async (s, e) =>
        {
            var url = urlBox.Text?.Trim() ?? "";
            if (url == "https://" || string.IsNullOrEmpty(url)) return;
            if (!string.IsNullOrWhiteSpace(iconBox.Text)) return;
            try
            {
                var fav = await Task.Run(() => FaviconService.FetchFaviconAsync(url));
                if (!string.IsNullOrEmpty(fav) && string.IsNullOrWhiteSpace(iconBox.Text))
                    iconBox.Text = fav;
            }
            catch { }
        };
    }

    private void UpdateIconPreview()
    {
        string val = iconBox.Text.Trim();
        if (FaviconService.IsPathIcon(val))
        {
            var src = FaviconService.LoadFileIcon(val);
            if (src != null)
                iconPreview.Content = new Image { Source = src, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            else
                iconPreview.Content = new TextBlock { Text = "?", FontSize = 14, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            iconPreview.Content = new Emoji.Wpf.TextBlock { Text = val, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    private void InitColorPicker()
    {
        var accentBrush = (Brush)FindResource("BrushAccent");
        foreach (var color in _presetColors)
        {
            var swatch = new Border
            {
                Width = 32, Height = 32,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(6),
                Background = (SolidColorBrush)new BrushConverter().ConvertFrom(color)!,
                BorderBrush = color == _selectedColor ? accentBrush : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = color
            };
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                _selectedColor = (string)swatch.Tag!;
                foreach (UIElement child in colorPickerPanel.Children)
                {
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                }
                swatch.BorderBrush = accentBrush;
            };
            colorPickerPanel.Children.Add(swatch);
        }
    }

    private void BrowserCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 默认浏览器：能识别出系统默浏时同样提供 Profile 行（列出默浏已配置的 Profiles）
        if (browserCombo.SelectedIndex == 0)
        {
            if (string.IsNullOrEmpty(_defaultBrowserExe))
            {
                _profileCreateNewIndex = -1;
                profilePanel.Visibility = Visibility.Collapsed;
                return;
            }
            RefreshProfileCombo(GetDefaultBrowserConfig()?.Profiles ?? new List<UserDataProfile>());
            profilePanel.Visibility = Visibility.Visible;
            return;
        }
        if (browserCombo.SelectedIndex < 0)
        {
            _profileCreateNewIndex = -1;
            profilePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var app = (App)Application.Current;
        var browserIndex = browserCombo.SelectedIndex - 1;
        if (browserIndex >= 0 && browserIndex < app.ProgramRegistry.Browsers.Count)
        {
            RefreshProfileCombo(app.ProgramRegistry.Browsers[browserIndex].Profiles);
            // 选了具体浏览器就显示 Profile 行：未配置时可选"默认"或"新建"
            profilePanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>系统默认浏览器对应的已登记浏览器条目（按 exe 路径匹配）；未登记返回 null</summary>
    private BrowserConfig? GetDefaultBrowserConfig()
    {
        if (string.IsNullOrEmpty(_defaultBrowserExe)) return null;
        return ((App)Application.Current).ProgramRegistry.Browsers.Find(
            b => string.Equals(b.ExePath, _defaultBrowserExe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>默认浏览器未登记时自动登记一条，使"新建"Profile 有归属</summary>
    private BrowserConfig? AutoRegisterDefaultBrowser()
    {
        if (string.IsNullOrEmpty(_defaultBrowserExe)) return null;
        var registry = ((App)Application.Current).ProgramRegistry;
        var cfg = new BrowserConfig
        {
            Name = Path.GetFileNameWithoutExtension(_defaultBrowserExe),
            ExePath = _defaultBrowserExe
        };
        registry.Browsers.Add(cfg);
        registry.Save();
        return cfg;
    }

    /// <summary>重建 Profile 下拉：默认 + 已配置 Profiles + "新建"项（新建仅标记意图，确定时才创建目录）</summary>
    private void RefreshProfileCombo(List<UserDataProfile> profiles)
    {
        profileCombo.Items.Clear();
        profileCombo.Items.Add(Framework.Lang.T("common.default"));
        foreach (var profile in profiles)
        {
            profileCombo.Items.Add(profile.Name);
        }
        profileCombo.Items.Add(Framework.Lang.T("shortcut.profileCreateNew"));
        _profileCreateNewIndex = profileCombo.Items.Count - 1;
        profileCombo.SelectedIndex = 0;
    }

    /// <summary>以快捷项名称为名新建 UserData 目录并登记到浏览器；失败返回 null（回退为默认）</summary>
    private UserDataProfile? CreateProfileFromName(BrowserConfig browser, string desiredName)
    {
        var registry = ((App)Application.Current).ProgramRegistry;
        var baseDir = registry.DefaultProfileBaseDir;
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "BrowserProfiles");

        // 名称清洗：剔除目录非法字符；空名回退 Profile N
        var baseName = string.Join("", desiredName.Split(Path.GetInvalidFileNameChars())).Trim();
        if (baseName.Length > 60) baseName = baseName.Substring(0, 60);
        if (string.IsNullOrEmpty(baseName)) baseName = "Profile " + (browser.Profiles.Count + 1);

        // 同名 Profile 或同名目录已存在时追加序号，保证新建
        var name = baseName;
        for (int suffix = 2;
             browser.Profiles.Exists(p => p.Name == name) || Directory.Exists(Path.Combine(baseDir, name));
             suffix++)
        {
            name = baseName + " " + suffix;
        }

        var dir = Path.Combine(baseDir, name);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AddShortcut] create profile dir failed: {ex.Message}");
            ThemedMessageBox.Show(this, Framework.Lang.T("common.operationFailed"), Framework.Lang.T("common.notice"));
            return null;
        }

        var entry = new UserDataProfile { Name = name, Directory = dir };
        browser.Profiles.Add(entry);
        registry.Save();
        return entry;
    }

    private void OpenWebMarket_Click(object sender, RoutedEventArgs e)
    {
        Windows.MarketWindow.Open("/web-market", Framework.Lang.T("shortcut.webMarket"));
    }

    private void ToggleBatch_Click(object sender, RoutedEventArgs e)
    {
        _isBatchMode = !_isBatchMode;
        singlePanel.Visibility = _isBatchMode ? Visibility.Collapsed : Visibility.Visible;
        batchPanel.Visibility = _isBatchMode ? Visibility.Visible : Visibility.Collapsed;
        toggleBatchBtn.Content = _isBatchMode ? Framework.Lang.T("shortcut.singleMode") : Framework.Lang.T("shortcut.batchMode");
        this.Height = _isBatchMode ? 350 : 290;
        this.Width = _isBatchMode ? 500 : 400;

        if (_isBatchMode && batchRows.Children.Count == 0)
        {
            AddBatchRow();
            AddBatchRow();
            AddBatchRow();
        }
    }

    private void AddBatchRow()
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

        var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");

        var iconBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus"),
            MaxLength = 4
        };
        Grid.SetColumn(iconBox, 0);

        var nameBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus")
        };
        Grid.SetColumn(nameBox, 1);

        var urlBox = new TextBox
        {
            Margin = new Thickness(0, 0, 5, 0),
            Style = textBoxStyle,
            Text = "https://",
            Height = 30,
            FontSize = (double)FindResource("FontSizeSmallPlus")
        };
        Grid.SetColumn(urlBox, 2);

        var appCheck = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsChecked = true,
            Style = (Style)FindResource("RaycastSwitchStyle")
        };
        Grid.SetColumn(appCheck, 3);

        nameBox.TextChanged += BatchRow_TextChanged;
        urlBox.TextChanged += BatchRow_TextChanged;

        row.Children.Add(iconBox);
        row.Children.Add(nameBox);
        row.Children.Add(urlBox);
        row.Children.Add(appCheck);

        batchRows.Children.Add(row);
    }

    private void BatchRow_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (batchRows.Children.Count == 0) return;
        var lastRow = batchRows.Children[batchRows.Children.Count - 1] as Grid;
        if (lastRow == null) return;

        string name = "";
        string url = "";
        foreach (UIElement child in lastRow.Children)
        {
            if (child is TextBox tb)
            {
                int col = Grid.GetColumn(tb);
                if (col == 1) name = tb.Text;
                else if (col == 2) url = tb.Text;
            }
        }

        if (!string.IsNullOrEmpty(name) || (url != "https://" && !string.IsNullOrEmpty(url)))
        {
            AddBatchRow();
        }
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 重入保护：favicon 异步拉取期间用户可能再次点确定，避免二次续体访问已关闭的对话框
        if (_okInProgress || _closed) return;
        _okInProgress = true;
        try
        {
            if (_isBatchMode)
            {
                BatchResults = new List<ShortcutResult>();
                foreach (UIElement element in batchRows.Children)
                {
                    if (element is not Grid row) continue;

                    string name = "";
                    string url = "";
                    string icon = "";
                    bool isApp = false;

                    foreach (UIElement child in row.Children)
                    {
                        if (child is TextBox tb)
                        {
                            int col = Grid.GetColumn(tb);
                            if (col == 0) icon = tb.Text?.Trim() ?? "";
                            else if (col == 1) name = tb.Text?.Trim() ?? "";
                            else if (col == 2) url = tb.Text?.Trim() ?? "";
                        }
                        else if (child is CheckBox cb)
                        {
                            isApp = cb.IsChecked ?? false;
                        }
                    }

                    if (url == "https://") url = "";
                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(url))
                    {
                        BatchResults.Add(new ShortcutResult
                        {
                            Name = name,
                            Icon = string.IsNullOrEmpty(icon) ? null : icon,
                            Url = url,
                            IsAppMode = isApp
                        });
                    }
                }

                if (BatchResults.Count > 0)
                {
                    TryClose(true);
                }
            }
            else
            {
                var url = urlBox.Text?.Trim() ?? "";
                if (url == "https://") url = "";

                // 图标为空且网址有效时，自动获取 favicon 填充（缓存命中立即返回）
                if (string.IsNullOrWhiteSpace(iconBox.Text) && !string.IsNullOrEmpty(url))
                {
                    try
                    {
                        var fav = await Task.Run(() => FaviconService.FetchFaviconAsync(url));
                        // await 期间窗口可能已被取消/X 关闭，续体先探测再操作 UI
                        if (_closed) return;
                        if (!string.IsNullOrEmpty(fav)) iconBox.Text = fav;
                    }
                    catch { }
                }
                if (_closed) return;

                // 获取选中的浏览器 ID
                string? browserId = null;
                string? profileId = null;
                var shortcutName = nameBox.Text?.Trim() ?? "";
                if (browserCombo.SelectedIndex == 0)
                {
                    // 默认浏览器：仅当实际选了 Profile 才写入 browserId（默浏本身即系统默认，绑定后启动引擎才能解析 Profile）
                    if (profilePanel.Visibility == Visibility.Visible && profileCombo.SelectedIndex != 0)
                    {
                        var defCfg = GetDefaultBrowserConfig();
                        if (profileCombo.SelectedIndex == _profileCreateNewIndex && _profileCreateNewIndex > 0)
                        {
                            // "新建 Profile"：确定时才用名称栏内容创建 UserData 目录并指向它
                            defCfg ??= AutoRegisterDefaultBrowser();
                            if (defCfg != null)
                            {
                                browserId = defCfg.Id;
                                profileId = CreateProfileFromName(defCfg, shortcutName)?.Id;
                            }
                        }
                        else if (defCfg != null)
                        {
                            var defProfileIndex = profileCombo.SelectedIndex - 1;
                            if (defProfileIndex >= 0 && defProfileIndex < defCfg.Profiles.Count)
                            {
                                browserId = defCfg.Id;
                                profileId = defCfg.Profiles[defProfileIndex].Id;
                            }
                        }
                    }
                }
                else if (browserCombo.SelectedIndex > 0)
                {
                    var app = (App)Application.Current;
                    var browserIndex = browserCombo.SelectedIndex - 1;
                    if (browserIndex >= 0 && browserIndex < app.ProgramRegistry.Browsers.Count)
                    {
                        var browser = app.ProgramRegistry.Browsers[browserIndex];
                        browserId = browser.Id;

                        if (profileCombo.SelectedIndex == _profileCreateNewIndex && _profileCreateNewIndex > 0)
                        {
                            // "新建 Profile"：确定时才用名称栏内容创建 UserData 目录并指向它
                            var entry = CreateProfileFromName(browser, shortcutName);
                            if (entry != null) profileId = entry.Id;
                        }
                        else if (profileCombo.SelectedIndex > 0)
                        {
                            var profileIndex = profileCombo.SelectedIndex - 1;
                            if (profileIndex >= 0 && profileIndex < browser.Profiles.Count)
                            {
                                profileId = browser.Profiles[profileIndex].Id;
                            }
                        }
                    }
                }

                SingleResult = new ShortcutResult
                {
                    Name = nameBox.Text?.Trim() ?? "",
                    Icon = string.IsNullOrEmpty(iconBox.Text.Trim()) ? null : iconBox.Text.Trim(),
                    Url = url,
                    IsAppMode = appModeCheck.IsChecked ?? false,
                    BrowserId = browserId,
                    ProfileId = profileId,
                    BackgroundColor = _selectedColor
                };
                TryClose(true);
            }
        }
        finally
        {
            _okInProgress = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        TryClose(false);
    }

    /// <summary>安全关闭：对话框可能已被 X/取消关闭，或调用方非 ShowDialog 打开，避免 DialogResult 抛异常</summary>
    private void TryClose(bool result)
    {
        if (_closed) return;
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // 窗口已关闭或非模态显示：忽略并继续 Close 兜底
        }
        if (!_closed) Close();
    }
}

public class ShortcutResult
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string Url { get; set; } = "";
    public bool IsAppMode { get; set; } = false;
    public string? BrowserId { get; set; }
    public string? ProfileId { get; set; }
    public string BackgroundColor { get; set; } = "#3498DB";
}
