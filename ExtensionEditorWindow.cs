using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using SideDesk.Models;
using SideDesk.Controls;

namespace SideDesk;

/// <summary>
/// 高级项（扩展）编辑窗口：类型卡片 + 类型专属表单 + 高级选项，支持「简单模式 / 高级 JSON」双模式，
/// 配置序列化为 JSON 存入 QuickItem.AdvancedConfigJson。
/// </summary>
public partial class ExtensionEditorWindow : Window
{
    /// <summary>动作类型元数据：emoji 图标 / 名称键 / 描述键 / 主题色</summary>
    internal static readonly Dictionary<string, (string Emoji, string NameKey, string DescKey, string Color)> TypeMeta = new()
    {
        ["url"] = ("🌐", "notification.openWebPage", "advanced.typeUrlDesc", "#3b82f6"),
        ["exec"] = ("🚀", "advanced.typeExec", "advanced.typeExecDesc", "#a855f7"),
        ["keys"] = ("⌨️", "advanced.typeKeys", "advanced.typeKeysDesc", "#10b981"),
        ["paste"] = ("📋", "advanced.typePaste", "advanced.typePasteDesc", "#ec4899"),
        ["script"] = ("🖥️", "advanced.typeScript", "advanced.typeScriptDesc", "#f59e0b"),
        ["clipboard"] = ("📎", "advanced.typeClipboard", "advanced.typeClipboardDesc", "#06b6d4"),
        ["search"] = ("🔍", "common.search", "advanced.typeSearchDesc", "#6366f1"),
    };

    private static readonly string[] _presetColors =
    {
        "#3498DB", "#2ECC71", "#E74C3C", "#9B59B6",
        "#F39C12", "#1ABC9C", "#E67E22", "#34495E",
        "#2980B9", "#27AE60", "#C0392B", "#8E44AD"
    };

    private AdvancedItemConfig _cfg = new AdvancedItemConfig();
    private string _actionType = "url";
    private string _selectedColor = "#3498DB";
    private readonly QuickItem? _existing;
    private readonly string? _groupId;
    private bool _jsonMode = false;

    /// <summary>新建模式下的结果项</summary>
    public QuickItem? Result { get; private set; }

    // ===== UI =====
    private Button _simpleModeBtn = null!;
    private Button _jsonModeBtn = null!;
    private Grid _simplePanel = null!;
    private Grid _jsonPanel = null!;
    private WrapPanel _cardsPanel = null!;
    private StackPanel _formHost = null!;
    private readonly Dictionary<string, Border> _cardBorders = new();
    private readonly Dictionary<string, StackPanel> _formPanels = new();

    private TextBox _jsonBox = null!;
    private TextBlock _jsonStatus = null!;
    private ComboBox? _teamCombo;

    private EmojiTextBox _nameBox = null!;
    private TextBox _descBox = null!;
    private TextBox _keywordsBox = null!;
    private TextBox _iconBox = null!;
    private ContentControl _iconPreview = null!;
    private Border _previewIconBg = null!;
    private Emoji.Wpf.TextBlock _previewIcon = null!;
    private Emoji.Wpf.TextBlock _previewName = null!;
    private TextBlock _previewDesc = null!;
    private WrapPanel _colorPanel = null!;

    /// <param name="existing">编辑模式：传入已有项；新建模式传 null</param>
    /// <param name="groupId">所属分组 Id（登录后用于展示/修改所属团队）</param>
    public ExtensionEditorWindow(QuickItem? existing = null, string? groupId = null)
    {
        _existing = existing;
        _groupId = groupId;
        Title = Framework.Lang.T("advanced.editorTitle");
        Width = 980;
        Height = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BrushDialogBG");
        Foreground = (Brush)Application.Current.FindResource("BrushTextMain");
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

        if (existing != null)
        {
            _cfg = AdvancedItemConfig.TryParse(existing.AdvancedConfigJson) ?? new AdvancedItemConfig();
            _actionType = TypeMeta.ContainsKey(_cfg.ActionType) ? _cfg.ActionType : "url";
            if (!string.IsNullOrEmpty(existing.BackgroundColor)) _selectedColor = existing.BackgroundColor!;
        }

        BuildTopBar(out var topBar);
        BuildSimplePanel(out _simplePanel);
        BuildJsonPanel(out _jsonPanel);

        var root = new DockPanel();
        DockPanel.SetDock(topBar, Dock.Top);
        root.Children.Add(topBar);
        var contentGrid = new Grid();
        contentGrid.Children.Add(_simplePanel);
        contentGrid.Children.Add(_jsonPanel);
        root.Children.Add(contentGrid);
        Content = root;

        BuildTypeCards();
        BuildAllForms();
        InitRightPanelDefaults();
        ShowForm(_actionType);
        UpdateModeButtons();
    }

    // ===== 顶栏 =====

    private void BuildTopBar(out DockPanel topBar)
    {
        topBar = new DockPanel { Margin = new Thickness(16, 12, 16, 10) };

        // 右侧：试运行 / 保存
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(rightBtns, Dock.Right);

        var testRunBtn = new Button
        {
            Content = Framework.Lang.T("advanced.testRun"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("StandardButtonStyle")
        };
        testRunBtn.Click += async (s, e) => await TestRunAsync();
        rightBtns.Children.Add(testRunBtn);

        var saveBtn = new Button
        {
            Content = Framework.Lang.T("common.save"),
            Padding = new Thickness(14, 6, 14, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        saveBtn.Click += Save_Click;
        rightBtns.Children.Add(saveBtn);
        topBar.Children.Add(rightBtns);

        // 左侧：标题
        var title = new Emoji.Wpf.TextBlock
        {
            Text = Framework.Lang.T("advanced.editorTitle"),
            FontWeight = FontWeights.Bold,
            FontSize = (double)FindResource("FontSizeSubtitle"),
            Foreground = (Brush)FindResource("BrushTextMain"),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(title, Dock.Left);
        topBar.Children.Add(title);

        // 中部：模式切换
        var modeStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _simpleModeBtn = new Button
        {
            Content = Framework.Lang.T("advanced.simpleMode"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 6, 0)
        };
        _simpleModeBtn.Click += (s, e) => SwitchMode(false);
        _jsonModeBtn = new Button
        {
            Content = Framework.Lang.T("advanced.jsonMode"),
            Padding = new Thickness(14, 6, 14, 6)
        };
        _jsonModeBtn.Click += (s, e) => SwitchMode(true);
        modeStack.Children.Add(_simpleModeBtn);
        modeStack.Children.Add(_jsonModeBtn);
        topBar.Children.Add(modeStack);
    }

    private void UpdateModeButtons()
    {
        var primary = (Style)FindResource("PrimaryButtonStyle");
        var standard = (Style)FindResource("StandardButtonStyle");
        _simpleModeBtn.Style = _jsonMode ? standard : primary;
        _jsonModeBtn.Style = _jsonMode ? primary : standard;
    }

    // ===== 简单模式主面板 =====

    private void BuildSimplePanel(out Grid panel)
    {
        panel = new Grid { Margin = new Thickness(16, 0, 16, 16) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        // 左：类型卡片 + 表单 + 高级选项
        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 16, 0)
        };
        var leftStack = new StackPanel();

        _cardsPanel = new WrapPanel();
        leftStack.Children.Add(_cardsPanel);

        _formHost = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        leftStack.Children.Add(_formHost);

        leftStack.Children.Add(BuildAdvancedOptionsSection());

        leftScroll.Content = leftStack;
        Grid.SetColumn(leftScroll, 0);
        panel.Children.Add(leftScroll);

        // 右：实时预览 / 基础信息 / 外观
        var rightScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        rightScroll.Content = BuildRightPanel();
        Grid.SetColumn(rightScroll, 1);
        panel.Children.Add(rightScroll);
    }

    /// <summary>类型卡片网格</summary>
    private void BuildTypeCards()
    {
        foreach (var (type, meta) in TypeMeta)
        {
            var icon = new Emoji.Wpf.TextBlock
            {
                Text = meta.Emoji,
                FontSize = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T(meta.NameKey),
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            var desc = new TextBlock
            {
                Text = Framework.Lang.T(meta.DescKey),
                FontSize = (double)FindResource("FontSizeSmall"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                TextWrapping = TextWrapping.Wrap
            };
            var textStack = new StackPanel();
            textStack.Children.Add(name);
            textStack.Children.Add(desc);

            var inner = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            inner.Children.Add(icon);
            inner.Children.Add(textStack);

            var border = new Border
            {
                Width = 220,
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = inner,
                Tag = type
            };
            border.MouseLeftButtonUp += (s, e) => SelectType((string)border.Tag!);
            _cardBorders[type] = border;
            _cardsPanel.Children.Add(border);
        }
    }

    private void SelectType(string type)
    {
        if (type == _actionType) return;
        ReadCurrentForm();
        _actionType = type;
        ShowForm(type);
        // 名称为空时预填类型默认名
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = Framework.Lang.T(TypeMeta[type].NameKey);
            UpdatePreview();
        }
    }

    private void ShowForm(string type)
    {
        foreach (var (t, form) in _formPanels)
            form.Visibility = t == type ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Brush)FindResource("BrushAccent");
        var normalBorder = (Brush)FindResource("BrushBorder");
        foreach (var (t, border) in _cardBorders)
        {
            bool active = t == type;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(TypeMeta[t].Color)!;
                border.BorderBrush = active
                    ? new SolidColorBrush(color)
                    : normalBorder;
            }
            catch
            {
                border.BorderBrush = active ? accent : normalBorder;
            }
            border.Opacity = active ? 1.0 : 0.85;
        }
        UpdatePreview();
    }

    // ===== JSON 模式面板 =====

    private void BuildJsonPanel(out Grid panel)
    {
        panel = new Grid { Margin = new Thickness(16, 0, 16, 16), Visibility = Visibility.Collapsed };

        var dock = new DockPanel();

        // 顶部工具行
        var toolBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(toolBar, Dock.Top);
        var rawLabel = new TextBlock
        {
            Text = Framework.Lang.T("advanced.rawJson"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(rawLabel, Dock.Left);
        toolBar.Children.Add(rawLabel);

        var toolBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var formatBtn = new Button
        {
            Content = Framework.Lang.T("advanced.format"),
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("GhostButtonStyle")
        };
        formatBtn.Click += FormatJson_Click;
        var copyBtn = new Button
        {
            Content = Framework.Lang.T("common.copy"),
            Padding = new Thickness(12, 4, 12, 4),
            Style = (Style)FindResource("GhostButtonStyle")
        };
        copyBtn.Click += (s, e) => { try { Clipboard.SetText(_jsonBox.Text); } catch { } };
        toolBtns.Children.Add(formatBtn);
        toolBtns.Children.Add(copyBtn);
        toolBar.Children.Add(toolBtns);
        dock.Children.Add(toolBar);

        // 底部状态
        _jsonStatus = new TextBlock
        {
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        DockPanel.SetDock(_jsonStatus, Dock.Bottom);
        dock.Children.Add(_jsonStatus);

        // JSON 编辑框
        _jsonBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = (double)FindResource("FontSizeNormal"),
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = (Style)FindResource("GlobalTextBoxStyle"),
            // 覆盖样式内置的 Height=36，填满剩余空间；顶部对齐避免文本居中悬浮，上内边距留白
            Height = double.NaN,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 8, 6, 8)
        };
        _jsonBox.TextChanged += (s, e) => ValidateJsonSilent();
        dock.Children.Add(_jsonBox);

        panel.Children.Add(dock);
    }

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var obj = JsonConvert.DeserializeObject<object>(_jsonBox.Text);
            _jsonBox.Text = JsonConvert.SerializeObject(obj, Formatting.Indented);
        }
        catch { /* 非法 JSON 保持原样，状态栏会提示 */ }
    }

    private bool TryParseJsonFromBox(out AdvancedItemConfig parsed)
    {
        parsed = new AdvancedItemConfig();
        try
        {
            var p = JsonConvert.DeserializeObject<AdvancedItemConfig>(_jsonBox.Text);
            if (p == null) throw new Exception("null");
            parsed = p;
            UpdateJsonStatus(true, null);
            return true;
        }
        catch (Exception ex)
        {
            UpdateJsonStatus(false, ex.Message);
            return false;
        }
    }

    private void ValidateJsonSilent()
    {
        try
        {
            JsonConvert.DeserializeObject<AdvancedItemConfig>(_jsonBox.Text);
            UpdateJsonStatus(true, null);
        }
        catch (Exception ex)
        {
            UpdateJsonStatus(false, ex.Message);
        }
    }

    private void UpdateJsonStatus(bool valid, string? error)
    {
        if (valid)
        {
            _jsonStatus.Text = Framework.Lang.T("advanced.jsonValid") + " · " + Framework.Lang.T("advanced.jsonSyncHint");
            _jsonStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
        }
        else
        {
            _jsonStatus.Text = Framework.Lang.T("advanced.jsonInvalid") + (string.IsNullOrEmpty(error) ? "" : "：" + error);
            _jsonStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    // ===== 模式切换 =====

    private void SwitchMode(bool toJson)
    {
        if (toJson == _jsonMode) return;

        if (toJson)
        {
            ReadCurrentForm();
            ReadRightPanelMeta();
            _jsonBox.Text = _cfg.ToJson();
            ValidateJsonSilent();
            _simplePanel.Visibility = Visibility.Collapsed;
            _jsonPanel.Visibility = Visibility.Visible;
        }
        else
        {
            if (!TryParseJsonFromBox(out var parsed)) return; // 校验失败停留在 JSON 模式
            _cfg = parsed;
            _actionType = TypeMeta.ContainsKey(_cfg.ActionType) ? _cfg.ActionType : "url";
            FillFormsFromConfig();
            FillRightPanelMeta();
            ShowForm(_actionType);
            _jsonPanel.Visibility = Visibility.Collapsed;
            _simplePanel.Visibility = Visibility.Visible;
        }

        _jsonMode = toJson;
        UpdateModeButtons();
    }

    // ===== 保存 / 试运行 =====

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_jsonMode)
        {
            if (!TryParseJsonFromBox(out var parsed)) return;
            _cfg = parsed;
        }
        else
        {
            ReadCurrentForm();
        }
        ReadRightPanelMeta();

        string json = _cfg.ToJson();
        string name = _nameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            name = Framework.Lang.T(TypeMeta[_actionType].NameKey);

        string? icon = string.IsNullOrWhiteSpace(_iconBox.Text)
            ? (TypeMeta.TryGetValue(_cfg.ActionType ?? "", out var iconMeta) ? iconMeta.Emoji : "⚡")   // 未设图标时用动作类型 emoji 兜底，避免面板显示空白/通用链接图标
            : _iconBox.Text.Trim();

        if (_existing != null)
        {
            _existing.Name = name;
            _existing.Icon = icon;
            _existing.BackgroundColor = _selectedColor;
            _existing.AdvancedConfigJson = json;
        }
        else
        {
            Result = new QuickItem
            {
                Name = name,
                Icon = icon,
                Type = "advanced",
                Target = "",
                BackgroundColor = _selectedColor,
                AdvancedConfigJson = json
            };
        }

        // 所属团队（仅登录时展示）：选择结果回写到所属分组（团队信息存于分组级）
        if (_teamCombo != null && !string.IsNullOrEmpty(_groupId))
        {
            var group = QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == _groupId);
            if (group != null)
            {
                var selTeam = _teamCombo.SelectedItem as ComboBoxItem;
                group.TeamId = selTeam?.Tag as string;
                group.TeamName = string.IsNullOrEmpty(group.TeamId) ? null : selTeam?.Content?.ToString();
                group.OwnerType = string.IsNullOrEmpty(group.TeamId) ? "personal" : "team";
                QuickGroupManager.UpdateGroup(group);
                QuickGroupManager.Save();
            }
        }

        DialogResult = true;
        Close();
    }

    private async System.Threading.Tasks.Task TestRunAsync()
    {
        AdvancedItemConfig cfg;
        if (_jsonMode)
        {
            if (!TryParseJsonFromBox(out var parsed)) return;
            cfg = parsed;
        }
        else
        {
            ReadCurrentForm();
            ReadRightPanelMeta();
            cfg = _cfg;
        }

        var temp = new QuickItem { Type = "advanced", AdvancedConfigJson = cfg.ToJson() };
        await Services.AdvancedActionService.ExecuteAsync(temp);
    }
}
