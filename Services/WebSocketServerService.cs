using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// 本地 WebSocket 服务端：程序启动时监听 127.0.0.1:19191，
    /// 供浏览器扩展 / 外部程序与 SideDesk 双向通信。
    /// 基于 HttpListener + System.Net.WebSockets 实现，无需额外依赖。
    /// </summary>
    /// <summary>
    /// WebSocket 客户端元数据
    /// </summary>
    public class WsClientInfo
    {
        public Guid Id { get; set; }
        public string UniqId { get; set; } = "";     // 扩展端生成的唯一标识
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public int? TabId { get; set; }
        public DateTime ConnectedAt { get; set; } = DateTime.Now;
    }

    public sealed class WebSocketServerService
    {
        private static readonly Lazy<WebSocketServerService> _instance =
            new(() => new WebSocketServerService());
        public static WebSocketServerService Instance => _instance.Value;

        /// <summary>监听地址</summary>
        public const string Host = "127.0.0.1";
        /// <summary>监听端口</summary>
        public const int Port = 19191;

        private string Prefix => $"http://{Host}:{Port}/";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
        private readonly ConcurrentDictionary<Guid, WsClientInfo> _clientInfo = new();
        private volatile bool _running;

        /// <summary>收到文本消息时触发（参数：客户端Id, 消息文本）</summary>
        public event Action<Guid, string>? MessageReceived;
        /// <summary>客户端接入时触发</summary>
        public event Action<Guid>? ClientConnected;
        /// <summary>客户端断开时触发</summary>
        public event Action<Guid>? ClientDisconnected;

        /// <summary>服务端是否正在运行</summary>
        public bool IsRunning => _running;
        /// <summary>当前连接的客户端数量</summary>
        public int ClientCount => _clients.Count;

        private WebSocketServerService() { }

        /// <summary>
        /// 启动服务端（幂等：已运行时重复调用无副作用）
        /// </summary>
        public void Start()
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _running = true;
                Debug.WriteLine($"[WebSocketServer] started, listening on {Prefix}");
                _ = AcceptLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _running = false;
                Debug.WriteLine($"[WebSocketServer] start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止服务端并断开所有连接
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }

            foreach (var kv in _clients)
            {
                try { kv.Value.Abort(); kv.Value.Dispose(); } catch { }
            }
            _clients.Clear();

            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;

            try { _cts?.Dispose(); } catch { }
            _cts = null;

            Debug.WriteLine("[WebSocketServer] stopped");
        }

        /// <summary>
        /// 接受连接主循环
        /// </summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocketServer] accept connection error: {ex.Message}");
                    continue;
                }

                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleWebSocketAsync(context, token);
                }
                else
                {
                    // 非 WebSocket 请求：返回 400
                    try
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 处理单个 WebSocket 连接的生命周期
        /// </summary>
        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken token)
        {
            var id = Guid.NewGuid();
            WebSocket socket;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                socket = wsContext.WebSocket;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketServer] handshake failed: {ex.Message}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                return;
            }

            _clients[id] = socket;
            _clientInfo[id] = new WsClientInfo { Id = id };
            ClientConnected?.Invoke(id);
            Debug.WriteLine($"[WebSocketServer] client connected: {id} (current {_clients.Count})");

            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing",
                                CancellationToken.None).ConfigureAwait(false);
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    if (ms.Length > 0 && result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        Debug.WriteLine($"[WebSocketServer] message received [{id}]: {text}");
                        MessageReceived?.Invoke(id, text);
                    }
                }
            }
            catch (OperationCanceledException) { /* 服务停止，正常退出 */ }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketServer] connection handling error [{id}]: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(id, out _);
                _clientInfo.TryRemove(id, out _);
                try { socket.Dispose(); } catch { }
                ClientDisconnected?.Invoke(id);
                Debug.WriteLine($"[WebSocketServer] client disconnected: {id} (current {_clients.Count})");
            }
        }

        /// <summary>
        /// 向指定客户端发送文本消息
        /// </summary>
        public async Task SendAsync(Guid clientId, string message)
        {
            if (_clients.TryGetValue(clientId, out var socket) && socket.State == WebSocketState.Open)
            {
                try
                {
                    Debug.WriteLine($"[WebSocketServer] sending message [{clientId}]: {message}");
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocketServer] send failed [{clientId}]: {ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"[WebSocketServer] send skipped [{clientId}]: socket missing or not Open (current={socket?.State})");
            }
        }

        /// <summary>客户端元数据更新时触发</summary>
        public event Action? ClientInfoUpdated;

        /// <summary>
        /// 更新客户端元数据（由 register 消息触发）
        /// </summary>
        public void UpdateClientInfo(Guid clientId, string uniqId, string title, string url, int? tabId)
        {
            if (_clientInfo.TryGetValue(clientId, out var info))
            {
                info.UniqId = uniqId;
                info.Title = title;
                info.Url = url;
                info.TabId = tabId;
                ClientInfoUpdated?.Invoke();
            }
        }

        /// <summary>
        /// 获取所有已连接客户端的元数据列表
        /// </summary>
        public List<WsClientInfo> GetConnectedClients()
        {
            return _clientInfo.Values.ToList();
        }

        /// <summary>
        /// 向所有已连接客户端广播文本消息
        /// </summary>
        public async Task BroadcastAsync(string message)
        {
            Debug.WriteLine($"[WebSocketServer] broadcasting, current client count={_clients.Count}");
            var bytes = Encoding.UTF8.GetBytes(message);
            foreach (var kv in _clients)
            {
                if (kv.Value.State != WebSocketState.Open)
                {
                    Debug.WriteLine($"[WebSocketServer] broadcast skipped client [{kv.Key}]: state={kv.Value.State}");
                    continue;
                }
                try
                {
                    Debug.WriteLine($"[WebSocketServer] broadcast to client [{kv.Key}]: {message}");
                    await kv.Value.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocketServer] broadcast failed [{kv.Key}]: {ex.Message}");
                }
            }
        }
    }
}
