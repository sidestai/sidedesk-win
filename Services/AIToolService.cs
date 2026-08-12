using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// AI 工具服务：为 AI Chat 提供 Function Calling 工具定义和执行能力。
    /// 工具逻辑与 MCP Server 共享同一套底层操作。
    /// </summary>
    public static class AIToolService
    {
        /// <summary>
        /// 获取 OpenAI function calling 格式的 tools 数组
        /// </summary>
        public static List<object> GetToolDefinitions()
        {
            return new List<object>
            {
                new { type = "function", function = new { name = "get_status", description = "获取 SideDesk 运行状态（MCP服务、WebSocket服务、窗口数量等）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "get_settings", description = "获取 SideDesk 当前设置概览（主题、布局、宽度等）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "set_theme", description = "切换 SideDesk 主题模式", parameters = new { type = "object", properties = new { themeMode = new { type = "string", description = "主题模式：Dark、Light 或 System", @enum = new[] { "Dark", "Light", "System" } } }, required = new[] { "themeMode" } } } },
                new { type = "function", function = new { name = "set_layout", description = "切换主显示器停靠布局", parameters = new { type = "object", properties = new { layout = new { type = "string", description = "布局模式：Left、Right、Both 或 None", @enum = new[] { "Left", "Right", "Both", "None" } } }, required = new[] { "layout" } } } },
                new { type = "function", function = new { name = "list_windows", description = "列出所有小屏窗口配置（ID、名称、类型、面板数量）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "list_ws_clients", description = "列出当前已连接的 WebSocket 客户端（浏览器扩展）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "broadcast_ws", description = "向所有已连接的 WebSocket 客户端（浏览器扩展）广播消息", parameters = new { type = "object", properties = new { message = new { type = "string", description = "要广播的消息内容" } }, required = new[] { "message" } } } },
                new { type = "function", function = new { name = "list_notes", description = "获取所有便签列表（含全局便签和模块内便签）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "search_notes", description = "根据关键词搜索便签内容（模糊匹配）", parameters = new { type = "object", properties = new { keyword = new { type = "string", description = "搜索关键词" } }, required = new[] { "keyword" } } } },
                new { type = "function", function = new { name = "add_note", description = "新建一条便签。可指定目标窗口和便签模块（名称或序号），不指定则创建全局便签", parameters = new { type = "object", properties = new { content = new { type = "string", description = "便签内容" }, window = new { type = "string", description = "目标窗口名称、序号或方位（可选，如'主页'、'1'、'左侧'、'右侧'）" }, panel = new { type = "string", description = "目标便签模块标题或序号（可选，如'便签'或'1'）" } }, required = new[] { "content" } } } },
                new { type = "function", function = new { name = "delete_note", description = "删除便签（两步确认）：第一次调用返回匹配便签的预览，用户确认后用 confirm=true 再次调用才真正删除", parameters = new { type = "object", properties = new { target = new { type = "string", description = "便签序号（按 list_notes 顺序，1-based）或内容关键词" }, window = new { type = "string", description = "限定窗口范围（可选，名称/序号/方位）" }, confirm = new { type = "string", description = "是否确认删除：'true' 表示用户已确认，立即执行删除", @enum = new[] { "true", "false" } } }, required = new[] { "target" } } } },
                new { type = "function", function = new { name = "list_clipboard_history", description = "获取最近的剪贴板历史记录（最多100条复制记录，含文本/图片/文件）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "search_clipboard_history", description = "根据关键词搜索剪贴板历史记录（模糊匹配，含文件路径）", parameters = new { type = "object", properties = new { keyword = new { type = "string", description = "搜索关键词" } }, required = new[] { "keyword" } } } },
                new { type = "function", function = new { name = "list_clipboard_favorites", description = "获取所有剪贴板收藏内容（用户手动保存的剪贴板信息）", parameters = new { type = "object", properties = new { }, required = new string[] { } } } },
                new { type = "function", function = new { name = "search_clipboard_favorites", description = "根据关键词搜索剪贴板收藏内容（模糊匹配）", parameters = new { type = "object", properties = new { keyword = new { type = "string", description = "搜索关键词" } }, required = new[] { "keyword" } } } },
                new { type = "function", function = new { name = "get_pomodoro_status", description = "获取番茄闹钟组件当前状态（模式、是否运行中、剩余时间、今日完成数）", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则返回所有窗口的番茄闹钟" } }, required = new string[] { } } } },
                new { type = "function", function = new { name = "start_pomodoro", description = "开始番茄钟（切换到工作模式并开始计时）", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则作用于所有窗口的番茄闹钟" } }, required = new string[] { } } } },
                new { type = "function", function = new { name = "stop_pomodoro", description = "停止/暂停当前番茄闹钟计时（剩余时间保留，可随时继续）", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则作用于所有窗口的番茄闹钟" } }, required = new string[] { } } } },
                new { type = "function", function = new { name = "start_short_break", description = "开始短休息（切换到短休息模式并开始计时）", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则作用于所有窗口的番茄闹钟" } }, required = new string[] { } } } },
                new { type = "function", function = new { name = "start_long_break", description = "开始长休息（切换到长休息模式并开始计时）", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则作用于所有窗口的番茄闹钟" } }, required = new string[] { } } } },
                new { type = "function", function = new { name = "get_pomodoro_count", description = "获取今日已完成的番茄数量", parameters = new { type = "object", properties = new { window = new { type = "string", description = "目标窗口名称或序号（可选，如'2'、'主页'），不传则合计所有窗口" } }, required = new string[] { } } } }
            };
        }

        /// <summary>
        /// 执行工具调用，返回 JSON 字符串结果
        /// </summary>
        public static string ExecuteTool(string toolName, string argumentsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);

                object result = toolName switch
                {
                    "get_status" => Tool_GetStatus(),
                    "get_settings" => Tool_GetSettings(),
                    "set_theme" => Tool_SetTheme(args["themeMode"]?.ToString() ?? ""),
                    "set_layout" => Tool_SetLayout(args["layout"]?.ToString() ?? ""),
                    "list_windows" => Tool_ListWindows(),
                    "list_ws_clients" => Tool_ListWsClients(),
                    "broadcast_ws" => Tool_BroadcastWs(args["message"]?.ToString() ?? ""),
                    "list_notes" => Tool_ListNotes(),
                    "search_notes" => Tool_SearchNotes(args["keyword"]?.ToString() ?? ""),
                    "add_note" => Tool_AddNote(args["content"]?.ToString() ?? "", args["window"]?.ToString(), args["panel"]?.ToString()),
                    "delete_note" => Tool_DeleteNote(args["target"]?.ToString() ?? "", args["window"]?.ToString(), args["confirm"]?.ToString() == "true"),
                    "list_clipboard_history" => Tool_ListClipboardHistory(),
                    "search_clipboard_history" => Tool_SearchClipboardHistory(args["keyword"]?.ToString() ?? ""),
                    "list_clipboard_favorites" => Tool_ListClipboardFavorites(),
                    "search_clipboard_favorites" => Tool_SearchClipboardFavorites(args["keyword"]?.ToString() ?? ""),
                    "get_pomodoro_status" => Tool_GetPomodoroStatus(args["window"]?.ToString()),
                    "start_pomodoro" => Tool_StartPomodoro(args["window"]?.ToString()),
                    "stop_pomodoro" => Tool_StopPomodoro(args["window"]?.ToString()),
                    "start_short_break" => Tool_StartShortBreak(args["window"]?.ToString()),
                    "start_long_break" => Tool_StartLongBreak(args["window"]?.ToString()),
                    "get_pomodoro_count" => Tool_GetPomodoroCount(args["window"]?.ToString()),
                    _ => new { error = $"未知工具: {toolName}" }
                };

                return JsonConvert.SerializeObject(result, Formatting.None);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message });
            }
        }

        // ═══════════ 工具实现（MCP Server 与 AI Chat 共用） ═══════════

        public static object Tool_GetStatus()
        {
            var app = (App)System.Windows.Application.Current;
            return new
            {
                appName = "元桌小屏 (SideDesk)",
                mcpServer = new { running = McpServerService.Instance.IsRunning, port = McpServerService.Port },
                webSocketServer = new
                {
                    running = WebSocketServerService.Instance.IsRunning,
                    port = WebSocketServerService.Port,
                    clientCount = WebSocketServerService.Instance.ClientCount
                },
                windowCount = app.WindowConfigs.Windows.Count,
                themeMode = app.AppSettings.ThemeMode,
                cloudSyncEnabled = app.AppSettings.EnableCloudSync,
                accountName = app.AppSettings.AccountName ?? Framework.Lang.T("(未登录)")
            };
        }

        public static object Tool_GetSettings()
        {
            var app = (App)System.Windows.Application.Current;
            var s = app.AppSettings;
            return new
            {
                themeMode = s.ThemeMode,
                backgroundOpacity = s.BackgroundOpacity,
                enableTitleLock = s.EnableTitleLock,
                enableWindowSwitchHotkey = s.EnableWindowSwitchHotkey,
                enableCloudSync = s.EnableCloudSync,
                enableMcpServer = s.EnableMcpServer,
                accountName = s.AccountName ?? Framework.Lang.T("(未登录)"),
                monitors = s.Monitors.Select((m, i) => new
                {
                    index = i,
                    layout = m.Layout.ToString(),
                    leftPanelWidth = m.LeftPanelWidth,
                    rightPanelWidth = m.RightPanelWidth
                }).ToList()
            };
        }

        public static object Tool_SetTheme(string themeMode)
        {
            var valid = new[] { "Dark", "Light", "System" };
            if (!valid.Contains(themeMode, StringComparer.OrdinalIgnoreCase))
                return new { success = false, error = $"无效主题: {themeMode}，可选值: Dark, Light, System" };

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var app = (App)System.Windows.Application.Current;
                app.AppSettings.ThemeMode = themeMode;
                App.ApplyTheme(themeMode);
                app.SaveState();
            });
            return new { success = true, message = $"主题已切换为: {themeMode}" };
        }

        public static object Tool_SetLayout(string layout)
        {
            var valid = new[] { "Left", "Right", "Both", "None" };
            if (!valid.Contains(layout, StringComparer.OrdinalIgnoreCase))
                return new { success = false, error = $"无效布局: {layout}，可选值: Left, Right, Both, None" };

            var mode = Enum.Parse<LayoutMode>(layout, ignoreCase: true);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var app = (App)System.Windows.Application.Current;
                app.SwitchLayout(mode);
            });
            return new { success = true, message = $"布局已切换为: {layout}" };
        }

        public static object Tool_ListWindows()
        {
            var app = (App)System.Windows.Application.Current;
            return app.WindowConfigs.Windows.Select(w => new
            {
                id = w.Id,
                name = w.Name,
                type = w.Type.ToString(),
                url = w.Url ?? "",
                panelCount = w.Panels.Count,
                panels = w.Panels.Select(p => new { id = p.Id, type = p.Type.ToString(), title = p.Title ?? "" }).ToList(),
                showInTabs = w.ShowInTabs
            }).ToList();
        }

        public static object Tool_ListWsClients()
        {
            var clients = WebSocketServerService.Instance.GetConnectedClients();
            return new
            {
                isRunning = WebSocketServerService.Instance.IsRunning,
                clientCount = clients.Count,
                clients = clients.Select(c => new
                {
                    id = c.Id.ToString(),
                    uniqId = c.UniqId,
                    title = c.Title,
                    url = c.Url,
                    connectedAt = c.ConnectedAt.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList()
            };
        }

        public static object Tool_BroadcastWs(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return new { success = false, error = "消息内容不能为空" };

            _ = WebSocketServerService.Instance.BroadcastAsync(message);
            return new { success = true, message = $"已广播到 {WebSocketServerService.Instance.ClientCount} 个客户端" };
        }

        public static object Tool_ListNotes()
        {
            var app = (App)System.Windows.Application.Current;
            var result = new List<object>();

            foreach (var note in StickyNoteManager.GetAll())
            {
                result.Add(new
                {
                    id = note.Id,
                    content = note.Content,
                    preview = note.Preview,
                    source = "global",
                    updatedAt = note.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            foreach (var w in app.WindowConfigs.Windows)
            {
                foreach (var p in w.Panels)
                {
                    if (p.StickyNotes == null || p.StickyNotes.Count == 0) continue;
                    foreach (var note in p.StickyNotes)
                    {
                        result.Add(new
                        {
                            id = note.Id,
                            content = note.Content,
                            preview = note.Preview,
                            source = $"panel:{p.Title ?? p.Type.ToString()}@{w.Name}",
                            updatedAt = note.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                }
            }

            return new { total = result.Count, notes = result };
        }

        public static object Tool_SearchNotes(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new { keyword = "", matchCount = 0, notes = new List<object>() };

            var app = (App)System.Windows.Application.Current;
            var kw = keyword.Trim();
            var result = new List<object>();

            foreach (var note in StickyNoteManager.GetAll())
            {
                if (note.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    result.Add(new { id = note.Id, content = note.Content, preview = note.Preview, source = "global", updatedAt = note.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss") });
            }

            foreach (var w in app.WindowConfigs.Windows)
            {
                foreach (var p in w.Panels)
                {
                    if (p.StickyNotes == null || p.StickyNotes.Count == 0) continue;
                    foreach (var note in p.StickyNotes)
                    {
                        if (note.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            result.Add(new { id = note.Id, content = note.Content, preview = note.Preview, source = $"panel:{p.Title ?? p.Type.ToString()}@{w.Name}", updatedAt = note.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss") });
                    }
                }
            }

            return new { keyword = kw, matchCount = result.Count, notes = result };
        }

        // ═══════════ 剪贴板历史 / 收藏工具 ═══════════

        /// <summary>获取最近的剪贴板历史记录（最多 100 条复制记录）</summary>
        public static object Tool_ListClipboardHistory()
        {
            var svc = EnsureClipboardService();
            var items = svc.ClipboardHistory.Select((e, i) => new
            {
                index = i + 1,
                content = e.Text,
                type = e.ContentType.ToString(),
                filePaths = e.FilePaths ?? new List<string>(),
                time = e.Time.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToList();
            return new { total = items.Count, capacity = ClipboardService.MaxItems, items };
        }

        /// <summary>根据关键词搜索剪贴板历史记录（文本与文件路径均参与匹配）</summary>
        public static object Tool_SearchClipboardHistory(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new { keyword = "", matchCount = 0, items = new List<object>() };

            var svc = EnsureClipboardService();
            var kw = keyword.Trim();
            var items = new List<object>();
            int index = 0;
            foreach (var e in svc.ClipboardHistory)
            {
                index++;
                bool match = e.Text.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || (e.FilePaths != null && e.FilePaths.Any(f => f.Contains(kw, StringComparison.OrdinalIgnoreCase)));
                if (match)
                    items.Add(new { index, content = e.Text, type = e.ContentType.ToString(), filePaths = e.FilePaths ?? new List<string>(), time = e.Time.ToString("yyyy-MM-dd HH:mm:ss") });
            }
            return new { keyword = kw, matchCount = items.Count, total = svc.ClipboardHistory.Count, items };
        }

        /// <summary>获取所有剪贴板收藏内容（用户手动保存的剪贴板信息）</summary>
        public static object Tool_ListClipboardFavorites()
        {
            var app = (App)System.Windows.Application.Current;
            var items = CollectClipboardFavoriteItems(app)
                .Select(t => new { id = t.item.Id, content = t.item.Content, type = t.item.ContentType, source = t.source, savedAt = t.item.SavedAt.ToString("yyyy-MM-dd HH:mm:ss") })
                .ToList();
            return new { total = items.Count, items };
        }

        /// <summary>根据关键词搜索剪贴板收藏内容</summary>
        public static object Tool_SearchClipboardFavorites(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new { keyword = "", matchCount = 0, items = new List<object>() };

            var app = (App)System.Windows.Application.Current;
            var kw = keyword.Trim();
            var items = CollectClipboardFavoriteItems(app)
                .Where(t => t.item.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                .Select(t => new { id = t.item.Id, content = t.item.Content, type = t.item.ContentType, source = t.source, savedAt = t.item.SavedAt.ToString("yyyy-MM-dd HH:mm:ss") })
                .ToList();
            return new { keyword = kw, matchCount = items.Count, items };
        }

        /// <summary>收集所有窗口的剪贴板收藏条目（含分组子模块）</summary>
        private static IEnumerable<(ClipboardSavedItem item, string source)> CollectClipboardFavoriteItems(App app)
        {
            foreach (var w in app.WindowConfigs.Windows)
            {
                foreach (var p in w.Panels)
                {
                    if (p.ClipboardItems != null)
                        foreach (var ci in p.ClipboardItems)
                            yield return (ci, $"panel:{p.Title ?? p.Type.ToString()}@{w.Name}");
                    if (p.Type == PanelType.Group && p.ChildPanels != null)
                        foreach (var c in p.ChildPanels.Where(c => c.ClipboardItems != null))
                            foreach (var ci in c.ClipboardItems!)
                                yield return (ci, $"panel:{c.Title ?? c.Type.ToString()}@{w.Name}");
                }
            }
        }

        /// <summary>获取剪贴板历史服务单例；未启动时在 UI 线程自动启动监控，确保 MCP/AI 始终可访问最近复制历史</summary>
        private static ClipboardService EnsureClipboardService()
        {
            var svc = ClipboardService.Instance;
            if (!svc.IsRunning)
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (svc.IsRunning) return;
                        IntPtr hwnd = IntPtr.Zero;
                        try
                        {
                            var mw = System.Windows.Application.Current?.MainWindow;
                            if (mw != null)
                                hwnd = new System.Windows.Interop.WindowInteropHelper(mw).EnsureHandle();
                        }
                        catch { }
                        svc.Start(hwnd);
                    });
                }
                catch { }
            }
            return svc;
        }

        public static object Tool_AddNote(string content, string? window = null, string? panel = null)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new { success = false, error = "便签内容不能为空" };

            var app = (App)System.Windows.Application.Current;
            var note = new StickyNote { Content = content.Trim() };

            // 指定了窗口：添加到该窗口的便签模块（per-panel 存储）
            if (!string.IsNullOrWhiteSpace(window))
            {
                var win = ResolveWindow(app, window.Trim());
                if (win == null)
                    return new { success = false, error = $"找不到窗口: {window}（可用窗口: {string.Join(", ", app.WindowConfigs.Windows.Select(w => w.Name))}）" };

                var target = ResolveStickyNotesPanel(win, panel?.Trim());
                if (target == null)
                    return new { success = false, error = $"窗口'{win.Name}'中找不到便签模块" };

                target.StickyNotes!.Insert(0, note);
                app.WindowConfigs.Save();
                app.Dispatcher.BeginInvoke(() => app.RefreshAllNativePanels());
                return new { success = true, message = $"便签已创建到窗口'{win.Name}'的便签模块", id = note.Id, location = $"panel:{target.Title ?? "StickyNotes"}@{win.Name}" };
            }

            // 未指定窗口：创建全局便签
            StickyNoteManager.Add(note);
            app.Dispatcher.BeginInvoke(() => app.RefreshAllNativePanels());
            return new { success = true, message = "全局便签已创建", id = note.Id, location = "global" };
        }

        public static object Tool_DeleteNote(string target, string? window = null, bool confirm = false)
        {
            if (string.IsNullOrWhiteSpace(target))
                return new { success = false, error = "请指定要删除的便签（序号或内容关键词）" };

            var app = (App)System.Windows.Application.Current;

            // 收集候选便签（与 list_notes 相同顺序）
            var candidates = new List<(StickyNote note, string source, PanelItem? panel)>();

            if (!string.IsNullOrWhiteSpace(window))
            {
                // 限定窗口范围
                var win = ResolveWindow(app, window.Trim());
                if (win == null)
                    return new { success = false, error = $"找不到窗口: {window}" };

                foreach (var p in win.Panels)
                {
                    if (p.StickyNotes != null)
                        candidates.AddRange(p.StickyNotes.Select(n => (n, $"panel:{p.Title ?? p.Type.ToString()}@{win.Name}", (PanelItem?)p)));
                    if (p.Type == PanelType.Group && p.ChildPanels != null)
                        foreach (var c in p.ChildPanels.Where(c => c.StickyNotes != null))
                            candidates.AddRange(c.StickyNotes!.Select(n => (n, $"panel:{c.Title ?? c.Type.ToString()}@{win.Name}", (PanelItem?)c)));
                }
            }
            else
            {
                // 全局 + 所有窗口模块
                foreach (var n in StickyNoteManager.GetAll())
                    candidates.Add((n, "global", null));
                foreach (var w in app.WindowConfigs.Windows)
                {
                    foreach (var p in w.Panels)
                    {
                        if (p.StickyNotes != null)
                            candidates.AddRange(p.StickyNotes.Select(n => (n, $"panel:{p.Title ?? p.Type.ToString()}@{w.Name}", (PanelItem?)p)));
                        if (p.Type == PanelType.Group && p.ChildPanels != null)
                            foreach (var c in p.ChildPanels.Where(c => c.StickyNotes != null))
                                candidates.AddRange(c.StickyNotes!.Select(n => (n, $"panel:{c.Title ?? c.Type.ToString()}@{w.Name}", (PanelItem?)c)));
                    }
                }
            }

            if (candidates.Count == 0)
                return new { success = false, error = "当前没有任何便签" };

            // 按序号或内容关键词定位
            (StickyNote note, string source, PanelItem? panel)? match = null;
            if (int.TryParse(target.Trim(), out int idx) && idx >= 1 && idx <= candidates.Count)
            {
                match = candidates[idx - 1];
            }
            else
            {
                var kw = target.Trim();
                var matches = candidates.Where(c => c.note.Content.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                    match = matches[0];
                else if (matches.Count > 1)
                    return new { success = false, error = $"匹配到 {matches.Count} 条便签，请用序号精确指定", matches = matches.Select((m, i) => new { index = candidates.IndexOf(m) + 1, preview = m.note.Preview, source = m.source }).ToList() };
            }

            if (match == null)
                return new { success = false, error = $"找不到匹配的便签: {target}（当前共 {candidates.Count} 条）" };

            var (note, source, panel) = match.Value;

            // 未确认：返回预览，等待用户确认
            if (!confirm)
            {
                return new
                {
                    success = false,
                    pendingConfirm = true,
                    message = $"找到以下便签，请向用户确认是否删除：",
                    note = new { id = note.Id, content = note.Content, source, index = candidates.IndexOf(match.Value) + 1 },
                    hint = "用户确认后，请用 confirm=\"true\" 再次调用 delete_note"
                };
            }

            // 已确认：执行删除
            if (panel != null)
            {
                panel.StickyNotes!.RemoveAll(n => n.Id == note.Id);
                app.WindowConfigs.Save();
            }
            else
            {
                StickyNoteManager.Delete(note.Id);
            }
            app.Dispatcher.BeginInvoke(() => app.RefreshAllNativePanels());
            return new { success = true, message = $"便签已删除（{note.Preview}）", id = note.Id, source };
        }

        // ═══════════ 番茄闹钟工具 ═══════════

        /// <summary>收集番茄闹钟组件（顶层 + 分组内子模块）；win 非空时仅限该窗口</summary>
        private static List<(PanelItem item, string source)> CollectPomodoroPanels(App app, WindowConfig? win = null)
        {
            var list = new List<(PanelItem, string)>();
            IEnumerable<WindowConfig> windows = win != null ? new[] { win } : app.WindowConfigs.Windows;
            foreach (var w in windows)
            {
                foreach (var p in w.Panels)
                {
                    if (p.Type == PanelType.Pomodoro)
                        list.Add((p, $"{p.Title ?? "番茄闹钟"}@{w.Name}"));
                    if (p.Type == PanelType.Group && p.ChildPanels != null)
                        foreach (var c in p.ChildPanels.Where(c => c.Type == PanelType.Pomodoro))
                            list.Add((c, $"{c.Title ?? "番茄闹钟"}@{w.Name}"));
                }
            }
            return list;
        }

        /// <summary>模式显示名称</summary>
        private static string GetPomodoroModeLabel(string mode) => mode switch
        {
            "ShortBreak" => Framework.Lang.T("短休息"),
            "LongBreak" => Framework.Lang.T("长休息"),
            _ => Framework.Lang.T("番茄钟")
        };

        /// <summary>生成单个番茄组件的状态快照</summary>
        private static object PomodoroSnapshot(PanelItem p, string source)
        {
            var s = p.Pomodoro ??= new PomodoroSettings();
            s.EnsureTodayCount();
            var remaining = s.GetRemaining();
            return new
            {
                source,
                mode = s.Mode,
                modeLabel = GetPomodoroModeLabel(s.Mode),
                isRunning = s.IsRunning,
                remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds),
                remainingText = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}",
                completedCount = s.CompletedCount
            };
        }

        /// <summary>
        /// 解析番茄闹钟作用域：window 非空时按名称/序号/方位定位窗口；
        /// 解析成功 panels 非空返回 null，否则返回错误描述。
        /// </summary>
        private static string? TryResolvePomodoroPanels(App app, string? window, out List<(PanelItem item, string source)> panels)
        {
            panels = new List<(PanelItem, string)>();
            WindowConfig? win = null;
            if (!string.IsNullOrWhiteSpace(window))
            {
                win = ResolveWindow(app, window.Trim());
                if (win == null)
                    return $"找不到窗口: {window}（可用窗口: {string.Join(", ", app.WindowConfigs.Windows.Select((w, i) => $"{i + 1}.{w.Name}"))}）";
            }

            panels = CollectPomodoroPanels(app, win);
            if (panels.Count == 0)
                return win == null
                    ? Framework.Lang.T("未找到番茄闹钟组件，请先在侧栏添加「番茄闹钟」组件")
                    : Framework.Lang.TF("窗口'{0}'下没有番茄闹钟组件，请先在该窗口添加「番茄闹钟」组件", win.Name);
            return null;
        }

        /// <summary>获取番茄闹钟组件当前状态（可按窗口定位）</summary>
        public static object Tool_GetPomodoroStatus(string? window = null)
        {
            var app = (App)System.Windows.Application.Current;
            var err = TryResolvePomodoroPanels(app, window, out var panels);
            if (err != null)
                return new { success = false, error = err };
            return new { total = panels.Count, items = panels.Select(t => PomodoroSnapshot(t.item, t.source)).ToList() };
        }

        /// <summary>获取今日已完成的番茄数量（可按窗口定位，默认多个组件合计）</summary>
        public static object Tool_GetPomodoroCount(string? window = null)
        {
            var app = (App)System.Windows.Application.Current;
            var err = TryResolvePomodoroPanels(app, window, out var panels);
            if (err != null)
                return new { success = false, error = err };

            int total = 0;
            foreach (var (p, _) in panels)
            {
                var s = p.Pomodoro ??= new PomodoroSettings();
                s.EnsureTodayCount();
                total += s.CompletedCount;
            }
            return new { date = DateTime.Now.ToString("yyyy-MM-dd"), completedCount = total, componentCount = panels.Count };
        }

        /// <summary>开始番茄钟（切换到工作模式并开始计时）</summary>
        public static object Tool_StartPomodoro(string? window = null) => ApplyPomodoroAction("Work", start: true, window);

        /// <summary>开始短休息（切换到短休息模式并开始计时）</summary>
        public static object Tool_StartShortBreak(string? window = null) => ApplyPomodoroAction("ShortBreak", start: true, window);

        /// <summary>开始长休息（切换到长休息模式并开始计时）</summary>
        public static object Tool_StartLongBreak(string? window = null) => ApplyPomodoroAction("LongBreak", start: true, window);

        /// <summary>停止/暂停当前计时（保持当前模式，剩余时间保留）</summary>
        public static object Tool_StopPomodoro(string? window = null) => ApplyPomodoroAction(null, start: false, window);

        /// <summary>
        /// 番茄闹钟统一控制：mode 非空时先切换模式（重置为新模式满时长），
        /// start=true 开始/继续，start=false 暂停并结算剩余时间。
        /// 状态变更在 UI 线程执行，完成后保存配置并重绘面板（组件重绘时自动恢复计时器）。
        /// </summary>
        private static object ApplyPomodoroAction(string? mode, bool start, string? window = null)
        {
            var app = (App)System.Windows.Application.Current;
            var err = TryResolvePomodoroPanels(app, window, out var panels);
            if (err != null)
                return new { success = false, error = err };

            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var (p, _) in panels)
                    {
                        var s = p.Pomodoro ??= new PomodoroSettings();
                        s.EnsureTodayCount();

                        // 切换模式：重置为新模式满时长（与组件内手动切换逻辑一致，但不计入完成数）
                        if (!string.IsNullOrEmpty(mode) && s.Mode != mode)
                        {
                            s.Mode = mode;
                            s.RemainingSeconds = s.GetModeTotalSeconds();
                            s.IsRunning = false;
                            s.StartTime = null;
                        }

                        if (start)
                        {
                            if (s.GetRemaining().TotalSeconds <= 0)
                                s.RemainingSeconds = s.GetModeTotalSeconds();
                            if (!s.IsRunning)
                            {
                                s.IsRunning = true;
                                s.StartTime = DateTime.Now;
                            }
                        }
                        else
                        {
                            // 暂停：结算剩余时间（与组件内暂停逻辑一致）
                            if (s.IsRunning && s.StartTime.HasValue)
                                s.RemainingSeconds = Math.Max(0, s.RemainingSeconds - (DateTime.Now - s.StartTime.Value).TotalSeconds);
                            s.IsRunning = false;
                            s.StartTime = null;
                        }
                    }

                    app.WindowConfigs.Save();
                    app.RefreshAllNativePanels();
                });
            }
            catch (Exception ex)
            {
                return new { success = false, error = $"操作失败: {ex.Message}" };
            }

            string actionLabel = start
                ? Framework.Lang.TF("已开始{0}", GetPomodoroModeLabel(mode ?? panels[0].item.Pomodoro!.Mode))
                : Framework.Lang.T("已停止计时");
            return new
            {
                success = true,
                message = $"{actionLabel}（共 {panels.Count} 个番茄闹钟组件）",
                items = panels.Select(t => PomodoroSnapshot(t.item, t.source)).ToList()
            };
        }

        /// <summary>按名称、序号或方位（左侧/右侧）解析窗口</summary>
        private static WindowConfig? ResolveWindow(App app, string nameOrIndex)
        {
            // 方位解析：左侧/右侧 → 当前活动窗口
            if (nameOrIndex.Contains("左") || nameOrIndex.Equals("left", StringComparison.OrdinalIgnoreCase))
            {
                var dock = app.WindowConfigs.LeftDock;
                if (dock.WindowIds.Count > 0)
                {
                    int idx = Math.Clamp(dock.ActiveIndex, 0, dock.WindowIds.Count - 1);
                    return app.WindowConfigs.Windows.FirstOrDefault(w => w.Id == dock.WindowIds[idx]);
                }
                return null;
            }
            if (nameOrIndex.Contains("右") || nameOrIndex.Equals("right", StringComparison.OrdinalIgnoreCase))
            {
                var dock = app.WindowConfigs.RightDock;
                if (dock.WindowIds.Count > 0)
                {
                    int idx = Math.Clamp(dock.ActiveIndex, 0, dock.WindowIds.Count - 1);
                    return app.WindowConfigs.Windows.FirstOrDefault(w => w.Id == dock.WindowIds[idx]);
                }
                return null;
            }

            if (int.TryParse(nameOrIndex, out int idx2) && idx2 >= 1 && idx2 <= app.WindowConfigs.Windows.Count)
                return app.WindowConfigs.Windows[idx2 - 1];
            return app.WindowConfigs.Windows.FirstOrDefault(w => w.Name.Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按标题或 1-based 序号解析窗口内的便签模块（含分组子模块）</summary>
        private static PanelItem? ResolveStickyNotesPanel(WindowConfig win, string? nameOrIndex)
        {
            // 收集所有便签模块（顶层 + 分组内）
            var notePanels = new List<PanelItem>();
            foreach (var p in win.Panels)
            {
                if (p.Type == PanelType.StickyNotes) notePanels.Add(p);
                if (p.Type == PanelType.Group && p.ChildPanels != null)
                    notePanels.AddRange(p.ChildPanels.Where(c => c.Type == PanelType.StickyNotes));
            }

            if (notePanels.Count == 0) return null;

            if (string.IsNullOrWhiteSpace(nameOrIndex))
                return notePanels[0]; // 默认第一个便签模块

            if (int.TryParse(nameOrIndex, out int idx) && idx >= 1 && idx <= notePanels.Count)
                return notePanels[idx - 1];

            return notePanels.FirstOrDefault(p => (p.Title ?? "").Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase));
        }
    }
}
