using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>
    /// 设置-我的数据：便签/剪贴板/每日活跃/数据变量/TODO/每日屏幕时长 六类数据的概述卡片，
    /// 点击卡片打开对应的只读详情窗口。
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _myDataBuilt;
        private readonly Dictionary<string, TextBlock> _myDataSummaries = new();

        // ==================== 概述页 ====================

        /// <summary>切换到「我的数据」页时刷新（卡片只构建一次，概述每次重算）</summary>
        private void RefreshMyDataPage()
        {
            if (!_myDataBuilt) BuildMyDataCards();
            UpdateMyDataCards();
        }

        private void BuildMyDataCards()
        {
            _myDataBuilt = true;
            myDataCardsGrid.Children.Clear();
            myDataCardsGrid.Children.Add(BuildMyDataCard("📝", "便签", "sticky"));
            myDataCardsGrid.Children.Add(BuildMyDataCard("📋", "剪贴板", "clipboard"));
            myDataCardsGrid.Children.Add(BuildMyDataCard("🔥", "每日活跃", "daily"));
            myDataCardsGrid.Children.Add(BuildMyDataCard("🔣", "数据变量", "variable"));
            myDataCardsGrid.Children.Add(BuildMyDataCard("✅", "TODO", "todo"));
            myDataCardsGrid.Children.Add(BuildMyDataCard("⏱", "每日屏幕时长", "screentime"));
        }

        private Border BuildMyDataCard(string icon, string title, string kind)
        {
            var summary = new TextBlock
            {
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            _myDataSummaries[kind] = summary;

            var sp = new StackPanel();
            sp.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = $"{icon} {title}",
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain")
            });
            sp.Children.Add(summary);

            var card = new Border
            {
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(6),
                Cursor = Cursors.Hand,
                Tag = kind,
                Child = sp
            };
            card.MouseLeftButtonUp += (s, e) => OpenMyDataDetail(kind);
            return card;
        }

        private void UpdateMyDataCards()
        {
            // 便签
            int noteCount = 0; DateTime? noteLatest = null;
            WalkPanels((w, p) =>
            {
                if (p.StickyNotes == null) return;
                noteCount += p.StickyNotes.Count;
                foreach (var n in p.StickyNotes)
                    if (noteLatest == null || n.UpdatedAt > noteLatest) noteLatest = n.UpdatedAt;
            });
            _myDataSummaries["sticky"].Text = noteCount == 0
                ? "暂无便签"
                : $"{noteCount} 条 · 最近更新 {noteLatest:MM-dd HH:mm}";

            // 剪贴板收藏
            int clipCount = 0; DateTime? clipLatest = null;
            WalkPanels((w, p) =>
            {
                if (p.ClipboardItems == null) return;
                clipCount += p.ClipboardItems.Count;
                foreach (var c in p.ClipboardItems)
                    if (clipLatest == null || c.SavedAt > clipLatest) clipLatest = c.SavedAt;
            });
            _myDataSummaries["clipboard"].Text = clipCount == 0
                ? "暂无收藏"
                : $"{clipCount} 条收藏 · 最近 {clipLatest:MM-dd HH:mm}";

            // 每日活跃（云端数据；概述仅读本地缓存，不发起请求）
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                _myDataSummaries["daily"].Text = "未登录 · 数据保存在云端";
            }
            else
            {
                var pomodoroToday = DailyActivityService.Instance.TryGetCachedValue(null, DailyActivityTypes.Pomodoro, DateTime.Today);
                _myDataSummaries["daily"].Text = pomodoroToday != null
                    ? $"云端记录 · 今日番茄 {FormatNum(pomodoroToday.Value)} 次"
                    : "云端记录 · 点击查看详情";
            }

            // 数据变量（本地定义：面板数据项 + 后台数据项）
            var vars = CollectLocalVariables();
            int bgVarCount = _app.WindowConfigs.BackgroundDashboardItems.Count(d => !string.IsNullOrEmpty(d.VarKey));
            _myDataSummaries["variable"].Text = vars.Count == 0
                ? "暂无变量定义"
                : $"{vars.Count} 个变量（后台 {bgVarCount} 个）";

            // TODO
            int todoPending = 0, todoDone = 0;
            WalkPanels((w, p) =>
            {
                if (p.TodoItems == null) return;
                todoPending += p.TodoItems.Count(t => !t.IsDone);
                todoDone += p.TodoItems.Count(t => t.IsDone);
            });
            _myDataSummaries["todo"].Text = (todoPending + todoDone) == 0
                ? "暂无待办"
                : $"{todoPending} 项未完成 · {todoDone} 项已完成";

            // 每日屏幕时长（本地窗口日志）
            try
            {
                var usage = SideDesk.Windows.WindowUsageStats.GetAppUsageSummary();
                var total = usage.Aggregate(TimeSpan.Zero, (acc, i) => acc + i.TotalDuration);
                _myDataSummaries["screentime"].Text = usage.Count == 0
                    ? "今日暂无使用记录"
                    : $"今日 {FormatDuration(total)} · {usage.Count} 个应用";
            }
            catch
            {
                _myDataSummaries["screentime"].Text = "读取使用记录失败";
            }
        }

        // ==================== 数据收集辅助 ====================

        /// <summary>递归遍历所有窗口的全部面板（含分组子面板）</summary>
        private void WalkPanels(Action<WindowConfig, PanelItem> visit)
        {
            void Recurse(WindowConfig w, PanelItem p)
            {
                visit(w, p);
                if (p.ChildPanels != null)
                    foreach (var c in p.ChildPanels) Recurse(w, c);
            }
            foreach (var w in _app.WindowConfigs.Windows)
                foreach (var p in w.Panels) Recurse(w, p);
        }

        /// <summary>收集本地全部变量定义（实际轮询的数据项 + 后台数据项），按 VarKey 去重</summary>
        private Dictionary<string, DataDashboardItem> CollectLocalVariables()
        {
            var seen = new Dictionary<string, DataDashboardItem>(StringComparer.OrdinalIgnoreCase);
            WalkPanels((w, p) =>
            {
                if (p.DashboardItems == null) return;
                foreach (var d in p.DashboardItems)
                    if (!string.IsNullOrEmpty(d.VarKey) && string.IsNullOrEmpty(d.RefVarKey))
                        seen.TryAdd(d.VarKey, d);
            });
            foreach (var d in _app.WindowConfigs.BackgroundDashboardItems)
                if (!string.IsNullOrEmpty(d.VarKey))
                    seen.TryAdd(d.VarKey, d);
            return seen;
        }

        private static string PanelLocation(WindowConfig w, PanelItem p)
            => $"窗口「{w.Name}」· {(string.IsNullOrEmpty(p.Title) ? p.Type.ToString() : p.Title)}";

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes} 分钟";
            return $"{(int)ts.TotalSeconds} 秒";
        }

        private static string FormatNum(double v) => v == Math.Floor(v) ? v.ToString("0") : v.ToString("0.#");

        // ==================== 归属信息（团队/个人 · 共享组件 · 创作者） ====================

        /// <summary>归属查询上下文：团队名表（加密ID → 名称）+ 模块创作者表（ServerId → 归属信息）</summary>
        private class OwnershipContext
        {
            public Dictionary<string, string>? Teams;
            public Dictionary<string, ItemOwnerInfo>? Creators;
        }

        /// <summary>团队名缓存（加密团队ID → 团队名），设置窗口生命周期内只拉一次</summary>
        private Dictionary<string, string>? _teamNameCache;

        /// <summary>加载归属上下文：团队列表（解析团队名）+ 批量查模块创作者；未登录/后端不可达时对应表为 null，调用方降级显示</summary>
        private async Task<OwnershipContext> LoadOwnershipAsync(IEnumerable<string?> serverIds)
        {
            var ctx = new OwnershipContext { Teams = _teamNameCache };
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken)) return ctx;

            var ids = serverIds.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
            var teamTask = ctx.Teams == null ? TeamService.Instance.ListTeamsAsync() : Task.FromResult<List<Team>?>(null);
            var creatorTask = ids.Count > 0
                ? TeamService.Instance.GetItemCreatorsAsync(ids)
                : Task.FromResult<Dictionary<string, ItemOwnerInfo>?>(null);

            var teamList = await teamTask;
            if (ctx.Teams == null && teamList != null)
            {
                ctx.Teams = teamList.ToDictionary(t => t.Id, t => t.Name);
                _teamNameCache = ctx.Teams;
            }
            ctx.Creators = await creatorTask;
            return ctx;
        }

        /// <summary>共享组件标识 + 创作者（不含团队/个人归属，供已有作用域文字的行追加）</summary>
        private static string ShareCreatorText(PanelItem p, OwnershipContext ctx)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.ShareMode))
                parts.Add(p.ShareMode == "reference" ? "共享组件·引用" : "共享组件·复制");
            if (!string.IsNullOrEmpty(p.ServerId) && ctx.Creators != null
                && ctx.Creators.TryGetValue(p.ServerId!, out var info) && !string.IsNullOrEmpty(info.CreatorName))
                parts.Add($"创作者 {info.CreatorName}");
            return string.Join(" · ", parts);
        }

        /// <summary>模块归属完整描述：团队「名」/个人 · 共享组件(复制/引用) · 创作者 XX</summary>
        private string OwnershipText(PanelItem p, OwnershipContext ctx)
        {
            var parts = new List<string>();
            if (p.OwnerType == "team")
            {
                string? teamName = p.TeamName;
                if (string.IsNullOrEmpty(teamName) && !string.IsNullOrEmpty(p.TeamId) && ctx.Teams != null)
                    ctx.Teams.TryGetValue(p.TeamId!, out teamName);
                // 本地无团队名快照时用服务端归属的团队名兜底
                if (string.IsNullOrEmpty(teamName) && !string.IsNullOrEmpty(p.ServerId) && ctx.Creators != null
                    && ctx.Creators.TryGetValue(p.ServerId!, out var byCorp) && !string.IsNullOrEmpty(byCorp.CorpName))
                    teamName = byCorp.CorpName;
                parts.Add(string.IsNullOrEmpty(teamName) ? "团队" : $"团队「{teamName}」");
            }
            else
            {
                parts.Add("个人");
            }

            string extra = ShareCreatorText(p, ctx);
            if (!string.IsNullOrEmpty(extra)) parts.Add(extra);
            return string.Join(" · ", parts);
        }

        // ==================== 详情窗口 ====================

        private void OpenMyDataDetail(string kind)
        {
            switch (kind)
            {
                case "sticky": ShowStickyDetail(); break;
                case "clipboard": ShowClipboardDetail(); break;
                case "daily": ShowDailyActivityDetail(); break;
                case "variable": ShowVariableDetail(); break;
                case "todo": ShowTodoDetail(); break;
                case "screentime": ShowScreenTimeDetail(); break;
            }
        }

        private Window CreateMyDataDetailWindow(string title, out StackPanel content)
        {
            var win = new Window
            {
                Title = title,
                Width = 560,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            win.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(win, App.IsCurrentThemeLight());
            content = new StackPanel { Margin = new Thickness(20) };
            win.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
            return win;
        }

        /// <summary>详情行：主文本 + 次级说明，可选 ToolTip 全文</summary>
        private Border DetailRow(string main, string? sub, string? tooltip = null)
        {
            var sp = new StackPanel();
            sp.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = main,
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrEmpty(sub))
            {
                sp.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = sub,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            var row = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                Child = sp
            };
            if (tooltip != null) row.ToolTip = tooltip;
            return row;
        }

        private TextBlock DetailTip(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("BrushTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 8, 10, 8)
        };

        // ----- 便签 -----

        private async void ShowStickyDetail()
        {
            var win = CreateMyDataDetailWindow("便签 · 详情", out var content);
            win.Show();

            var notes = new List<(StickyNote Note, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.StickyNotes == null) return;
                foreach (var n in p.StickyNotes) notes.Add((n, p, PanelLocation(w, p)));
            });

            if (notes.Count == 0)
            {
                content.Children.Add(DetailTip("暂无便签。可在侧边栏添加「便签」模块创建。"));
                return;
            }

            var ctx = await LoadOwnershipAsync(notes.Select(x => x.Panel.ServerId));
            foreach (var (note, panel, loc) in notes.OrderByDescending(x => x.Note.UpdatedAt))
            {
                content.Children.Add(DetailRow(
                    note.Preview,
                    $"{loc} · {OwnershipText(panel, ctx)} · 更新于 {note.UpdatedAt:yyyy-MM-dd HH:mm}",
                    note.Content.Length > 500 ? note.Content.Substring(0, 500) + "…" : note.Content));
            }
        }

        // ----- 剪贴板 -----

        private async void ShowClipboardDetail()
        {
            var win = CreateMyDataDetailWindow("剪贴板 · 详情", out var content);
            win.Show();

            var items = new List<(ClipboardSavedItem Clip, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.ClipboardItems == null) return;
                foreach (var c in p.ClipboardItems) items.Add((c, p, PanelLocation(w, p)));
            });

            if (items.Count == 0)
            {
                content.Children.Add(DetailTip("暂无剪贴板收藏。可在侧边栏添加「剪贴板收藏」模块收藏复制内容。"));
                return;
            }

            var ctx = await LoadOwnershipAsync(items.Select(x => x.Panel.ServerId));
            foreach (var (clip, panel, loc) in items.OrderByDescending(x => x.Clip.SavedAt))
            {
                string icon = clip.ContentType switch { "Image" => "🖼", "File" => "📁", _ => "📄" };
                string preview = clip.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(空)";
                if (preview.Length > 60) preview = preview.Substring(0, 60) + "…";
                content.Children.Add(DetailRow(
                    $"{icon} {preview}",
                    $"{clip.ContentType} · {loc} · {OwnershipText(panel, ctx)} · 收藏于 {clip.SavedAt:yyyy-MM-dd HH:mm}",
                    clip.Content.Length > 500 ? clip.Content.Substring(0, 500) + "…" : clip.Content));
            }
        }

        // ----- 每日活跃 -----

        private async void ShowDailyActivityDetail()
        {
            var win = CreateMyDataDetailWindow("每日活跃 · 详情", out var content);
            win.Show();

            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                content.Children.Add(DetailTip("未登录。每日活跃数据保存在云端，请先在「账户」页登录后再查看。"));
                return;
            }

            content.Children.Add(DetailTip("加载数据中…"));
            var types = await DailyActivityService.Instance.ListTypesAsync(null);
            content.Children.Clear();

            if (types == null)
            {
                content.Children.Add(DetailTip("获取失败：后端不可达。请确认 SideDesk 服务正在运行后重试。"));
                return;
            }

            var end = DateTime.Today;
            var start = end.AddDays(-29);
            int added = 0;

            async Task AddTypeRowAsync(string scope, string type, string? corpId)
            {
                var values = await DailyActivityService.Instance.GetValuesAsync(corpId, type, start, end);
                double today = values != null && values.TryGetValue(end.ToString("yyyy-MM-dd"), out var v) ? v : 0;
                double week = 0, month = 0;
                if (values != null)
                {
                    foreach (var kv in values)
                    {
                        month += kv.Value;
                        if (DateTime.TryParse(kv.Key, out var d) && d >= end.AddDays(-6)) week += kv.Value;
                    }
                }
                content.Children.Add(DetailRow(
                    type,
                    $"{scope} · 今日 {FormatNum(today)} · 近 7 天 {FormatNum(week)} · 近 30 天 {FormatNum(month)}"));
                added++;
            }

            // 个人作用域
            foreach (var t in types) await AddTypeRowAsync("个人", t, null);

            // 团队作用域（逐团队查询，失败跳过）
            var teams = await TeamService.Instance.ListTeamsAsync();
            if (teams != null)
            {
                foreach (var team in teams)
                {
                    var teamTypes = await DailyActivityService.Instance.ListTypesAsync(team.Id);
                    if (teamTypes == null) continue;
                    foreach (var t in teamTypes) await AddTypeRowAsync($"团队「{team.Name}」", t, team.Id);
                }
            }

            if (added == 0)
                content.Children.Add(DetailTip("暂无每日活跃记录。完成番茄、记录健身等操作后会自动写入。"));
        }

        // ----- 数据变量 -----

        private async void ShowVariableDetail()
        {
            var win = CreateMyDataDetailWindow("数据变量 · 详情", out var content);
            win.Show();

            // 本地定义的变量（面板数据项 + 后台数据项）
            var local = new Dictionary<string, (DataDashboardItem Item, PanelItem? Panel, string Location)>(StringComparer.OrdinalIgnoreCase);
            WalkPanels((w, p) =>
            {
                if (p.DashboardItems == null) return;
                foreach (var d in p.DashboardItems)
                    if (!string.IsNullOrEmpty(d.VarKey) && string.IsNullOrEmpty(d.RefVarKey))
                        local.TryAdd(d.VarKey, (d, p, PanelLocation(w, p)));
            });
            foreach (var d in _app.WindowConfigs.BackgroundDashboardItems)
                if (!string.IsNullOrEmpty(d.VarKey))
                    local.TryAdd(d.VarKey, (d, null, "后台轮询"));

            if (local.Count == 0)
                content.Children.Add(DetailTip("本地暂无变量定义。"));

            var ctx = await LoadOwnershipAsync(local.Values.Select(v => v.Panel?.ServerId));
            foreach (var kv in local)
            {
                var (item, panel, loc) = kv.Value;
                string scope = string.IsNullOrEmpty(item.TeamId) ? "个人" : $"团队「{item.TeamName ?? item.TeamId}」";
                var sub = new List<string> { scope, $"来自「{item.Label}」", loc };
                if (panel != null)
                {
                    string extra = ShareCreatorText(panel, ctx);
                    if (!string.IsNullOrEmpty(extra)) sub.Add(extra);
                }
                sub.Add($"当前值：{(string.IsNullOrEmpty(item.Value) ? "--" : item.Value)}");
                content.Children.Add(DetailRow(kv.Key, string.Join(" · ", sub)));
            }

            // 云端已登记但本地不存在的变量（其他设备创建）
            if (!string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                var cloudTip = DetailTip("同步云端变量列表中…");
                content.Children.Add(cloudTip);
                _ = Task.Run(async () =>
                {
                    var list = await DataVariableService.Instance.ListAsync(null);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        content.Children.Remove(cloudTip);
                        if (list == null) return; // 后端不可达：静默跳过
                        var extras = list.Where(v => !string.IsNullOrEmpty(v.VarKey) && !local.ContainsKey(v.VarKey)).ToList();
                        if (extras.Count == 0) return;
                        content.Children.Add(DetailTip("云端其他设备登记的变量："));
                        foreach (var v in extras)
                        {
                            string scope = v.CorpId != null ? "团队" : "个人";
                            content.Children.Add(DetailRow(
                                v.VarKey,
                                $"{scope} · 来自「{(string.IsNullOrEmpty(v.Label) ? "未知" : v.Label)}」· 当前值：{(string.IsNullOrEmpty(v.VarValue) ? "--" : v.VarValue)}"));
                        }
                    });
                });
            }
        }

        // ----- TODO -----

        private async void ShowTodoDetail()
        {
            var win = CreateMyDataDetailWindow("TODO · 详情", out var content);
            win.Show();

            var todos = new List<(TodoItem Todo, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.TodoItems == null) return;
                foreach (var t in p.TodoItems) todos.Add((t, p, PanelLocation(w, p)));
            });

            if (todos.Count == 0)
            {
                content.Children.Add(DetailTip("暂无待办事项。可在侧边栏添加「今日待办」模块创建。"));
                return;
            }

            var ctx = await LoadOwnershipAsync(todos.Select(x => x.Panel.ServerId));
            foreach (var (todo, panel, loc) in todos.OrderBy(x => x.Todo.IsDone).ThenByDescending(x => x.Todo.CreatedAt))
            {
                string sub = $"{loc} · {OwnershipText(panel, ctx)} · 创建于 {todo.CreatedAt:yyyy-MM-dd HH:mm}";
                if (todo.DueTime != null) sub += $" · 截止 {todo.DueTime:MM-dd HH:mm}";
                content.Children.Add(DetailRow(
                    (todo.IsDone ? "✅ " : "☐ ") + todo.Text,
                    sub,
                    string.IsNullOrEmpty(todo.Description) ? null : todo.Description));
            }
        }

        // ----- 每日屏幕时长（小时 / 日 / 月 / 年统计） -----

        private void ShowScreenTimeDetail()
        {
            var win = CreateMyDataDetailWindow("每日屏幕时长 · 详情", out var content);

            // 数据源：local_screen_time_hourly（每小时 × 每应用一条记录）
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 6) };
            var body = new StackPanel();
            Button? active = null;

            void Switch(Button btn, Func<Task> render)
            {
                if (active != null) active.Style = (Style)FindResource("StandardButtonStyle");
                btn.Style = (Style)FindResource("PrimaryButtonStyle");
                active = btn;
                body.Children.Clear();
                body.Children.Add(DetailTip("统计中…"));
                _ = Task.Run(async () =>
                {
                    try { await render(); }
                    catch { Dispatcher.InvokeAsync(() => { body.Children.Clear(); body.Children.Add(DetailTip("统计失败，请重试。")); }); }
                });
            }

            Button MakeTab(string text, Func<Task> render)
            {
                var b = new Button
                {
                    Content = text,
                    MinWidth = 64,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                b.Click += (s, e) => Switch(b, render);
                return b;
            }

            var tabHour = MakeTab("小时", () => RenderScreenTimeAsync(body, "hour", DateTime.Today, null));
            var tabDay = MakeTab("日", () => RenderScreenTimeAsync(body, "day", DateTime.Today.AddDays(-29), null));
            var tabMonth = MakeTab("月", () => RenderScreenTimeAsync(body, "month",
                new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11), null));
            var tabYear = MakeTab("年", () => RenderScreenTimeAsync(body, "year", null, null));
            tabRow.Children.Add(tabHour);
            tabRow.Children.Add(tabDay);
            tabRow.Children.Add(tabMonth);
            tabRow.Children.Add(tabYear);

            content.Children.Add(tabRow);
            content.Children.Add(body);
            win.Show();
            Switch(tabHour, () => RenderScreenTimeAsync(body, "hour", DateTime.Today, null));
        }

        /// <summary>按粒度渲染屏幕时长统计（本地 + 云端其他设备合并，后台线程执行）</summary>
        private async Task RenderScreenTimeAsync(StackPanel body, string granularity, DateTime? from, DateTime? to)
        {
            var svc = ScreenTimeHourlyService.Instance;
            List<ScreenTimeHourlyService.StatRow> rows;
            try
            {
                rows = svc.Query(granularity, from, to);
                // 云端合并：服务端已排除本机设备（本机数据以本地为准），失败时降级为仅本地
                var cloud = await svc.GetCloudStatsAsync(granularity, from, to);
                if (cloud != null && cloud.Count > 0) rows = rows.Concat(cloud).ToList();
            }
            catch
            {
                Dispatcher.InvokeAsync(() =>
                {
                    body.Children.Clear();
                    body.Children.Add(DetailTip("读取屏幕时长记录失败。"));
                });
                return;
            }

            // 时间桶 → 应用时长
            var grouped = rows
                .GroupBy(r => r.Period)
                .OrderByDescending(g => g.Key)
                .Select(g => (Period: g.Key,
                              Total: TimeSpan.FromSeconds(g.Sum(r => r.Seconds)),
                              Apps: g.OrderByDescending(r => r.Seconds)
                                     .Select(r => (r.Process, Duration: TimeSpan.FromSeconds(r.Seconds)))
                                     .ToList()))
                .ToList();

            Dispatcher.InvokeAsync(() =>
            {
                body.Children.Clear();
                if (grouped.Count == 0)
                {
                    body.Children.Add(DetailTip("暂无屏幕时长记录。需在设置中开启「使用时长分析」，应用运行时会自动采集。"));
                    return;
                }

                // 顶部合计行
                var grandTotal = TimeSpan.FromSeconds(rows.Sum(r => r.Seconds));
                var allApps = rows.GroupBy(r => r.Process)
                    .OrderByDescending(g => g.Sum(r => r.Seconds))
                    .Select(g => (Process: g.Key, Total: TimeSpan.FromSeconds(g.Sum(r => r.Seconds))))
                    .ToList();
                string rangeLabel = granularity switch
                {
                    "hour" => DateTime.Today.ToString("MM-dd"),
                    "day" => $"{from:MM-dd} ~ {DateTime.Today:MM-dd}",
                    "month" => $"{from:yyyy-MM} ~ {DateTime.Today:yyyy-MM}",
                    _ => "全部年份"
                };
                body.Children.Add(DetailRow(
                    $"合计 {FormatDuration(grandTotal)}",
                    $"{rangeLabel} · {grouped.Count} 个{PeriodUnitName(granularity)} · {allApps.Count} 个应用",
                    string.Join("\n", allApps.Select(a => $"{a.Process}：{FormatDuration(a.Total)}"))));

                foreach (var g in grouped)
                {
                    string label = granularity switch { "hour" => g.Period, "day" => g.Period, "month" => g.Period, _ => $"{g.Period} 年" };
                    string sub = $"{FormatDuration(g.Total)} · Top：" + string.Join("，",
                        g.Apps.Take(3).Select(a => $"{a.Process} {FormatDuration(a.Duration)}"));
                    string tip = string.Join("\n", g.Apps.Select(a => $"{a.Process}：{FormatDuration(a.Duration)}"));
                    body.Children.Add(DetailRow(label, sub, tip));
                }
            });
        }

        private static string PeriodUnitName(string granularity) => granularity switch
        {
            "hour" => "小时",
            "day" => "天",
            "month" => "月",
            _ => "年"
        };
    }
}
