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

        private McpServerService() { }

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
                        Debug.WriteLine($"[McpServer] 运行异常: {ex.Message}");
                        _running = false;
                    }
                });

                Debug.WriteLine($"[McpServer] 已启动，监听 http://127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                _running = false;
                Debug.WriteLine($"[McpServer] 启动失败: {ex.Message}");
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

            Debug.WriteLine("[McpServer] 已停止");
        }

        /// <summary>
        /// 构建 MCP App 实例，注册所有 Tools / Resources
        /// </summary>
        private McpApp BuildMcpApp()
        {
            var mcp = McpApp.Create("SideDesk", "1.0.0");

            // ═══════════ Tools（统一委托 AIToolService，与 AI Chat Function Calling 共用同一套逻辑） ═══════════

            mcp.AddTool("get_settings", "获取 SideDesk 当前设置概览（主题、布局、宽度等）", () => AIToolService.Tool_GetSettings());

            mcp.AddTool("set_theme", "切换 SideDesk 主题模式（Dark/Light/System）", (string themeMode) => AIToolService.Tool_SetTheme(themeMode));

            mcp.AddTool("list_windows", "列出所有小屏窗口配置（ID、名称、类型、面板数量）", () => AIToolService.Tool_ListWindows());

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

            mcp.AddResource("sidedesk://windows", "Window Configs", "所有小屏窗口配置", () =>
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

            return mcp;
        }
    }
}
