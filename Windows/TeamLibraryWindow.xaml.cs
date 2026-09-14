using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk.Windows;

/// <summary>
/// 团队组件库窗口：浏览团队共享的模块并添加到当前窗口（数据共享互通）
/// </summary>
public partial class TeamLibraryWindow : Window
{
    /// <summary>添加模块时的回调（将构造好的 PanelItem 传回调用方）</summary>
    public Action<PanelItem>? OnAddItem { get; set; }

    /// <summary>加载序号令牌：快速切换团队时丢弃过期加载结果，避免旧结果覆盖新列表</summary>
    private int _loadToken;

    public TeamLibraryWindow()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        Loaded += async (_, _) => await InitTeamSelectorAsync();
        teamCombo.SelectionChanged += async (_, _) =>
        {
            if ((teamCombo.SelectedItem as ComboBoxItem)?.Tag is string corpId)
                await LoadLibraryAsync(corpId);
        };
    }

    /// <summary>拉取团队列表填充切换下拉；默认选中设置里的当前团队，未设置则选第一个，随后加载其组件库</summary>
    private async System.Threading.Tasks.Task InitTeamSelectorAsync()
    {
        listPanel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("team.loadingTeamList"),
            FontSize = 12,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var teams = await TeamService.Instance.ListTeamsAsync();
        if (teams == null || teams.Count == 0)
        {
            listPanel.Children.Clear();
            listPanel.Children.Add(BuildHint(Framework.Lang.T("team.noTeamsToSwitch")));
            teamCombo.IsEnabled = false;
            return;
        }

        foreach (var t in teams)
            teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });

        // 默认选中设置里的当前团队；未设置时选第一个
        var app = (App)Application.Current;
        string? selectedCorpId = app.AppSettings.SelectedCorpId;
        int selIdx = 0;
        if (!string.IsNullOrEmpty(selectedCorpId))
        {
            for (int i = 0; i < teamCombo.Items.Count; i++)
            {
                if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == selectedCorpId)
                { selIdx = i; break; }
            }
        }
        teamCombo.SelectedIndex = selIdx; // 触发 SelectionChanged 加载该团队的组件列表
    }

    private async System.Threading.Tasks.Task LoadLibraryAsync(string corpId)
    {
        int token = ++_loadToken;
        listPanel.Children.Clear();

        listPanel.Children.Add(new TextBlock
        {
            Text = Framework.Lang.T("common.loading"),
            FontSize = 12,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var items = await TeamService.Instance.GetTeamLibraryAsync(corpId);

        // 期间又切换了团队，丢弃过期结果
        if (token != _loadToken) return;

        listPanel.Children.Clear();

        if (items == null)
        {
            listPanel.Children.Add(BuildHint(Framework.Lang.T("team.loadFailedLoginHint")));
            return;
        }

        if (items.Count == 0)
        {
            listPanel.Children.Add(BuildHint(Framework.Lang.T("team.noTeamSharedComponents")));
            return;
        }

        foreach (var libItem in items)
        {
            listPanel.Children.Add(BuildItemRow(libItem));
        }
    }

    private UIElement BuildHint(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
    }

    private UIElement BuildItemRow(TeamLibraryItem libItem)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("BrushInputBG"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var dock = new DockPanel();

        // 右侧：添加按钮
        var addBtn = new Button
        {
            Content = Framework.Lang.T("common.add"),
            Width = 56,
            MinHeight = 26,
            FontSize = 11,
            Style = (Style)FindResource("PrimaryButtonStyle"),
            Tag = libItem
        };
        addBtn.Click += AddBtn_Click;
        DockPanel.SetDock(addBtn, Dock.Right);
        dock.Children.Add(addBtn);

        // 左侧：图标 + 信息
        var icon = new TextBlock
        {
            Text = GetTypeIcon(libItem.ItemType),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        dock.Children.Add(icon);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 名称行：标题 + "我的"标记
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = string.IsNullOrEmpty(libItem.Title) ? GetTypeName(libItem.ItemType) : libItem.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("BrushTextMain")
        });
        if (libItem.IsMine)
        {
            namePanel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("common.mine"),
                FontSize = 10,
                Foreground = (Brush)FindResource("BrushAccent"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        infoPanel.Children.Add(namePanel);

        // 副标题：类型 + 创建者
        string sub = GetTypeName(libItem.ItemType);
        if (!string.IsNullOrEmpty(libItem.CreatorName))
            sub += $" · {libItem.CreatorName}";
        infoPanel.Children.Add(new TextBlock
        {
            Text = sub,
            FontSize = 11,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        dock.Children.Add(infoPanel);
        border.Child = dock;
        return border;
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TeamLibraryItem libItem) return;

        // 构造本地 PanelItem：引用同一条服务端记录（数据共享）；TeamId 为团队加密串
        var panel = new PanelItem
        {
            ServerId = libItem.ItemServerId,
            OwnerType = "team",
            TeamId = libItem.CorpId,
            Title = libItem.Title
        };

        // 解析模块类型
        if (Enum.TryParse<PanelType>(libItem.ItemType, out var panelType))
            panel.Type = panelType;

        // 用服务端 itemData 还原模块级设置（列表数据通过行级拉取获取）
        if (!string.IsNullOrEmpty(libItem.ItemData))
        {
            try
            {
                var id = panel.Id;
                var serverId = panel.ServerId;
                var type = panel.Type;
                var ownerType = panel.OwnerType;
                var teamId = panel.TeamId;

                Newtonsoft.Json.JsonConvert.PopulateObject(libItem.ItemData, panel);

                panel.Id = id;
                panel.ServerId = serverId;
                panel.Type = type;
                panel.OwnerType = ownerType;
                panel.TeamId = teamId;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TeamLibrary] failed to restore module data: {ex.Message}");
            }
        }

        OnAddItem?.Invoke(panel);

        // 添加后异步拉取该模块的行级数据（列表内容）
        _ = PullRowsForNewPanel(panel);

        // 标记已添加
        btn.Content = Framework.Lang.T("settings.added");
        btn.IsEnabled = false;
    }

    /// <summary>添加团队模块后，异步拉取其行级数据（列表内容）</summary>
    private async System.Threading.Tasks.Task PullRowsForNewPanel(PanelItem panel)
    {
        try
        {
            var app = (App)Application.Current;
            var settings = app.AppSettings;
            if (string.IsNullOrEmpty(settings.AccountToken) || string.IsNullOrEmpty(panel.ServerId)) return;
            if (!RowChangeTracker.IsRowSyncable(panel.Type)) return;

            string url = $"{Constants.URLs.ServerBaseUrl}/api/sidebar/item-row/pull?itemServerId={Uri.EscapeDataString(panel.ServerId!)}";
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await Services.HttpLog.SendAsync(http, req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            if (root["code"]?.ToObject<int>() != 200) return;
            if (root["data"]?["rows"] is not Newtonsoft.Json.Linq.JArray rowsArr || rowsArr.Count == 0) return;

            // 将行数据还原到对应列表
            var rows = new Newtonsoft.Json.Linq.JArray();
            foreach (var r in rowsArr)
            {
                var content = r["content"] as Newtonsoft.Json.Linq.JObject;
                if (content == null) continue;
                content["Id"] = r["rowId"]?.ToString();
                rows.Add(content);
            }

            string listJson = rows.ToString();
            // 缓存服务端行主键 ID（供后续行级操作使用）
            SideDesk.Services.CloudSyncService.CacheRowServerIds(rowsArr);
            await Dispatcher.InvokeAsync(() =>
            {
                switch (panel.Type)
                {
                    case PanelType.StickyNotes:
                        panel.StickyNotes = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<StickyNote>>(listJson) ?? new();
                        break;
                    case PanelType.TodoList:
                        panel.TodoItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<TodoItem>>(listJson) ?? new();
                        break;
                    case PanelType.Timer:
                        panel.TimerItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<TimerItem>>(listJson) ?? new();
                        break;
                    case PanelType.Countdown:
                        panel.CountdownItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<CountdownItem>>(listJson) ?? new();
                        break;
                    case PanelType.SignalLight:
                        panel.SignalLights = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<SignalLightItem>>(listJson) ?? new();
                        break;
                    case PanelType.Clipboard:
                        panel.ClipboardItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<ClipboardSavedItem>>(listJson) ?? new();
                        break;
                    case PanelType.FileArea:
                        panel.FileItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<FileAreaItem>>(listJson) ?? new();
                        break;
                }
                System.Diagnostics.Debug.WriteLine($"[TeamLibrary] module row data pulled: {panel.Type}, {rowsArr.Count} rows");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamLibrary] failed to pull row data: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>模块类型 -> 图标</summary>
    private static string GetTypeIcon(string itemType) => itemType switch
    {
        "QuickGroup" => "⚡",
        "StickyNotes" => "📝",
        "TodoList" => "✅",
        "Clipboard" => "📋",
        "SystemMonitor" => "📊",
        "UsageStats" => "📈",
        "AIChat" => "🤖",
        "FileArea" => "📁",
        "DataDashboard" => "📉",
        "Timer" => "⏱",
        "Countdown" => "⏳",
        "Pomodoro" => "🍅",
        "SignalLight" => "🚦",
        "CheckIn" => "🎯",
        "ImageCarousel" => "🖼",
        "DailyActivity" => "📅",
        "Calendar" => "🗓",
        "DigitalEmployee" => "👤",
        "Friend" => "💬",
        "Group" => "📦",
        "ScreenTime" => "🕐",
        _ => "🧩"
    };

    /// <summary>模块类型 -> 中文名称</summary>
    private static string GetTypeName(string itemType) => itemType switch
    {
        "QuickGroup" => Framework.Lang.T("common.shortcut"),
        "StickyNotes" => Framework.Lang.T("common.stickyNotes"),
        "TodoList" => Framework.Lang.T("common.todayTodos"),
        "Clipboard" => Framework.Lang.T("common.clipboardFavorites"),
        "SystemMonitor" => Framework.Lang.T("common.systemInfo"),
        "UsageStats" => Framework.Lang.T("common.usageStatistics"),
        "AIChat" => Framework.Lang.T("common.aiChat"),
        "FileArea" => Framework.Lang.T("common.fileArea"),
        "DataDashboard" => Framework.Lang.T("common.dataDashboard"),
        "Timer" => Framework.Lang.T("common.timer"),
        "Countdown" => Framework.Lang.T("common.countdown"),
        "Pomodoro" => Framework.Lang.T("common.pomodoroAlarm"),
        "SignalLight" => Framework.Lang.T("common.signalLight"),
        "CheckIn" => Framework.Lang.T("common.checkIn"),
        "ImageCarousel" => Framework.Lang.T("common.imageCarousel"),
        "DailyActivity" => Framework.Lang.T("common.dailyActivity"),
        "Calendar" => Framework.Lang.T("common.calendar"),
        "DigitalEmployee" => Framework.Lang.T("common.digitalEmployees"),
        "Friend" => Framework.Lang.T("common.friends"),
        "Group" => Framework.Lang.T("common.group"),
        "ScreenTime" => Framework.Lang.T("common.screenTime"),
        _ => itemType
    };
}
