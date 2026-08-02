using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 内置轻量 HTTP Server：基于 System.Net.HttpListener 实现，无需额外依赖。
    /// 监听 http://127.0.0.1:{port}/，提供基础状态查询与通知推送接口，供外部工具/脚本调用。
    /// 默认开启，端口可在「设置 → 工具」中修改（默认 19195）。
    /// </summary>
    public sealed class HttpServerService
    {
        private static readonly Lazy<HttpServerService> _instance = new(() => new HttpServerService());
        public static HttpServerService Instance => _instance.Value;

        /// <summary>默认监听端口</summary>
        public const int DefaultPort = 19195;
        private const string Host = "127.0.0.1";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private volatile bool _running;

        /// <summary>服务是否正在运行</summary>
        public bool IsRunning => _running;
        /// <summary>当前监听端口</summary>
        public int CurrentPort { get; private set; } = DefaultPort;

        private HttpServerService() { }

        /// <summary>启动 HTTP Server（幂等；端口变化时先停后启）</summary>
        public void Start(int port = DefaultPort)
        {
            if (_running && CurrentPort == port) return;
            if (_running) Stop();

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{Host}:{port}/");
                _listener.Start();
                _cts = new CancellationTokenSource();
                CurrentPort = port;
                _running = true;
                Debug.WriteLine($"[HttpServer] 已启动，监听 http://{Host}:{port}/");
                _ = AcceptLoopAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _running = false;
                Debug.WriteLine($"[HttpServer] 启动失败: {ex.Message}");
            }
        }

        /// <summary>停止 HTTP Server</summary>
        public void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            Debug.WriteLine("[HttpServer] 已停止");
        }

        /// <summary>接受连接循环</summary>
        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
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
                    if (!_running) break;
                    Debug.WriteLine($"[HttpServer] 接受连接异常: {ex.Message}");
                    continue;
                }

                _ = Task.Run(() => HandleRequest(context));
            }
        }

        /// <summary>处理单个 HTTP 请求</summary>
        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var resp = context.Response;

                var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";
                var method = req.HttpMethod.ToUpperInvariant();

                // 请求日志：记录方法、路径、查询串与来源地址
                var query = req.Url?.Query;
                Debug.WriteLine($"[HttpServer] {method} {path}{query} <- {req.RemoteEndPoint}");

                // GET / 或 /status → 服务状态
                if (method == "GET" && (path == "/" || path == "/status"))
                {
                    var status = new JObject
                    {
                        ["name"] = "SideDesk",
                        ["service"] = "HttpServer",
                        ["port"] = CurrentPort,
                        ["running"] = true,
                        ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    WriteJson(resp, 200, status.ToString());
                    return;
                }

                // POST /notify → 推送一条通知到 SideDesk 通知栏（走真实渲染链路）
                if (method == "POST" && path == "/notify")
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        body = reader.ReadToEnd();

                    string title = "SideDesk";
                    string? text = null;
                    try
                    {
                        var json = JObject.Parse(body);
                        title = json.Value<string>("title") ?? title;
                        text = json.Value<string>("body") ?? json.Value<string>("text");
                    }
                    catch { text = body; } // 非 JSON 时整体作为正文

                    var payload = new NotificationPayload
                    {
                        Title = title,
                        Body = text,
                        Timeout = 5
                    };

                    // 通知渲染需在 UI 线程执行
                    System.Windows.Application.Current?.Dispatcher.Invoke(
                        () => NotificationService.Instance.Enqueue(payload));

                    WriteJson(resp, 200, new JObject { ["ok"] = true }.ToString());
                    return;
                }

                WriteJson(resp, 404, new JObject { ["error"] = "not found", ["path"] = path }.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpServer] 处理请求异常: {ex.Message}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        /// <summary>写出 JSON 响应</summary>
        private static void WriteJson(HttpListenerResponse resp, int statusCode, string json)
        {
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buffer.Length;
            resp.OutputStream.Write(buffer, 0, buffer.Length);
            resp.OutputStream.Close();
            resp.Close();
        }
    }
}
