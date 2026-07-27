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
                new { type = "function", function = new { name = "delete_note", description = "删除便签（两步确认）：第一次调用返回匹配便签的预览，用户确认后用 confirm=true 再次调用才真正删除", parameters = new { type = "object", properties = new { target = new { type = "string", description = "便签序号（按 list_notes 顺序，1-based）或内容关键词" }, window = new { type = "string", description = "限定窗口范围（可选，名称/序号/方位）" }, confirm = new { type = "string", description = "是否确认删除：'true' 表示用户已确认，立即执行删除", @enum = new[] { "true", "false" } } }, required = new[] { "target" } } } }
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
                accountName = app.AppSettings.AccountName ?? "(未登录)"
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
                accountName = s.AccountName ?? "(未登录)",
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
