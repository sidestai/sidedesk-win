using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Controls;
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
                ? Framework.Lang.T("暂无便签")
                : Framework.Lang.TF("{0} 条 · 最近更新 {1:MM-dd HH:mm}", noteCount, noteLatest);

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
                ? Framework.Lang.T("暂无收藏")
                : Framework.Lang.TF("{0} 条收藏 · 最近 {1:MM-dd HH:mm}", clipCount, clipLatest);

            // 每日活跃（云端数据；概述仅读本地缓存，不发起请求）
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                _myDataSummaries["daily"].Text = Framework.Lang.T("未登录 · 数据保存在云端");
            }
            else
            {
                var pomodoroToday = DailyActivityService.Instance.TryGetCachedValue(null, DailyActivityTypes.Pomodoro, DateTime.Today);
                _myDataSummaries["daily"].Text = pomodoroToday != null
                    ? Framework.Lang.TF("云端记录 · 今日番茄 {0} 次", FormatNum(pomodoroToday.Value))
                    : Framework.Lang.T("云端记录 · 点击查看详情");
            }

            // 数据变量（本地定义：面板数据项 + 后台数据项 + 变量集）
            var vars = CollectLocalVariables();
            int setVarCount = _app.WindowConfigs.VariableSets.Count;
            int bgVarCount = _app.WindowConfigs.BackgroundDashboardItems.Count(d => !string.IsNullOrEmpty(d.VarKey));
            _myDataSummaries["variable"].Text = vars.Count + setVarCount == 0
                ? Framework.Lang.T("暂无变量定义")
                : Framework.Lang.TF("{0} 个变量（后台 {1} 个）", vars.Count + setVarCount, bgVarCount);

            // TODO
            int todoPending = 0, todoDone = 0;
            WalkPanels((w, p) =>
            {
                if (p.TodoItems == null) return;
                todoPending += p.TodoItems.Count(t => !t.IsDone);
                todoDone += p.TodoItems.Count(t => t.IsDone);
            });
            _myDataSummaries["todo"].Text = (todoPending + todoDone) == 0
                ? Framework.Lang.T("暂无待办")
                : Framework.Lang.TF("{0} 项未完成 · {1} 项已完成", todoPending, todoDone);

            // 每日屏幕时长（本地窗口日志）
            try
            {
                var usage = SideDesk.Windows.WindowUsageStats.GetAppUsageSummary();
                var total = usage.Aggregate(TimeSpan.Zero, (acc, i) => acc + i.TotalDuration);
                _myDataSummaries["screentime"].Text = usage.Count == 0
                    ? Framework.Lang.T("今日暂无使用记录")
                    : Framework.Lang.TF("今日 {0} · {1} 个应用", FormatDuration(total), usage.Count);
            }
            catch
            {
                _myDataSummaries["screentime"].Text = Framework.Lang.T("读取使用记录失败");
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
            => Framework.Lang.TF("窗口「{0}」· {1}", w.Name, (string.IsNullOrEmpty(p.Title) ? p.Type.ToString() : p.Title));

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return Framework.Lang.TF("{0} 小时 {1} 分", (int)ts.TotalHours, ts.Minutes);
            if (ts.TotalMinutes >= 1) return Framework.Lang.TF("{0} 分钟", ts.Minutes);
            return Framework.Lang.TF("{0} 秒", (int)ts.TotalSeconds);
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
                parts.Add(p.ShareMode == "reference" ? Framework.Lang.T("共享组件·引用") : Framework.Lang.T("共享组件·复制"));
            if (!string.IsNullOrEmpty(p.ServerId) && ctx.Creators != null
                && ctx.Creators.TryGetValue(p.ServerId!, out var info) && !string.IsNullOrEmpty(info.CreatorName))
                parts.Add(Framework.Lang.TF("创作者 {0}", info.CreatorName));
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
                parts.Add(string.IsNullOrEmpty(teamName) ? Framework.Lang.T("团队") : Framework.Lang.TF("团队「{0}」", teamName));
            }
            else
            {
                parts.Add(Framework.Lang.T("个人"));
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

        /// <summary>便签编辑浮窗（与侧边栏共用 StickyNotePanel，设置窗口内单例复用）</summary>
        private StickyNotePanel? _myDataStickyEditor;

        private async void ShowStickyDetail()
        {
            var win = CreateMyDataDetailWindow(Framework.Lang.T("便签 · 详情"), out var content);
            var listPanel = new StackPanel();
            content.Children.Add(listPanel);
            win.Show();

            var notes = new List<(StickyNote Note, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.StickyNotes == null) return;
                foreach (var n in p.StickyNotes) notes.Add((n, p, PanelLocation(w, p)));
            });

            var ctx = await LoadOwnershipAsync(notes.Select(x => x.Panel.ServerId));

            void RenderList()
            {
                listPanel.Children.Clear();
                if (notes.Count == 0)
                {
                    listPanel.Children.Add(DetailTip(Framework.Lang.T("暂无便签。可在侧边栏添加「便签」模块创建。")));
                    return;
                }

                foreach (var entry in notes.OrderByDescending(x => x.Note.UpdatedAt).ToList())
                {
                    var (note, panel, loc) = entry;

                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                    texts.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = note.Preview,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    texts.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = Framework.Lang.TF("{0} · {1} · 更新于 {2:yyyy-MM-dd HH:mm}", loc, OwnershipText(panel, ctx), note.UpdatedAt),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    var delBtn = new Button
                    {
                        Content = Framework.Lang.T("删除"),
                        Style = (Style)FindResource("StandardButtonStyle"),
                        Padding = new Thickness(8, 2, 8, 2),
                        Margin = new Thickness(8, 0, 0, 0),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    delBtn.Click += (s, e) =>
                    {
                        if (MessageBox.Show(Framework.Lang.TF("确定删除便签「{0}」？此操作不可撤销。", note.Preview), Framework.Lang.T("提示"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        panel.StickyNotes?.RemoveAll(n => n.Id == note.Id);
                        notes.Remove(entry);
                        _app.WindowConfigs.Save();
                        UpdateMyDataCards();
                        RenderList();
                    };

                    var grid = new Grid { Cursor = Cursors.Hand };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(texts, 0);
                    Grid.SetColumn(delBtn, 1);
                    grid.Children.Add(texts);
                    grid.Children.Add(delBtn);

                    // 点击行打开便签编辑浮窗（查看/编辑，保存后回写并刷新列表）
                    grid.MouseLeftButtonUp += (s, e) =>
                    {
                        if (e.Handled) return;
                        OpenMyDataStickyEditor(win, note, () =>
                        {
                            note.UpdatedAt = DateTime.Now;
                            _app.WindowConfigs.Save();
                            UpdateMyDataCards();
                            RenderList();
                        });
                    };

                    listPanel.Children.Add(new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = (Brush)FindResource("BrushBorder"),
                        ToolTip = note.Content.Length > 500 ? note.Content.Substring(0, 500) + "…" : note.Content,
                        Child = grid
                    });
                }
            }
            RenderList();
        }

        /// <summary>从「我的数据-便签」打开便签编辑浮窗：紧贴详情窗口右侧弹出</summary>
        private void OpenMyDataStickyEditor(Window ownerWin, StickyNote note, Action onSaved)
        {
            _myDataStickyEditor ??= new StickyNotePanel();
            _myDataStickyEditor.Show(ownerWin,
                ownerWin.Left + ownerWin.ActualWidth, ownerWin.Top, 420, ownerWin.ActualHeight,
                note, onSaved);
        }

        // ----- 剪贴板 -----

        private async void ShowClipboardDetail()
        {
            var win = CreateMyDataDetailWindow(Framework.Lang.T("剪贴板 · 详情"), out var content);
            win.Show();

            var items = new List<(ClipboardSavedItem Clip, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.ClipboardItems == null) return;
                foreach (var c in p.ClipboardItems) items.Add((c, p, PanelLocation(w, p)));
            });

            if (items.Count == 0)
            {
                content.Children.Add(DetailTip(Framework.Lang.T("暂无剪贴板收藏。可在侧边栏添加「剪贴板收藏」模块收藏复制内容。")));
                return;
            }

            var ctx = await LoadOwnershipAsync(items.Select(x => x.Panel.ServerId));
            foreach (var (clip, panel, loc) in items.OrderByDescending(x => x.Clip.SavedAt))
            {
                string icon = clip.ContentType switch { "Image" => "🖼", "File" => "📁", _ => "📄" };
                string preview = clip.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Framework.Lang.T("(空)");
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
            var win = CreateMyDataDetailWindow(Framework.Lang.T("每日活跃 · 详情"), out var content);
            win.Show();

            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                content.Children.Add(DetailTip(Framework.Lang.T("未登录。每日活跃数据保存在云端，请先在「账户」页登录后再查看。")));
                return;
            }

            content.Children.Add(DetailTip(Framework.Lang.T("加载数据中…")));
            var types = await DailyActivityService.Instance.ListTypesAsync(null);
            content.Children.Clear();

            if (types == null)
            {
                content.Children.Add(DetailTip(Framework.Lang.T("获取失败：后端不可达。请确认 SideDesk 服务正在运行后重试。")));
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
                    Framework.Lang.TF("{0} · 今日 {1} · 近 7 天 {2} · 近 30 天 {3}", scope, FormatNum(today), FormatNum(week), FormatNum(month))));
                added++;
            }

            // 个人作用域
            foreach (var t in types) await AddTypeRowAsync(Framework.Lang.T("个人"), t, null);

            // 团队作用域（逐团队查询，失败跳过）
            var teams = await TeamService.Instance.ListTeamsAsync();
            if (teams != null)
            {
                foreach (var team in teams)
                {
                    var teamTypes = await DailyActivityService.Instance.ListTypesAsync(team.Id);
                    if (teamTypes == null) continue;
                    foreach (var t in teamTypes) await AddTypeRowAsync(Framework.Lang.TF("团队「{0}」", team.Name), t, team.Id);
                }
            }

            if (added == 0)
                content.Children.Add(DetailTip(Framework.Lang.T("暂无每日活跃记录。完成番茄、记录健身等操作后会自动写入。")));
        }

        // ----- 数据变量 -----

        private async void ShowVariableDetail()
        {
            var win = CreateMyDataDetailWindow(Framework.Lang.T("数据变量 · 详情"), out var content);

            // 顶部：创建变量集按钮
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 4) };
            var createSetBtn = new Button
            {
                Content = Framework.Lang.T("➕ 创建变量集"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Cursor = Cursors.Hand
            };
            btnRow.Children.Add(createSetBtn);
            content.Children.Add(btnRow);

            // 变量集列表（多变量聚合生成的新变量）
            var setsBox = new StackPanel();
            content.Children.Add(setsBox);
            createSetBtn.Click += (s, e) =>
            {
                if (ShowCreateVariableSetDialog(win)) RenderVariableSetsSection(setsBox);
            };
            RenderVariableSetsSection(setsBox);

            // 云端登记的变量集（其他设备/团队成员创建，本地不存在）
            var remoteBox = new StackPanel();
            content.Children.Add(remoteBox);

            void RenderRemoteSets()
            {
                remoteBox.Children.Clear();
                var localKeys = new HashSet<string>(
                    _app.WindowConfigs.VariableSets.Select(v => v.VarKey), StringComparer.OrdinalIgnoreCase);
                var remotes = VariableSetService.Instance.GetRemoteSets()
                    .Where(r => !string.IsNullOrEmpty(r.VarKey) && !localKeys.Contains(r.VarKey)).ToList();
                if (remotes.Count == 0) return;

                remoteBox.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("☁️ 云端登记的变量集"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    Margin = new Thickness(10, 6, 10, 2)
                });

                foreach (var set in remotes)
                {
                    var value = VariableSetService.Instance.FindItemByVarKey(set.VarKey)?.Value;
                    var sub = new List<string>
                    {
                        string.IsNullOrEmpty(set.TeamId) ? Framework.Lang.T("个人") : Framework.Lang.TF("团队「{0}」", set.TeamName ?? set.TeamId),
                        VariableSetOpName(set.Op),
                        Framework.Lang.TF("当前值：{0}", string.IsNullOrEmpty(value) ? "--" : value)
                    };

                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    texts.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = set.VarKey,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("BrushTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    texts.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = string.Join(" · ", sub),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });

                    var delBtn = new Button
                    {
                        Content = Framework.Lang.T("删除"),
                        Style = (Style)FindResource("StandardButtonStyle"),
                        Padding = new Thickness(8, 2, 8, 2),
                        Margin = new Thickness(8, 0, 0, 0),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var captured = set;
                    delBtn.Click += async (s, e) =>
                    {
                        if (MessageBox.Show(Framework.Lang.TF("删除云端变量集「{0}」？", captured.VarKey), Framework.Lang.T("提示"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        delBtn.IsEnabled = false;
                        bool ok = await Task.Run(() => DataVariableService.Instance.DeleteSetAsync(captured.VarKey, captured.TeamId));
                        if (!ok)
                        {
                            MessageBox.Show(Framework.Lang.T("删除失败，请重试。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            delBtn.IsEnabled = true;
                            return;
                        }
                        VariableSetService.Instance.RemoveRemote(captured.VarKey, captured.TeamId);
                        RenderRemoteSets();
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(texts, 0);
                    Grid.SetColumn(delBtn, 1);
                    grid.Children.Add(texts);
                    grid.Children.Add(delBtn);

                    remoteBox.Children.Add(new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = (Brush)FindResource("BrushBorder"),
                        Child = grid
                    });
                }
            }

            // 打开详情窗时重新拉取云端变量集（个人 + 已加入团队）
            _ = Task.Run(async () =>
            {
                await VariableSetService.Instance.PullRemoteAsync();
                await Dispatcher.InvokeAsync(RenderRemoteSets);
            });

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
                content.Children.Add(DetailTip(Framework.Lang.T("本地暂无变量定义。")));

            var ctx = await LoadOwnershipAsync(local.Values.Select(v => v.Panel?.ServerId));
            foreach (var kv in local)
            {
                var (item, panel, loc) = kv.Value;
                string scope = string.IsNullOrEmpty(item.TeamId) ? Framework.Lang.T("个人") : Framework.Lang.TF("团队「{0}」", item.TeamName ?? item.TeamId);
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
                var cloudTip = DetailTip(Framework.Lang.T("同步云端变量列表中…"));
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
                        content.Children.Add(DetailTip(Framework.Lang.T("云端其他设备登记的变量：")));
                        foreach (var v in extras)
                        {
                            string scope = v.CorpId != null ? Framework.Lang.T("团队") : Framework.Lang.T("个人");
                            content.Children.Add(DetailRow(
                                v.VarKey,
                                $"{scope} · 来自「{(string.IsNullOrEmpty(v.Label) ? "未知" : v.Label)}」· 当前值：{(string.IsNullOrEmpty(v.VarValue) ? "--" : v.VarValue)}"));
                        }
                    });
                });
            }
        }

        // ----- 变量集（多变量聚合：求和/求平均/最大/最小/计数） -----

        private static string VariableSetOpName(string op) => (op ?? "").ToUpperInvariant() switch
        {
            "AVG" => Framework.Lang.T("求平均"),
            "MAX" => Framework.Lang.T("最大值"),
            "MIN" => Framework.Lang.T("最小值"),
            "COUNT" => Framework.Lang.T("计数"),
            _ => Framework.Lang.T("求和")
        };

        /// <summary>渲染变量集列表：变量集名 + 聚合逻辑/来源/当前值 + 删除按钮</summary>
        private void RenderVariableSetsSection(StackPanel box)
        {
            box.Children.Clear();
            var sets = _app.WindowConfigs.VariableSets;
            if (sets.Count == 0) return;

            box.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("🧮 变量集"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(10, 6, 10, 2)
            });

            foreach (var set in sets.ToList())
            {
                var value = VariableSetService.Instance.FindItemByVarKey(set.VarKey)?.Value;
                var sub = new List<string>
                {
                    // 归属范围：个人 或 团队「名称」
                    string.IsNullOrEmpty(set.TeamId) ? Framework.Lang.T("个人") : Framework.Lang.TF("团队「{0}」", set.TeamName ?? set.TeamId),
                    VariableSetOpName(set.Op)
                };
                if (set.SourceKeys.Count > 0) sub.Add(Framework.Lang.TF("勾选 {0} 个", set.SourceKeys.Count));
                if (!string.IsNullOrWhiteSpace(set.Pattern)) sub.Add(Framework.Lang.TF("通配 {0}", set.Pattern));
                sub.Add(Framework.Lang.TF("当前值：{0}", string.IsNullOrEmpty(value) ? "--" : value));

                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = set.VarKey,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                texts.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = string.Join(" · ", sub),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var delBtn = new Button
                {
                    Content = Framework.Lang.T("删除"),
                    Style = (Style)FindResource("StandardButtonStyle"),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var captured = set;
                var capturedBox = box;
                delBtn.Click += (s, e) =>
                {
                    if (MessageBox.Show(Framework.Lang.TF("删除变量集「{0}」？", captured.VarKey), Framework.Lang.T("提示"),
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    _app.WindowConfigs.VariableSets.Remove(captured);
                    _app.WindowConfigs.Save();
                    VariableSetService.Instance.Rebuild();
                    RenderVariableSetsSection(capturedBox);
                    // 同步删除服务端登记的变量集（失败不阻断，下次同步不再上报即可）
                    var tid = captured.TeamId;
                    _ = Task.Run(() => DataVariableService.Instance.DeleteSetAsync(captured.VarKey, tid));
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(texts, 0);
                Grid.SetColumn(delBtn, 1);
                grid.Children.Add(texts);
                grid.Children.Add(delBtn);

                // ToolTip：完整来源清单（勾选的变量名 + 通配符）
                var tipParts = new List<string>();
                if (set.SourceKeys.Count > 0) tipParts.AddRange(set.SourceKeys);
                if (!string.IsNullOrWhiteSpace(set.Pattern)) tipParts.Add(Framework.Lang.TF("通配 {0}", set.Pattern));

                box.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    ToolTip = string.Join("\n", tipParts),
                    Child = grid
                });
            }
        }

        /// <summary>创建变量集对话框：变量集名 + 聚合逻辑 + 来源（勾选变量/通配符），实时预览匹配结果。返回是否创建成功</summary>
        private bool ShowCreateVariableSetDialog(Window owner)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("创建变量集"),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var labelBrush = (Brush)FindResource("BrushTextSec");
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");
            var root = new StackPanel { Margin = new Thickness(20) };

            // 变量集名
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("变量集名（如 qoder.count_all）"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var nameBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(nameBox);

            // 归属范围（个人=仅自己；团队=团队内共享，同名在作用域内唯一）
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("归属范围"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var teamCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = (string?)null });
            teamCombo.SelectedIndex = 0;
            root.Children.Add(teamCombo);
            _ = Task.Run(async () =>
            {
                var teams = await TeamService.Instance.ListTeamsAsync();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (teamCombo.Items.Count > 1) return; // 已加载过不重复追加
                    if (teams != null)
                        foreach (var t in teams)
                            teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
                });
            });

            // 聚合逻辑
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("聚合逻辑"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var opCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var (tag, name) in new[] { ("SUM", "求和"), ("AVG", "求平均"), ("MAX", "最大值"), ("MIN", "最小值"), ("COUNT", "计数") })
                opCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T(name), Tag = tag });
            opCombo.SelectedIndex = 0;
            root.Children.Add(opCombo);

            // 候选变量（本地变量定义 + 运行中变量，按变量名去重）：通配符选取弹窗与下方勾选列表共用
            var candidates = new List<string>();
            var candSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in CollectLocalVariables().Keys)
                if (candSeen.Add(key)) candidates.Add(key);
            foreach (var it in DataPollingService.Instance.GetAllItems())
                if (!string.IsNullOrEmpty(it.VarKey) && candSeen.Add(it.VarKey)) candidates.Add(it.VarKey);

            // 通配符匹配
            root.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("通配符匹配"),
                Foreground = labelBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var patternBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 8, 0) };
            // 右侧“选取变量”：弹窗多选后把选中的变量 key 逗号隔开写回通配符输入框
            var pickBtn = new Button
            {
                Content = Framework.Lang.T("选取变量"),
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand
            };
            pickBtn.Click += (s, e) =>
            {
                // 已有内容中的精确变量名由勾选结果替换，带通配符的模式保留
                var entries = VariableSetService.SplitPatterns(patternBox.Text);
                var wild = entries.Where(p => p.Contains('*') || p.Contains('?'));
                var picked = ShowPickVariablesDialog(dialog, candidates, entries.Except(wild));
                if (picked != null) patternBox.Text = string.Join(", ", wild.Concat(picked));
            };
            var patternRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            patternRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            patternRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(patternBox, 0);
            Grid.SetColumn(pickBtn, 1);
            patternRow.Children.Add(patternBox);
            patternRow.Children.Add(pickBtn);
            root.Children.Add(patternRow);
            root.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("如 qoder.count*（* 匹配任意字符），支持逗号分隔多个或点右侧按钮选取；留空不启用"),
                FontSize = 11,
                Foreground = labelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 实时预览：匹配数量与当前计算结果
            var preview = new TextBlock
            {
                FontSize = 11,
                Foreground = labelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            root.Children.Add(preview);

            VariableSet BuildTempSet() => new()
            {
                VarKey = nameBox.Text.Trim(),
                Op = (opCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "SUM",
                SourceKeys = new List<string>(), // 来源统一走通配符框（选取按钮多选后逗号分隔写回）
                Pattern = patternBox.Text.Trim()
            };

            void RefreshPreview()
            {
                var temp = BuildTempSet();
                var matched = VariableSetService.ResolveSourceKeys(temp, candidates);
                if (matched.Count == 0)
                {
                    preview.Text = Framework.Lang.T("尚未匹配到任何变量");
                    return;
                }
                // 变量名允许重复：同名变量在多 Profile/账号下的值全部参与聚合
                var running = DataPollingService.Instance.GetAllItems()
                    .Where(i => !string.IsNullOrEmpty(i.VarKey))
                    .GroupBy(i => i.VarKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(i => i.Value ?? "").ToList(), StringComparer.OrdinalIgnoreCase);
                var valueCount = matched.Sum(k => running.TryGetValue(k, out var vs) ? vs.Count : 0);
                preview.Text = Framework.Lang.TF("匹配 {0} 个变量 · 当前值：{1}",
                    valueCount > 0 ? valueCount : matched.Count, VariableSetService.ComputeValue(temp, running));
            }

            patternBox.TextChanged += (s, e) => RefreshPreview();
            opCombo.SelectionChanged += (s, e) => RefreshPreview();
            RefreshPreview();

            // 确定 / 取消
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var okBtn = new Button
            {
                Content = Framework.Lang.T("确定"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                MinWidth = 80,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"),
                Style = (Style)FindResource("StandardButtonStyle"),
                MinWidth = 80,
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            root.Children.Add(btnRow);

            bool created = false;
            cancelBtn.Click += (s, e) => dialog.Close();
            okBtn.Click += async (s, e) =>
            {
                var varKey = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(varKey))
                {
                    MessageBox.Show(Framework.Lang.T("请输入变量名"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameBox.Focus();
                    return;
                }
                var temp = BuildTempSet();
                if (temp.SourceKeys.Count == 0 && string.IsNullOrWhiteSpace(temp.Pattern))
                {
                    MessageBox.Show(Framework.Lang.T("请填写通配符或点「选取变量」选择来源。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // 归属范围：选中的团队ID加密串（个人=null）
                var selTeamItem = teamCombo.SelectedItem as ComboBoxItem;
                string? teamId = selTeamItem?.Tag as string;
                string? teamName = teamCombo.SelectedIndex > 0 ? selTeamItem?.Content as string : null;
                // 唯一性校验：本地变量（同作用域）+ 已有变量集（同作用域）+ 后端权威校验
                bool SameScope(string? setTeamId)
                {
                    bool setPersonal = string.IsNullOrEmpty(setTeamId);
                    bool targetPersonal = string.IsNullOrEmpty(teamId);
                    return (setPersonal && targetPersonal) || (!setPersonal && !targetPersonal && setTeamId == teamId);
                }
                if (IsVarKeyDuplicateLocally(varKey, teamId, null)
                    || _app.WindowConfigs.VariableSets.Any(v => string.Equals(v.VarKey, varKey, StringComparison.OrdinalIgnoreCase) && SameScope(v.TeamId))
                    || VariableSetService.Instance.GetRemoteSets().Any(v => string.Equals(v.VarKey, varKey, StringComparison.OrdinalIgnoreCase) && SameScope(v.TeamId)))
                {
                    MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey),
                        Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameBox.Focus();
                    return;
                }
                okBtn.IsEnabled = false;
                bool available;
                try { available = await DataVariableService.Instance.CheckAsync(varKey, teamId); }
                finally { okBtn.IsEnabled = true; }
                if (!available)
                {
                    MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey),
                        Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameBox.Focus();
                    return;
                }

                temp.TeamId = teamId;
                temp.TeamName = teamName;
                _app.WindowConfigs.VariableSets.Add(temp);
                _app.WindowConfigs.Save();
                VariableSetService.Instance.Rebuild();
                created = true;
                dialog.Close();
            };

            dialog.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
            dialog.ShowDialog();
            return created;
        }

        /// <summary>变量选取弹窗：从候选列表勾选多个变量，确定返回选中的 key 列表（取消返回 null）。
        /// 供创建变量集对话框把选中变量逗号隔开写回通配符输入框</summary>
        private List<string>? ShowPickVariablesDialog(Window owner, List<string> candidates, IEnumerable<string> preselected)
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("选取变量"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 540,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var labelBrush = (Brush)FindResource("BrushTextSec");
            var root = new StackPanel { Margin = new Thickness(20) };

            // 全选 / 清空
            var toolRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            Button MakeToolBtn(string text) => new()
            {
                Content = text,
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            var checks = new List<CheckBox>();
            var selectAllBtn = MakeToolBtn(Framework.Lang.T("全选"));
            var clearBtn = MakeToolBtn(Framework.Lang.T("清空"));
            toolRow.Children.Add(selectAllBtn);
            toolRow.Children.Add(clearBtn);
            root.Children.Add(toolRow);

            // 变量勾选列表（预选中通配符框里已有的精确变量名）
            var preSet = new HashSet<string>(preselected, StringComparer.OrdinalIgnoreCase);
            var listPanel = new StackPanel();
            foreach (var key in candidates)
            {
                var cb = new CheckBox
                {
                    Content = key,
                    Tag = key,
                    IsChecked = preSet.Contains(key),
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    Margin = new Thickness(0, 2, 0, 2)
                };
                checks.Add(cb);
                listPanel.Children.Add(cb);
            }
            root.Children.Add(candidates.Count > 0
                ? (UIElement)new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel }
                : new TextBlock { Text = Framework.Lang.T("本地暂无可聚合的变量。"), FontSize = 11, Foreground = labelBrush });

            // 确定 / 取消
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var okBtn = new Button
            {
                Content = Framework.Lang.T("确定"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                MinWidth = 80,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("取消"),
                Style = (Style)FindResource("StandardButtonStyle"),
                MinWidth = 80,
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            root.Children.Add(btnRow);

            selectAllBtn.Click += (s, e) => { foreach (var cb in checks) cb.IsChecked = true; };
            clearBtn.Click += (s, e) => { foreach (var cb in checks) cb.IsChecked = false; };

            List<string>? picked = null;
            cancelBtn.Click += (s, e) => dialog.Close();
            okBtn.Click += (s, e) =>
            {
                picked = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                dialog.Close();
            };

            dialog.Content = root;
            dialog.ShowDialog();
            return picked;
        }

        // ----- TODO -----

        private async void ShowTodoDetail()
        {
            var win = CreateMyDataDetailWindow(Framework.Lang.T("TODO · 详情"), out var content);
            win.Show();

            var todos = new List<(TodoItem Todo, PanelItem Panel, string Location)>();
            WalkPanels((w, p) =>
            {
                if (p.TodoItems == null) return;
                foreach (var t in p.TodoItems) todos.Add((t, p, PanelLocation(w, p)));
            });

            if (todos.Count == 0)
            {
                content.Children.Add(DetailTip(Framework.Lang.T("暂无待办事项。可在侧边栏添加「今日待办」模块创建。")));
                return;
            }

            var ctx = await LoadOwnershipAsync(todos.Select(x => x.Panel.ServerId));
            foreach (var (todo, panel, loc) in todos.OrderBy(x => x.Todo.IsDone).ThenByDescending(x => x.Todo.CreatedAt))
            {
                string sub = Framework.Lang.TF("{0} · {1} · 创建于 {2:yyyy-MM-dd HH:mm}", loc, OwnershipText(panel, ctx), todo.CreatedAt);
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
            var win = CreateMyDataDetailWindow(Framework.Lang.T("每日屏幕时长 · 详情"), out var content);

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
                body.Children.Add(DetailTip(Framework.Lang.T("统计中…")));
                _ = Task.Run(async () =>
                {
                    try { await render(); }
                    catch { Dispatcher.InvokeAsync(() => { body.Children.Clear(); body.Children.Add(DetailTip(Framework.Lang.T("统计失败，请重试。"))); }); }
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

            var tabHour = MakeTab(Framework.Lang.T("小时"), () => RenderScreenTimeAsync(body, "hour", DateTime.Today, null));
            var tabDay = MakeTab(Framework.Lang.T("日"), () => RenderScreenTimeAsync(body, "day", DateTime.Today.AddDays(-29), null));
            var tabMonth = MakeTab(Framework.Lang.T("月"), () => RenderScreenTimeAsync(body, "month",
                new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11), null));
            var tabYear = MakeTab(Framework.Lang.T("年"), () => RenderScreenTimeAsync(body, "year", null, null));
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
                    body.Children.Add(DetailTip(Framework.Lang.T("读取屏幕时长记录失败。")));
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
                    body.Children.Add(DetailTip(Framework.Lang.T("暂无屏幕时长记录。需在设置中开启「使用时长分析」，应用运行时会自动采集。")));
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
                    _ => Framework.Lang.T("全部年份")
                };
                body.Children.Add(DetailRow(
                    Framework.Lang.TF("合计 {0}", FormatDuration(grandTotal)),
                    $"{rangeLabel} · {grouped.Count} 个{PeriodUnitName(granularity)} · {allApps.Count} 个应用",
                    string.Join("\n", allApps.Select(a => $"{a.Process}：{FormatDuration(a.Total)}"))));

                foreach (var g in grouped)
                {
                    string label = granularity switch { "hour" => g.Period, "day" => g.Period, "month" => g.Period, _ => Framework.Lang.TF("{0} 年", g.Period) };
                    string sub = $"{FormatDuration(g.Total)} · Top：" + string.Join("，",
                        g.Apps.Take(3).Select(a => $"{a.Process} {FormatDuration(a.Duration)}"));
                    string tip = string.Join("\n", g.Apps.Select(a => $"{a.Process}：{FormatDuration(a.Duration)}"));
                    body.Children.Add(DetailRow(label, sub, tip));
                }
            });
        }

        private static string PeriodUnitName(string granularity) => granularity switch
        {
            "hour" => Framework.Lang.T("小时"),
            "day" => Framework.Lang.T("天"),
            "month" => Framework.Lang.T("月"),
            _ => Framework.Lang.T("年")
        };
    }
}
