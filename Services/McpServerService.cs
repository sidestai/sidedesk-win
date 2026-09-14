using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinimalMcp;

namespace SideDesk.Services
{
    /// <summary>
    /// MCP (Model Context Protocol) 服务端：通过 HTTP/SSE 传输暴露 SideDesk 核心功能，
    /// 供 AI 助手或外部工具调用。监听端口 19192。
    /// </summary>
    public sealed class McpServerService
    {
        private static readonly Lazy<McpServerService> _instance = new(() => new McpServerService());
        public static McpServerService Instance => _instance.Value;

        /// <summary>MCP HTTP 监听端口</summary>
        public const int Port = 19192;

        private McpApp? _mcpApp;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private volatile bool _running;

        /// <summary>服务是否正在运行</summary>
        public bool IsRunning => _running;

        private McpServerService()
        {
            // 工具注册表变化（网页上改了配置 / 登录态变化）：重建工具列表
            McpToolRegistryService.Instance.RegistryChanged += () =>
            {
                if (!_running) return;
                _ = Task.Run(() =>
                {
                    try { Restart(); }
                    catch (Exception ex) { Debug.WriteLine($"[McpServer] registry rebuild error: {ex.Message}"); }
                });
            };
        }

        /// <summary>
        /// 启动 MCP Server（幂等）
        /// </summary>
        public void Start()
        {
            if (_running) return;

            try
            {
                _cts = new CancellationTokenSource();
                _mcpApp = BuildMcpApp();
                _running = true;

                _runTask = Task.Run(async () =>
                {
                    try
                    {
                        await _mcpApp.RunHttpAsync(port: Port);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[McpServer] run error: {ex.Message}");
                        _running = false;
                    }
                });

                Debug.WriteLine($"[McpServer] started, listening on http://127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                _running = false;
                Debug.WriteLine($"[McpServer] start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止 MCP Server
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _mcpApp = null;

            Debug.WriteLine("[McpServer] stopped");
        }

        /// <summary>重启 MCP Server（工具注册表变化时重建，SSE 客户端会自动重连拿到新工具列表）</summary>
        public void Restart()
        {
            Stop();
            Start();
        }

        /// <summary>
        /// 构建 MCP App 实例，注册所有 Tools / Resources
        /// </summary>
        private McpApp BuildMcpApp()
        {
            var mcp = McpApp.Create("SideDesk", "1.0.0");

            // ═══════════ 网页版工具注册表生效时：按服务端清单动态注册（执行统一走 AIToolService） ═══════════
            if (McpToolRegistryService.Instance.IsActive)
            {
                var tools = McpToolRegistryService.Instance.Tools.Where(t => t.Enabled).ToList();
                if (tools.Count > 0)
                {
                    foreach (var t in tools)
                        RegisterDynamicTool(mcp, t);

                    RegisterResources(mcp);
                    Debug.WriteLine($"[McpServer] registered {tools.Count} tools from server registry");
                    return mcp;
                }
            }

            // ═══════════ 未登录/未拉取到：内置硬编码工具（隐私模式兼容） ═══════════

            // ═══════════ Tools（统一委托 AIToolService，与 AI Chat Function Calling 共用同一套逻辑） ═══════════

            mcp.AddTool("get_settings", "获取 SideDesk 当前设置概览（主题、布局、宽度等）", () => AIToolService.Tool_GetSettings());

            mcp.AddTool("set_theme", "切换 SideDesk 主题模式（Dark/Light/System）", (string themeMode) => AIToolService.Tool_SetTheme(themeMode));

            mcp.AddTool("list_windows", "列出所有微屏窗口配置（ID、名称、类型、面板数量）", () => AIToolService.Tool_ListWindows());

            mcp.AddTool("list_ws_clients", "列出当前已连接的 WebSocket 客户端（浏览器扩展）", () => AIToolService.Tool_ListWsClients());

            mcp.AddTool("broadcast_ws", "向所有已连接的 WebSocket 客户端（浏览器扩展）广播消息", (string message) => AIToolService.Tool_BroadcastWs(message));

            mcp.AddTool("set_layout", "切换主显示器停靠布局（Left/Right/Both/None）", (string layout) => AIToolService.Tool_SetLayout(layout));

            mcp.AddTool("get_status", "获取 SideDesk 运行状态（MCP服务、WebSocket服务、窗口数量等）", () => AIToolService.Tool_GetStatus());

            mcp.AddTool("list_notes", "获取所有便签列表（含全局便签和模块内便签）", () => AIToolService.Tool_ListNotes());

            mcp.AddTool("search_notes", "根据关键词搜索便签内容（模糊匹配）", (string keyword) => AIToolService.Tool_SearchNotes(keyword));

            mcp.AddTool("create_note", "新建一条便签。可指定目标窗口和便签模块（名称或序号），不指定则创建全局便签", (string content, string window, string panel) => AIToolService.Tool_AddNote(content, window, panel));

            mcp.AddTool("delete_note", "删除便签（两步确认）：第一次调用返回匹配便签的预览，用户确认后用 confirm=true 再次调用才真正删除", (string target, string window, string confirm) => AIToolService.Tool_DeleteNote(target, window, confirm == "true"));

            // ═══════════ 剪贴板历史 / 收藏 ═══════════

            mcp.AddTool("list_clipboard_history", "获取最近的剪贴板历史记录（最多100条复制记录，含文本/图片/文件）", () => AIToolService.Tool_ListClipboardHistory());

            mcp.AddTool("search_clipboard_history", "根据关键词搜索剪贴板历史记录（模糊匹配，含文件路径）", (string keyword) => AIToolService.Tool_SearchClipboardHistory(keyword));

            mcp.AddTool("list_clipboard_favorites", "获取所有剪贴板收藏内容（用户手动保存的剪贴板信息）", () => AIToolService.Tool_ListClipboardFavorites());

            mcp.AddTool("search_clipboard_favorites", "根据关键词搜索剪贴板收藏内容（模糊匹配）", (string keyword) => AIToolService.Tool_SearchClipboardFavorites(keyword));

            // ═══════════ 番茄闹钟 ═══════════

            mcp.AddTool("get_pomodoro_status", "获取番茄闹钟组件当前状态（模式、是否运行中、剩余时间、今日完成数）。window 传窗口名称或序号（如'2'、'主页'）定位特定窗口，传空则返回所有窗口", (string window) => AIToolService.Tool_GetPomodoroStatus(window));

            mcp.AddTool("start_pomodoro", "开始番茄钟（切换到工作模式并开始计时）。window 传窗口名称或序号定位特定窗口，传空则作用于所有窗口", (string window) => AIToolService.Tool_StartPomodoro(window));

            mcp.AddTool("stop_pomodoro", "停止/暂停当前番茄闹钟计时（剩余时间保留，可随时继续）。window 传窗口名称或序号定位特定窗口，传空则作用于所有窗口", (string window) => AIToolService.Tool_StopPomodoro(window));

            mcp.AddTool("start_short_break", "开始短休息（切换到短休息模式并开始计时）。window 传窗口名称或序号定位特定窗口，传空则作用于所有窗口", (string window) => AIToolService.Tool_StartShortBreak(window));

            mcp.AddTool("start_long_break", "开始长休息（切换到长休息模式并开始计时）。window 传窗口名称或序号定位特定窗口，传空则作用于所有窗口", (string window) => AIToolService.Tool_StartLongBreak(window));

            mcp.AddTool("get_pomodoro_count", "获取今日已完成的番茄数量。window 传窗口名称或序号定位特定窗口，传空则合计所有窗口", (string window) => AIToolService.Tool_GetPomodoroCount(window));

            RegisterResources(mcp);

            return mcp;
        }

        /// <summary>Resources 注册（两种工具来源路径共用）</summary>
        private void RegisterResources(McpApp mcp)
        {
            // ═══════════ Resources ═══════════

            mcp.AddResource("sidedesk://status", "SideDesk Status", "SideDesk 应用运行状态", () =>
            {
                var app = (App)System.Windows.Application.Current;
                return new
                {
                    running = true,
                    version = "1.0.0",
                    theme = app.AppSettings.ThemeMode,
                    windows = app.WindowConfigs.Windows.Count,
                    wsClients = WebSocketServerService.Instance.ClientCount
                };
            });

            mcp.AddResource("sidedesk://windows", "Window Configs", "所有微屏窗口配置", () =>
            {
                var app = (App)System.Windows.Application.Current;
                return app.WindowConfigs.Windows.Select(w => new
                {
                    id = w.Id,
                    name = w.Name,
                    type = w.Type.ToString(),
                    panelCount = w.Panels.Count
                }).ToList();
            });
        }

        /// <summary>
        /// 按注册表定义动态注册工具：从 paramsSchema 解析参数名，按参数个数选用
        /// 0~3 个 string 参数的 lambda（MinimalMcp 按 lambda 签名自动生成 Schema），
        /// 执行统一委托 AIToolService.ExecuteTool（返回 JToken 避免二次 JSON 转义）。
        /// </summary>
        private static void RegisterDynamicTool(McpApp mcp, McpToolDef t)
        {
            var paramNames = ParseParamNames(t.ParamsSchema);
            string name = t.Name;

            object Exec(System.Collections.Generic.Dictionary<string, string> args)
            {
                var argsObj = new Newtonsoft.Json.Linq.JObject();
                foreach (var kv in args) argsObj[kv.Key] = kv.Value;
                var resultJson = AIToolService.ExecuteTool(name, argsObj.ToString(Newtonsoft.Json.Formatting.None));
                try { return Newtonsoft.Json.Linq.JToken.Parse(resultJson); }
                catch { return resultJson; }
            }

            switch (paramNames.Count)
            {
                case 0:
                    mcp.AddTool(t.Name, t.Description, () => Exec(new System.Collections.Generic.Dictionary<string, string>()));
                    break;
                case 1:
                    {
                        var p1 = paramNames[0];
                        mcp.AddTool(t.Name, t.Description, (string a1) => Exec(new() { [p1] = a1 ?? "" }));
                    }
                    break;
                case 2:
                    {
                        var p1 = paramNames[0]; var p2 = paramNames[1];
                        mcp.AddTool(t.Name, t.Description, (string a1, string a2) => Exec(new() { [p1] = a1 ?? "", [p2] = a2 ?? "" }));
                    }
                    break;
                case 3:
                    {
                        var p1 = paramNames[0]; var p2 = paramNames[1]; var p3 = paramNames[2];
                        mcp.AddTool(t.Name, t.Description, (string a1, string a2, string a3) =>
                            Exec(new() { [p1] = a1 ?? "", [p2] = a2 ?? "", [p3] = a3 ?? "" }));
                    }
                    break;
                default:
                    // 参数 > 3：退化为单参数 JSON 串，描述中说明用法
                    mcp.AddTool(t.Name, Framework.Lang.TF("system.toolArgsJsonNote", t.Description),
                        (string arguments) =>
                        {
                            try
                            {
                                var obj = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
                                var dict = obj.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
                                return Exec(dict);
                            }
                            catch (Exception ex)
                            {
                                return new { error = $"arguments 不是合法 JSON: {ex.Message}" };
                            }
                        });
                    break;
            }
        }

        /// <summary>从参数 Schema 解析参数名列表（required 优先，其次 properties 全部）</summary>
        private static System.Collections.Generic.List<string> ParseParamNames(string? paramsSchema)
        {
            var names = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(paramsSchema)) return names;
            try
            {
                var schema = Newtonsoft.Json.Linq.JObject.Parse(paramsSchema);
                if (schema["required"] is Newtonsoft.Json.Linq.JArray req)
                    foreach (var r in req)
                    {
                        var s = r.ToString();
                        if (!string.IsNullOrEmpty(s)) names.Add(s);
                    }
                if (names.Count == 0 && schema["properties"] is Newtonsoft.Json.Linq.JObject props)
                    foreach (var p in props.Properties()) names.Add(p.Name);
            }
            catch { }
            return names;
        }
    }
}
