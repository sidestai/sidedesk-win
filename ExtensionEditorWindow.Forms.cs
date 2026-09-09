using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Controls;

namespace SideDesk;

/// <summary>
/// ExtensionEditorWindow 的类型专属表单构建与配置读写部分。
/// </summary>
public partial class ExtensionEditorWindow
{
    // ===== url =====
    private TextBox _fUrlBox = null!;
    private ComboBox _fBrowserCombo = null!;
    private CheckBox _fAppModeCheck = null!;
    private ComboBox _fProfileCombo = null!;
    // ===== exec =====
    private TextBox _fPathBox = null!;
    private TextBox _fArgsBox = null!;
    private TextBox _fWorkDirBox = null!;
    private ComboBox _fLaunchModeCombo = null!;
    // ===== keys =====
    private StackPanel _fKeySeqPanel = null!;
    private TextBox _fIntervalBox = null!;
    private TextBox _fDelayBox = null!;
    // ===== paste =====
    private TextBox _fPasteTextBox = null!;
    private ComboBox _fPasteModeCombo = null!;
    private ComboBox _fRestoreCombo = null!;
    // ===== script =====
    private ComboBox _fRuntimeCombo = null!;
    private TextBox _fScriptCodeBox = null!;
    private ComboBox _fOutputCombo = null!;
    private TextBox _fTimeoutBox = null!;
    // ===== clipboard =====
    private TextBox _fClipboardTextBox = null!;
    private ComboBox _fClipNotifyCombo = null!;
    // ===== search =====
    private ComboBox _fSearchPresetCombo = null!;
    private TextBox _fSearchTemplateBox = null!;
    // ===== 高级选项 =====
    private TextBox _fHotkeyBox = null!;

    private static readonly string[] LaunchModes = { "normal", "admin", "minimized", "hidden" };
    private static readonly string[] PasteModes = { "ctrlv", "type", "clipboard" };
    private static readonly string[] ScriptRuntimes = { "powershell", "cmd" };
    private static readonly string[] ScriptOutputs = { "notify", "paste", "copy", "none" };
    private static readonly string[] SearchTemplates =
    {
        "https://www.google.com/search?q={query}",
        "https://www.bing.com/search?q={query}",
        "https://www.baidu.com/s?wd={query}",
        "https://github.com/search?q={query}"
    };

    // ===== 通用构建辅助 =====

    private Emoji.Wpf.TextBlock MakeLabel(string text) => new Emoji.Wpf.TextBlock
    {
        Text = text,
        FontSize = (double)FindResource("FontSizeNormal"),
        Foreground = (Brush)FindResource("BrushTextMain"),
        Margin = new Thickness(0, 0, 0, 6)
    };

    private TextBox MakeInput(string initial = "") => new TextBox
    {
        Text = initial,
        Style = (Style)FindResource("GlobalTextBoxStyle")
    };

    private TextBox MakeMultilineInput(string initial = "", double minHeight = 80) => new TextBox
    {
        Text = initial,
        Style = (Style)FindResource("GlobalTextBoxStyle"),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = minHeight,
        // 覆盖样式的垂直居中，多行文本从顶部开始显示；上下内边距留白避免贴边（模板把 Padding 用作内容 Margin）
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(6, 8, 6, 8)
    };

    private ComboBox MakeCombo(params string[] items)
    {
        var combo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle") };
        foreach (var item in items) combo.Items.Add(item);
        if (items.Length > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static TextBlock MakeHint(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = Brushes.Gray,
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    private Button MakeGhostButton(string content) => new Button
    {
        Content = content,
        Padding = new Thickness(10, 4, 10, 4),
        Style = (Style)FindResource("GhostButtonStyle"),
        HorizontalAlignment = HorizontalAlignment.Left
    };

    /// <summary>按键录制输入框：只读，聚焦后按下组合键即录入，禁止普通输入与文本选择</summary>
    private TextBox MakeKeyCaptureBox(string initial = "")
    {
        var box = MakeInput(initial);
        box.IsReadOnly = true;
        box.IsUndoEnabled = false;
        box.PreviewKeyDown += (s, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;   // 拦截 TextBox 自身的按键处理（光标移动/输入等）
            if (key == Key.None || IsModifierKey(key)) return;  // 仅按下修饰键时等待实际按键

            var parts = new List<string>();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(GetKeyToken(key));
            box.Text = string.Join("+", parts);
            box.Select(box.Text.Length, 0);
        };
        // 禁止文本选择：选择一旦产生立即收起光标到末尾
        box.SelectionChanged += (s, e) =>
        {
            if (box.SelectionLength > 0) box.Select(box.Text.Length, 0);
        };
        // 禁止粘贴覆盖
        DataObject.AddPastingHandler(box, (s, e) => e.CancelCommand());
        return box;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    /// <summary>按键录入名（与执行引擎 ParseKeySequence 兼容：枚举名/别名/单字符）</summary>
    private static string GetKeyToken(Key key)
    {
        // 数字主键盘键显示为 0-9（引擎按单字符解析为对应 VK）
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key.ToString();
    }

    private StackPanel MakeForm(string type)
    {
        var form = new StackPanel();
        _formPanels[type] = form;
        _formHost.Children.Add(form);
        form.Visibility = Visibility.Collapsed;
        return form;
    }

    private void AddField(StackPanel form, string labelKey, UIElement control, string? hintKey = null)
    {
        form.Children.Add(MakeLabel(Framework.Lang.T(labelKey)));
        form.Children.Add(control);
        if (hintKey != null) form.Children.Add(MakeHint(Framework.Lang.T(hintKey)));
        form.Children.Add(new Border { Height = 12 });
    }

    // ===== 表单构建 =====

    private void BuildAllForms()
    {
        // ---------- url ----------
        var urlForm = MakeForm("url");
        urlForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "🌐 " + Framework.Lang.T("advanced.typeUrl"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fUrlBox = MakeInput("https://");
        AddField(urlForm, "advanced.urlLabel", _fUrlBox, "advanced.queryHint");
        _fBrowserCombo = MakeCombo();
        _fBrowserCombo.Items.Add(Framework.Lang.T("common.defaultBrowser"));
        foreach (var browser in ((App)Application.Current).ProgramRegistry.Browsers)
            _fBrowserCombo.Items.Add(browser.Name);
        _fBrowserCombo.SelectedIndex = 0;
        AddField(urlForm, "advanced.browserLabel", _fBrowserCombo);

        // APP 模式开关（默认浏览器时执行引擎回退到 Edge）
        _fAppModeCheck = new CheckBox
        {
            Content = Framework.Lang.T("advanced.appMode"),
            Foreground = (Brush)FindResource("BrushTextMain"),
            FontSize = (double)FindResource("FontSizeNormal"),
            Margin = new Thickness(0, 2, 0, 10)
        };
        urlForm.Children.Add(_fAppModeCheck);

        // Profile 下拉（随浏览器切换重建，可选；默认浏览器时列出已保存的通用 Profile）
        _fProfileCombo = MakeCombo();
        AddField(urlForm, "advanced.profileLabel", _fProfileCombo);
        RefreshProfileCombo(resetSelection: true);
        _fBrowserCombo.SelectionChanged += (s, e) => RefreshProfileCombo(resetSelection: true);

        // ---------- exec ----------
        var execForm = MakeForm("exec");
        execForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "🚀 " + Framework.Lang.T("advanced.typeExec"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fPathBox = MakeInput();
        var pathDock = new DockPanel();
        var browseBtn = MakeGhostButton(Framework.Lang.T("advanced.browse"));
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
        browseBtn.Click += BrowsePath_Click;
        DockPanel.SetDock(browseBtn, Dock.Right);
        pathDock.Children.Add(browseBtn);
        pathDock.Children.Add(_fPathBox);
        AddField(execForm, "advanced.programPath", pathDock, "advanced.programPathHint");
        _fArgsBox = MakeInput();
        AddField(execForm, "advanced.launchArgs", _fArgsBox);
        _fWorkDirBox = MakeInput();
        AddField(execForm, "advanced.workingDir", _fWorkDirBox, "advanced.workingDirHint");
        _fLaunchModeCombo = MakeCombo(
            Framework.Lang.T("advanced.launchNormal"),
            Framework.Lang.T("advanced.launchAdmin"),
            Framework.Lang.T("advanced.launchMinimized"),
            Framework.Lang.T("advanced.launchHidden"));
        AddField(execForm, "advanced.launchMode", _fLaunchModeCombo);

        // ---------- keys ----------
        var keysForm = MakeForm("keys");
        keysForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "⌨️ " + Framework.Lang.T("advanced.typeKeys"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        keysForm.Children.Add(MakeLabel(Framework.Lang.T("advanced.keySequences")));
        keysForm.Children.Add(MakeHint(Framework.Lang.T("advanced.keyCaptureHint")));
        _fKeySeqPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        keysForm.Children.Add(_fKeySeqPanel);
        var addKeyBtn = MakeGhostButton("＋ " + Framework.Lang.T("advanced.addKeyStep"));
        addKeyBtn.Margin = new Thickness(0, 8, 0, 12);
        addKeyBtn.Click += (s, e) => AddKeySequenceRow("");
        keysForm.Children.Add(addKeyBtn);
        var intervalDock = new DockPanel();
        _fIntervalBox = MakeInput("50");
        _fIntervalBox.Width = 120;
        intervalDock.Children.Add(_fIntervalBox);
        intervalDock.Children.Add(new TextBlock
        {
            Text = " ms",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("BrushTextSec"),
            Margin = new Thickness(6, 0, 0, 0)
        });
        AddField(keysForm, "advanced.stepInterval", intervalDock);
        var delayDock = new DockPanel();
        _fDelayBox = MakeInput("100");
        _fDelayBox.Width = 120;
        delayDock.Children.Add(_fDelayBox);
        delayDock.Children.Add(new TextBlock
        {
            Text = " ms",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("BrushTextSec"),
            Margin = new Thickness(6, 0, 0, 0)
        });
        AddField(keysForm, "advanced.triggerDelay", delayDock, "advanced.waitFocusHint");

        // ---------- paste ----------
        var pasteForm = MakeForm("paste");
        pasteForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "📋 " + Framework.Lang.T("advanced.typePaste"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fPasteTextBox = MakeMultilineInput();
        AddField(pasteForm, "advanced.pasteContent", _fPasteTextBox);
        _fPasteModeCombo = MakeCombo(
            Framework.Lang.T("advanced.pasteModeCtrlV"),
            Framework.Lang.T("advanced.pasteModeType"),
            Framework.Lang.T("advanced.pasteModeClipboard"));
        AddField(pasteForm, "advanced.pasteMode", _fPasteModeCombo);
        _fRestoreCombo = MakeCombo(
            Framework.Lang.T("advanced.restoreClipboard"),
            Framework.Lang.T("advanced.keepClipboard"));
        AddField(pasteForm, "advanced.afterDone", _fRestoreCombo);

        // ---------- script ----------
        var scriptForm = MakeForm("script");
        scriptForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "🖥️ " + Framework.Lang.T("advanced.typeScript"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fRuntimeCombo = MakeCombo("PowerShell", "CMD");
        AddField(scriptForm, "advanced.runtime", _fRuntimeCombo);
        _fScriptCodeBox = new TextBox
        {
            Style = (Style)FindResource("GlobalTextBoxStyle"),
            FontFamily = new FontFamily("Consolas"),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 160
        };
        AddField(scriptForm, "advanced.scriptCode", _fScriptCodeBox, "advanced.queryHint");
        _fOutputCombo = MakeCombo(
            Framework.Lang.T("advanced.outputNotify"),
            Framework.Lang.T("advanced.outputPaste"),
            Framework.Lang.T("advanced.outputCopy"),
            Framework.Lang.T("advanced.outputNone"));
        AddField(scriptForm, "advanced.outputHandle", _fOutputCombo);
        var timeoutDock = new DockPanel();
        _fTimeoutBox = MakeInput("10");
        _fTimeoutBox.Width = 120;
        timeoutDock.Children.Add(_fTimeoutBox);
        timeoutDock.Children.Add(new TextBlock
        {
            Text = " s",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("BrushTextSec"),
            Margin = new Thickness(6, 0, 0, 0)
        });
        AddField(scriptForm, "advanced.timeout", timeoutDock);

        // ---------- clipboard ----------
        var clipForm = MakeForm("clipboard");
        clipForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "📎 " + Framework.Lang.T("advanced.typeClipboard"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fClipboardTextBox = MakeMultilineInput();
        AddField(clipForm, "advanced.content", _fClipboardTextBox);
        _fClipNotifyCombo = MakeCombo(
            Framework.Lang.T("advanced.bubbleNotify"),
            Framework.Lang.T("advanced.silentCopy"));
        AddField(clipForm, "advanced.doneHint", _fClipNotifyCombo);

        // ---------- search ----------
        var searchForm = MakeForm("search");
        searchForm.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = "🔍 " + Framework.Lang.T("advanced.typeSearch"),
            FontSize = (double)FindResource("FontSizeSubtitle"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushTextMain"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        _fSearchPresetCombo = MakeCombo("Google", "Bing", Framework.Lang.T("advanced.baidu"), "GitHub", Framework.Lang.T("advanced.custom"));
        _fSearchPresetCombo.SelectionChanged += (s, e) =>
        {
            int idx = _fSearchPresetCombo.SelectedIndex;
            if (idx >= 0 && idx < SearchTemplates.Length)
                _fSearchTemplateBox.Text = SearchTemplates[idx];
        };
        AddField(searchForm, "advanced.searchTemplate", _fSearchPresetCombo);
        _fSearchTemplateBox = MakeInput(SearchTemplates[0]);
        AddField(searchForm, "advanced.searchTemplateUrl", _fSearchTemplateBox, "advanced.queryHint");
    }

    /// <summary>按键序列行：输入框 + 删除按钮</summary>
    private void AddKeySequenceRow(string seq)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var delBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)FindResource("GhostButtonStyle")
        };
        DockPanel.SetDock(delBtn, Dock.Right);
        var box = MakeKeyCaptureBox(seq);
        row.Children.Add(delBtn);
        row.Children.Add(box);
        delBtn.Click += (s, e) => _fKeySeqPanel.Children.Remove(row);
        _fKeySeqPanel.Children.Add(row);
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Framework.Lang.T("advanced.programFilter")
        };
        if (dlg.ShowDialog(this) == true)
            _fPathBox.Text = dlg.FileName;
    }

    // ===== 高级选项折叠区 =====

    private UIElement BuildAdvancedOptionsSection()
    {
        var section = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var header = new Button
        {
            Content = "▸ " + Framework.Lang.T("advanced.advancedOptions"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Style = (Style)FindResource("GhostButtonStyle")
        };
        var body = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        bool expanded = false;
        header.Click += (s, e) =>
        {
            expanded = !expanded;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            header.Content = (expanded ? "▾ " : "▸ ") + Framework.Lang.T("advanced.advancedOptions");
        };

        _fHotkeyBox = MakeKeyCaptureBox();
        body.Children.Add(MakeLabel(Framework.Lang.T("advanced.globalHotkey")));
        body.Children.Add(_fHotkeyBox);
        body.Children.Add(MakeHint(Framework.Lang.T("advanced.globalHotkeyHint")));

        section.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("BrushBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 8, 0, 8)
        });
        section.Children.Add(header);
        section.Children.Add(body);
        return section;
    }

    // ===== 右侧栏 =====

    private UIElement BuildRightPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        // 实时预览
        panel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("advanced.livePreview"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        _previewIcon = new Emoji.Wpf.TextBlock { Text = "🌐", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        _previewIconBg = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor)!),
            Child = new Viewbox { Child = _previewIcon, Margin = new Thickness(8) }
        };
        _previewName = new Emoji.Wpf.TextBlock
        {
            Text = Framework.Lang.T("advanced.typeUrl"),
            FontSize = (double)FindResource("FontSizeNormal"),
            Foreground = (Brush)FindResource("BrushTextMain")
        };
        _previewDesc = new TextBlock
        {
            Text = "",
            FontSize = (double)FindResource("FontSizeSmall"),
            Foreground = (Brush)FindResource("BrushTextMuted"),
            TextWrapping = TextWrapping.Wrap
        };
        var previewText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        previewText.Children.Add(_previewName);
        previewText.Children.Add(_previewDesc);
        var previewDock = new DockPanel();
        DockPanel.SetDock(_previewIconBg, Dock.Left);
        previewDock.Children.Add(_previewIconBg);
        previewDock.Children.Add(previewText);
        panel.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 16),
            Child = previewDock
        });

        // 基础信息
        panel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("advanced.basicInfo"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(MakeLabel(Framework.Lang.T("advanced.nameLabel")));
        _nameBox = new EmojiTextBox();
        _nameBox.TextChanged += (s, e) => UpdatePreview();
        panel.Children.Add(_nameBox);
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(MakeLabel(Framework.Lang.T("advanced.descLabel")));
        _descBox = MakeInput();
        _descBox.TextChanged += (s, e) => UpdatePreview();
        panel.Children.Add(_descBox);
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(MakeLabel(Framework.Lang.T("advanced.keywordsLabel")));
        _keywordsBox = MakeInput();
        panel.Children.Add(_keywordsBox);

        // 所属团队（仅登录后展示）：团队信息存于分组级，保存时回写所属分组
        if (!string.IsNullOrEmpty(((App)Application.Current).AppSettings.AccountToken))
        {
            panel.Children.Add(new Border { Height = 10 });
            panel.Children.Add(MakeLabel(Framework.Lang.T("common.affiliatedTeam")));
            _teamCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
                IsEditable = false
            };
            _teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("common.personal"), Tag = (string?)null });
            _teamCombo.SelectedIndex = 0;
            panel.Children.Add(_teamCombo);

            // 异步拉取团队列表，加载完成后恢复当前分组所属团队的选中项
            string? pendingTeamId = QuickGroupManager.GetAll().FirstOrDefault(g => g.Id == _groupId)?.TeamId;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var teams = await Services.TeamService.Instance.ListTeamsAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_teamCombo == null || _teamCombo.Items.Count > 1) return; // 已加载过，避免重复
                        if (teams != null)
                        {
                            foreach (var t in teams)
                                _teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
                        }
                        if (!string.IsNullOrEmpty(pendingTeamId))
                        {
                            for (int i = 1; i < _teamCombo.Items.Count; i++)
                            {
                                if ((_teamCombo.Items[i] as ComboBoxItem)?.Tag as string == pendingTeamId)
                                { _teamCombo.SelectedIndex = i; break; }
                            }
                        }
                    });
                }
                catch { }
            });
        }
        panel.Children.Add(new Border { Height = 16 });

        // 外观
        panel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("advanced.appearance"),
            FontSize = (double)FindResource("FontSizeSmall"),
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(MakeLabel(Framework.Lang.T("advanced.iconLabel")));
        var iconDock = new DockPanel();
        _iconPreview = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(_iconPreview, Dock.Right);
        _iconBox = MakeInput();
        _iconBox.TextChanged += (s, e) => { UpdateIconPreview(); UpdatePreview(); };
        iconDock.Children.Add(_iconPreview);
        iconDock.Children.Add(_iconBox);
        panel.Children.Add(iconDock);
        panel.Children.Add(new Border { Height = 12 });

        panel.Children.Add(MakeLabel(Framework.Lang.T("advanced.themeColor")));
        _colorPanel = new WrapPanel();
        InitColorPicker();
        panel.Children.Add(_colorPanel);

        return panel;
    }

    private void InitColorPicker()
    {
        var accentBrush = (Brush)FindResource("BrushAccent");
        foreach (var color in _presetColors)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
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
                foreach (UIElement child in _colorPanel.Children)
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                swatch.BorderBrush = accentBrush;
                UpdatePreview();
            };
            _colorPanel.Children.Add(swatch);
        }
    }

    private void UpdateIconPreview()
    {
        string val = _iconBox.Text.Trim();
        if (string.IsNullOrEmpty(val))
        {
            _iconPreview.Content = null;
            return;
        }
        if (FaviconService.IsPathIcon(val))
        {
            var src = FaviconService.LoadFileIcon(val);
            _iconPreview.Content = src != null
                ? new Image { Source = src, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center }
                : null;
        }
        else
        {
            _iconPreview.Content = new Emoji.Wpf.TextBlock { Text = val, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    private void UpdatePreview()
    {
        try
        {
            _previewIconBg.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(_selectedColor)!;
        }
        catch { }
        _previewIcon.Text = string.IsNullOrWhiteSpace(_iconBox?.Text ?? "")
            ? TypeMeta[_actionType].Emoji
            : _iconBox.Text.Trim();
        string name = _nameBox?.Text?.Trim() ?? "";
        _previewName.Text = string.IsNullOrEmpty(name) ? Framework.Lang.T(TypeMeta[_actionType].NameKey) : name;
        _previewDesc.Text = _descBox?.Text?.Trim() ?? "";
    }

    // ===== 初始化 / 读写 =====

    /// <summary>编辑模式回填 或 新建模式默认值</summary>
    private void InitRightPanelDefaults()
    {
        if (_existing != null)
        {
            _nameBox.Text = _existing.Name ?? "";
            _iconBox.Text = _existing.Icon ?? "";
        }
        _descBox.Text = _cfg.Description ?? "";
        _keywordsBox.Text = _cfg.Keywords ?? "";
        FillFormsFromConfig();
        UpdateIconPreview();
        UpdatePreview();
    }

    /// <summary>把当前类型表单的控件值读入 _cfg</summary>
    private void ReadCurrentForm()
    {
        _cfg.ActionType = _actionType;
        switch (_actionType)
        {
            case "url":
                _cfg.Url = _fUrlBox.Text.Trim();
                _cfg.BrowserId = GetBrowserIdByComboIndex(_fBrowserCombo.SelectedIndex);
                _cfg.UseAppMode = _fAppModeCheck.IsChecked == true;
                _cfg.ProfileId = GetProfileIdByComboIndex();
                break;
            case "exec":
                _cfg.ProgramPath = _fPathBox.Text.Trim();
                _cfg.LaunchArgs = _fArgsBox.Text.Trim();
                _cfg.WorkingDir = _fWorkDirBox.Text.Trim();
                _cfg.LaunchMode = LaunchModes[Math.Clamp(_fLaunchModeCombo.SelectedIndex, 0, LaunchModes.Length - 1)];
                break;
            case "keys":
                _cfg.KeySequences = _fKeySeqPanel.Children.OfType<DockPanel>()
                    .Select(row => row.Children.OfType<TextBox>().FirstOrDefault())
                    .Where(box => box != null && !string.IsNullOrWhiteSpace(box!.Text))
                    .Select(box => box!.Text.Trim())
                    .ToList();
                _cfg.StepIntervalMs = ParseInt(_fIntervalBox.Text, 50);
                _cfg.TriggerDelayMs = ParseInt(_fDelayBox.Text, 100);
                break;
            case "paste":
                _cfg.PasteText = _fPasteTextBox.Text;
                _cfg.PasteMode = PasteModes[Math.Clamp(_fPasteModeCombo.SelectedIndex, 0, PasteModes.Length - 1)];
                _cfg.RestoreClipboard = _fRestoreCombo.SelectedIndex <= 0;
                break;
            case "script":
                _cfg.ScriptRuntime = ScriptRuntimes[Math.Clamp(_fRuntimeCombo.SelectedIndex, 0, ScriptRuntimes.Length - 1)];
                _cfg.ScriptCode = _fScriptCodeBox.Text;
                _cfg.ScriptOutput = ScriptOutputs[Math.Clamp(_fOutputCombo.SelectedIndex, 0, ScriptOutputs.Length - 1)];
                _cfg.ScriptTimeoutSec = ParseInt(_fTimeoutBox.Text, 10);
                break;
            case "clipboard":
                _cfg.ClipboardText = _fClipboardTextBox.Text;
                _cfg.ClipboardNotify = _fClipNotifyCombo.SelectedIndex <= 0;
                break;
            case "search":
                _cfg.SearchTemplate = _fSearchTemplateBox.Text.Trim();
                break;
        }
        _cfg.Hotkey = _fHotkeyBox.Text.Trim();
    }

    /// <summary>右侧栏元数据读入 _cfg（描述/关键词）</summary>
    private void ReadRightPanelMeta()
    {
        _cfg.Description = string.IsNullOrWhiteSpace(_descBox.Text) ? null : _descBox.Text.Trim();
        _cfg.Keywords = string.IsNullOrWhiteSpace(_keywordsBox.Text) ? null : _keywordsBox.Text.Trim();
    }

    /// <summary>用 _cfg 回填所有表单控件（JSON 往返 / 编辑模式）</summary>
    private void FillFormsFromConfig()
    {
        _fUrlBox.Text = string.IsNullOrEmpty(_cfg.Url) ? "https://" : _cfg.Url;
        _fBrowserCombo.SelectedIndex = GetBrowserComboIndex(_cfg.BrowserId);
        _fAppModeCheck.IsChecked = _cfg.UseAppMode;
        RefreshProfileCombo(resetSelection: false);
        SelectProfileInCombo(_cfg.ProfileId);

        _fPathBox.Text = _cfg.ProgramPath ?? "";
        _fArgsBox.Text = _cfg.LaunchArgs ?? "";
        _fWorkDirBox.Text = _cfg.WorkingDir ?? "";
        _fLaunchModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(LaunchModes, _cfg.LaunchMode));

        _fKeySeqPanel.Children.Clear();
        foreach (var seq in _cfg.KeySequences ?? new List<string>())
            AddKeySequenceRow(seq);
        if (_fKeySeqPanel.Children.Count == 0) AddKeySequenceRow("");
        _fIntervalBox.Text = _cfg.StepIntervalMs.ToString();
        _fDelayBox.Text = _cfg.TriggerDelayMs.ToString();

        _fPasteTextBox.Text = _cfg.PasteText ?? "";
        _fPasteModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PasteModes, _cfg.PasteMode));
        _fRestoreCombo.SelectedIndex = _cfg.RestoreClipboard ? 0 : 1;

        _fRuntimeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScriptRuntimes, _cfg.ScriptRuntime));
        _fScriptCodeBox.Text = _cfg.ScriptCode ?? "";
        _fOutputCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScriptOutputs, _cfg.ScriptOutput));
        _fTimeoutBox.Text = _cfg.ScriptTimeoutSec.ToString();

        _fClipboardTextBox.Text = _cfg.ClipboardText ?? "";
        _fClipNotifyCombo.SelectedIndex = _cfg.ClipboardNotify ? 0 : 1;

        int presetIdx = Array.IndexOf(SearchTemplates, _cfg.SearchTemplate ?? "");
        _fSearchPresetCombo.SelectedIndex = presetIdx >= 0 ? presetIdx : SearchTemplates.Length; // 自定义
        _fSearchTemplateBox.Text = string.IsNullOrEmpty(_cfg.SearchTemplate) ? SearchTemplates[0] : _cfg.SearchTemplate;

        _fHotkeyBox.Text = _cfg.Hotkey ?? "";
    }

    /// <summary>JSON 往返后回填右侧栏元数据</summary>
    private void FillRightPanelMeta()
    {
        _descBox.Text = _cfg.Description ?? "";
        _keywordsBox.Text = _cfg.Keywords ?? "";
    }

    private string? GetBrowserIdByComboIndex(int index)
    {
        if (index <= 0) return null;
        var browsers = ((App)Application.Current).ProgramRegistry.Browsers;
        int browserIndex = index - 1;
        return browserIndex >= 0 && browserIndex < browsers.Count ? browsers[browserIndex].Id : null;
    }

    private int GetBrowserComboIndex(string? browserId)
    {
        if (string.IsNullOrEmpty(browserId)) return 0;
        var browsers = ((App)Application.Current).ProgramRegistry.Browsers;
        int idx = browsers.FindIndex(b => b.Id == browserId);
        return idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>当前选中的浏览器（默认浏览器/未选时返回 null）</summary>
    private SideDesk.Models.BrowserConfig? GetSelectedBrowser()
    {
        int idx = _fBrowserCombo.SelectedIndex - 1;
        var browsers = ((App)Application.Current).ProgramRegistry.Browsers;
        return idx >= 0 && idx < browsers.Count ? browsers[idx] : null;
    }

    /// <summary>当前浏览器的 Profile 存储 Id 列表（指定浏览器=原始 Id；默认浏览器=通用 Profile 加 "legacy:" 前缀）</summary>
    private List<(string Name, string StoredId)> GetProfileOptions()
    {
        var browser = GetSelectedBrowser();
        if (browser != null)
            return browser.Profiles.Select(p => (p.Name, p.Id)).ToList();
        return BrowserProfileManager.GetAllProfiles().Select(p => (p.Name, "legacy:" + p.Id)).ToList();
    }

    /// <summary>按当前浏览器重建 Profile 下拉（首项=不使用）</summary>
    private void RefreshProfileCombo(bool resetSelection)
    {
        string? keepId = resetSelection ? null : GetProfileIdByComboIndex();
        _fProfileCombo.Items.Clear();
        _fProfileCombo.Items.Add(Framework.Lang.T("advanced.noProfile"));
        foreach (var (name, _) in GetProfileOptions())
            _fProfileCombo.Items.Add(name);
        _fProfileCombo.SelectedIndex = 0;
        if (!resetSelection) SelectProfileInCombo(keepId);
    }

    /// <summary>把存储的 ProfileId 映射到下拉选中项，找不到保持首项</summary>
    private void SelectProfileInCombo(string? profileId)
    {
        if (string.IsNullOrEmpty(profileId)) { _fProfileCombo.SelectedIndex = 0; return; }
        var options = GetProfileOptions();
        int idx = options.FindIndex(o => o.StoredId == profileId);
        _fProfileCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>下拉选中项 → 存储用 ProfileId（首项返回 null）</summary>
    private string? GetProfileIdByComboIndex()
    {
        var options = GetProfileOptions();
        int idx = _fProfileCombo.SelectedIndex - 1;
        return idx >= 0 && idx < options.Count ? options[idx].StoredId : null;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text?.Trim(), out int v) ? Math.Max(0, v) : fallback;
}
