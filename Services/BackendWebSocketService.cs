using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// 后端 WebSocket 客户端服务：与后端服务器 (ws://localhost:19190) 建立长连接。
    /// 已登录时调用 Start(token) 以用户身份连接；未登录时调用 Start(null) 以访客（设备）身份连接。
    /// 连接 URL 始终携带 deviceId（设备指纹），供后端“按设备推送”与“给未登录用户广播”使用。
    /// 支持自动重连（指数退避）、心跳保活（30s ping）、断线检测。
    /// </summary>
    public sealed class BackendWebSocketService
    {
        private static readonly Lazy<BackendWebSocketService> _instance =
            new(() => new BackendWebSocketService());
        public static BackendWebSocketService Instance => _instance.Value;

        /// <summary>后端 WebSocket 服务地址</summary>
        private const string WsHost = "127.0.0.1";
        private const int WsPort = 19190;

        /// <summary>心跳间隔（毫秒）</summary>
        private const int HeartbeatIntervalMs = 30_000;
        /// <summary>最大重连次数</summary>
        private const int MaxReconnectAttempts = 10;
        /// <summary>基础重连延迟（毫秒）</summary>
        private const int BaseReconnectDelayMs = 3_000;

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private string? _token;
        private volatile bool _running;
        private volatile bool _connected;
        private int _reconnectAttempts;
        private Timer? _heartbeatTimer;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>连接成功时触发</summary>
        public event Action? Connected;
        /// <summary>连接断开时触发</summary>
        public event Action? Disconnected;
        /// <summary>收到文本消息时触发（参数：消息文本）</summary>
        public event Action<string>? MessageReceived;

        /// <summary>是否已连接到后端</summary>
        public bool IsConnected => _connected;
        /// <summary>服务是否处于运行状态（含重连等待期间）</summary>
        public bool IsRunning => _running;

        private BackendWebSocketService() { }

        /// <summary>
        /// 启动 WebSocket 连接（幂等：身份未变时跳过；身份变化时重连）。
        /// token 为空表示以访客（未登录设备）身份连接，仅凭 deviceId 标识。
        /// </summary>
        public void Start(string? token)
        {
            // 空 token 规范化为 null（访客模式）
            if (string.IsNullOrEmpty(token)) token = null;

            // 身份变更（访客↔已登录，或切换账号）→ 先停止旧连接再重新连接
            if (_running && _token != token)
            {
                Debug.WriteLine("[BackendWS] 连接身份已变更，重新建立连接");
                StopInternal();
            }
            else if (_running)
            {
                Debug.WriteLine("[BackendWS] 已在运行中，跳过重复启动");
                return;
            }

            _token = token;
            _running = true;
            _reconnectAttempts = 0;
            _cts = new CancellationTokenSource();
            Debug.WriteLine(_token == null ? "[BackendWS] 服务启动（访客模式），准备连接后端 WebSocket" : "[BackendWS] 服务启动（已登录模式），准备连接后端 WebSocket");
            _ = ConnectLoopAsync(_cts.Token);
        }

        /// <summary>
        /// 停止 WebSocket 连接（主动停止后不再自动重连）
        /// </summary>
        public void Stop()
        {
            if (!_running) return;
            Debug.WriteLine("[BackendWS] 正在停止...");
            StopInternal();
            Debug.WriteLine("[BackendWS] 已停止");
        }

        private void StopInternal()
        {
            _running = false;
            _connected = false;
            try { _cts?.Cancel(); } catch { }
            StopHeartbeat();

            try
            {
                if (_socket != null && _socket.State == WebSocketState.Open)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stop",
                        CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch { }

            try { _socket?.Dispose(); } catch { }
            _socket = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        /// <summary>
        /// 连接主循环：连接 → 接收消息 → 断线后自动重连
        /// </summary>
        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _running)
            {
                try
                {
                    // 先用 TCP 探测端口，避免后端未启动时反复抛出 WebSocketException。
                    // 使用带取消令牌的 ConnectAsync：await 的就是最终失败的那个 Task，
                    // 不能像旧实现那样 WhenAny 超时后丢弃 probeTask —— 被丢弃的探测任务
                    // 在 TcpClient Dispose 时会以 OperationCanceledException("已中止 I/O 操作") 失败，
                    // 无人观察导致 UnobservedTaskException 反复写入崩溃日志。
                    using (var probe = new System.Net.Sockets.TcpClient())
                    using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        probeCts.CancelAfter(2000);
                        try
                        {
                            await probe.ConnectAsync(WsHost, WsPort, probeCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new TimeoutException($"端口 {WsPort} 探测超时");
                        }
                    }

                    _socket?.Dispose();
                    _socket = new ClientWebSocket();

                    // 连接 URL 始终携带 deviceId（设备指纹）；已登录时额外携带 token
                    var deviceId = DeviceIdService.DeviceId;
                    var query = string.IsNullOrEmpty(_token)
                        ? $"?deviceId={Uri.EscapeDataString(deviceId)}"
                        : $"?token={Uri.EscapeDataString(_token)}&deviceId={Uri.EscapeDataString(deviceId)}";
                    var uri = new Uri($"ws://{WsHost}:{WsPort}/{query}");
                    Debug.WriteLine($"[BackendWS] 正在连接 {WsHost}:{WsPort} ({(string.IsNullOrEmpty(_token) ? "访客" : "已登录")}) ...");

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10)); // 连接超时 10 秒

                    await _socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);

                    // 连接成功
                    _connected = true;
                    _reconnectAttempts = 0;
                    Debug.WriteLine("[BackendWS] 连接成功");
                    Connected?.Invoke();
                    StartHeartbeat(token);

                    // 接收消息循环
                    await ReceiveLoopAsync(_socket, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break; // 主动停止
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackendWS] 连接异常: {ex.Message}");
                }

                // 连接断开
                if (_connected)
                {
                    _connected = false;
                    Disconnected?.Invoke();
                }
                StopHeartbeat();

                if (!token.IsCancellationRequested && _running)
                {
                    // 指数退避重连
                    _reconnectAttempts++;
                    if (_reconnectAttempts > MaxReconnectAttempts)
                    {
                        Debug.WriteLine($"[BackendWS] 已达最大重连次数({MaxReconnectAttempts})，停止重连");
                        _running = false;
                        break;
                    }

                    int delay = Math.Min(BaseReconnectDelayMs * (1 << Math.Min(_reconnectAttempts - 1, 5)), 60_000);
                    Debug.WriteLine($"[BackendWS] {delay / 1000}秒后尝试第{_reconnectAttempts}次重连...");
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>
        /// 接收消息循环
        /// </summary>
        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.WriteLine($"[BackendWS] 服务端关闭连接: {result.CloseStatusDescription}");
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (ms.Length > 0 && result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    Debug.WriteLine($"[BackendWS] 收到消息: {text}");
                    try { MessageReceived?.Invoke(text); } catch { }
                }
            }
        }

        /// <summary>
        /// 发送文本消息到后端
        /// </summary>
        public async Task SendAsync(string message)
        {
            if (_socket == null || _socket.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await _socket.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackendWS] 发送失败: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        #region 心跳

        private void StartHeartbeat(CancellationToken token)
        {
            StopHeartbeat();
            _heartbeatTimer = new Timer(async _ =>
            {
                if (!_connected || token.IsCancellationRequested) return;
                try
                {
                    await SendAsync("{\"event\":\"ping\"}").ConfigureAwait(false);
                }
                catch { }
            }, null, HeartbeatIntervalMs, HeartbeatIntervalMs);
        }

        private void StopHeartbeat()
        {
            try { _heartbeatTimer?.Dispose(); } catch { }
            _heartbeatTimer = null;
        }

        #endregion
    }
}
