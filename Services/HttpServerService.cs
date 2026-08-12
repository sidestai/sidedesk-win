using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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

        /// <summary>AI Chat LLM 转发用 HttpClient（长超时，支持流式）</summary>
        private static readonly HttpClient _proxyHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

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

                _ = Task.Run(() => HandleRequestAsync(context));
            }
        }

        /// <summary>处理单个 HTTP 请求</summary>
        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var resp = context.Response;

                var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";
                var method = req.HttpMethod.ToUpperInvariant();

                // CORS：网页版 AI Chat（前端站点，基址见 Constants.URLs.ServerBaseUrl）跨域访问本机服务
                AddCorsHeaders(resp);
                if (method == "OPTIONS")
                {
                    resp.StatusCode = 204;
                    resp.Close();
                    return;
                }

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

                // ═══════════ AI Chat 本地代理（网页版 AI 对话：密钥不出本机） ═══════════
                if (path.StartsWith("/ai-chat"))
                {
                    await HandleAiChatAsync(context, method, path);
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

        /// <summary>AI Chat 相关端点分发</summary>
        private async Task HandleAiChatAsync(HttpListenerContext context, string method, string path)
        {
            var req = context.Request;
            var resp = context.Response;

            // GET /ai-chat/config → 本地 AI 配置（API Key 脱敏）+ 支持的模型列表
            if (method == "GET" && path == "/ai-chat/config")
            {
                var cfg = AIConfigManager.GetConfig();
                var models = new JArray();
                foreach (var m in AIConfigManager.SupportedModels)
                    models.Add(new JObject
                    {
                        ["id"] = m.Id,
                        ["name"] = m.Name,
                        ["apiBaseUrl"] = m.ApiBaseUrl,
                        ["defaultModelId"] = m.DefaultModelId
                    });

                WriteJson(resp, 200, new JObject
                {
                    ["selectedModelId"] = cfg.SelectedModelId ?? "deepseek",
                    ["customModelId"] = cfg.CustomModelId ?? "",
                    ["hasApiKey"] = !string.IsNullOrEmpty(cfg.ApiKey),
                    ["apiKeyMasked"] = MaskKey(cfg.ApiKey),
                    ["models"] = models
                }.ToString());
                return;
            }

            // POST /ai-chat/config → 保存本地 AI 配置（apiKey 为空串时保留原值）
            if (method == "POST" && path == "/ai-chat/config")
            {
                var body = await ReadBodyAsync(req);
                var cfg = AIConfigManager.GetConfig();
                try
                {
                    var json = JObject.Parse(body);
                    var selected = json.Value<string>("selectedModelId");
                    if (!string.IsNullOrEmpty(selected))
                        cfg.SelectedModelId = selected;
                    var customId = json.Value<string>("customModelId");
                    if (customId != null)
                        cfg.CustomModelId = customId.Trim();
                    var apiKey = json.Value<string>("apiKey");
                    if (!string.IsNullOrEmpty(apiKey))
                        cfg.ApiKey = apiKey.Trim();
                    AIConfigManager.SaveConfig(cfg);
                    WriteJson(resp, 200, new JObject { ["ok"] = true }.ToString());
                }
                catch (Exception ex)
                {
                    WriteJson(resp, 400, new JObject { ["ok"] = false, ["error"] = ex.Message }.ToString());
                }
                return;
            }

            // GET /ai-chat/tools → 当前生效的工具定义（注册表生效时用注册表，否则内置）
            if (method == "GET" && path == "/ai-chat/tools")
            {
                var tools = McpToolRegistryService.GetActiveToolDefinitions();
                WriteJson(resp, 200, new JObject
                {
                    ["registryActive"] = McpToolRegistryService.Instance.IsActive,
                    ["tools"] = JToken.FromObject(tools)
                }.ToString());
                return;
            }

            // POST /ai-chat/tool → 执行单个工具 {name, arguments(JSON字符串或对象)}
            if (method == "POST" && path == "/ai-chat/tool")
            {
                var body = await ReadBodyAsync(req);
                try
                {
                    var json = JObject.Parse(body);
                    var name = json.Value<string>("name") ?? "";
                    var argsToken = json["arguments"];
                    string argsStr = argsToken == null || argsToken.Type == JTokenType.Null
                        ? "{}"
                        : (argsToken.Type == JTokenType.String ? argsToken.Value<string>()! : argsToken.ToString(Newtonsoft.Json.Formatting.None));

                    string result = AIToolService.ExecuteTool(name, argsStr);
                    WriteJson(resp, 200, new JObject { ["ok"] = true, ["result"] = result }.ToString());
                }
                catch (Exception ex)
                {
                    WriteJson(resp, 200, new JObject { ["ok"] = false, ["result"] = Framework.Lang.TF("工具执行失败: {0}", ex.Message) }.ToString());
                }
                return;
            }

            // POST /ai-chat/completions → 注入本地 API Key 转发 LLM（流式透传）
            if (method == "POST" && path == "/ai-chat/completions")
            {
                await ProxyCompletionsAsync(context);
                return;
            }

            WriteJson(resp, 404, new JObject { ["error"] = "not found", ["path"] = path }.ToString());
        }

        /// <summary>转发 chat/completions：模型地址与密钥均来自本机配置，网页侧拿不到明文密钥</summary>
        private async Task ProxyCompletionsAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;

            var cfg = AIConfigManager.GetConfig();
            var modelInfo = AIConfigManager.GetSelectedModel();

            if (string.IsNullOrEmpty(modelInfo.ApiBaseUrl))
            {
                WriteJson(resp, 400, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.T("当前服务商未配置 API 地址，请改用其他服务商") } }.ToString());
                return;
            }
            if (string.IsNullOrEmpty(cfg.ApiKey))
            {
                WriteJson(resp, 400, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.T("尚未配置 API Key，请在客户端 AI 设置或本页设置中填写") } }.ToString());
                return;
            }

            string body = await ReadBodyAsync(req);
            try
            {
                var jo = JObject.Parse(body);
                if (string.IsNullOrEmpty(jo.Value<string>("model")))
                    jo["model"] = string.IsNullOrEmpty(cfg.CustomModelId) ? modelInfo.DefaultModelId : cfg.CustomModelId;
                body = jo.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { /* 非法 JSON 原样转发，由上游报错 */ }

            var url = modelInfo.ApiBaseUrl.TrimEnd('/') + "/v1/chat/completions";
            Debug.WriteLine($"[HttpServer] AI Chat 转发: POST {url}");

            HttpResponseMessage upResp;
            try
            {
                var upstream = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                upstream.Headers.Add("Authorization", "Bearer " + cfg.ApiKey);
                upResp = await _proxyHttp.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception ex)
            {
                WriteJson(resp, 502, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.TF("转发失败: {0}", ex.Message) } }.ToString());
                return;
            }

            using (upResp)
            {
                resp.StatusCode = (int)upResp.StatusCode;
                resp.ContentType = upResp.Content.Headers.ContentType?.ToString() ?? "application/json";
                resp.SendChunked = true; // 流式（SSE）响应无 Content-Length
                AddCorsHeaders(resp);

                try
                {
                    using var upStream = await upResp.Content.ReadAsStreamAsync();
                    await upStream.CopyToAsync(resp.OutputStream);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HttpServer] AI Chat 转发中断: {ex.Message}");
                }
                finally
                {
                    try { resp.OutputStream.Close(); } catch { }
                    try { resp.Close(); } catch { }
                }
            }
        }

        /// <summary>读取请求体</summary>
        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        /// <summary>API Key 脱敏显示</summary>
        private static string MaskKey(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length <= 8 ? "***" : key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
        }

        /// <summary>添加 CORS 响应头（网页版页面跨域访问本机服务）</summary>
        private static void AddCorsHeaders(HttpListenerResponse resp)
        {
            try
            {
                resp.Headers["Access-Control-Allow-Origin"] = "*";
                resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            }
            catch { }
        }

        /// <summary>写出 JSON 响应</summary>
        private static void WriteJson(HttpListenerResponse resp, int statusCode, string json)
        {
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json; charset=utf-8";
            AddCorsHeaders(resp);
            var buffer = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buffer.Length;
            resp.OutputStream.Write(buffer, 0, buffer.Length);
            resp.OutputStream.Close();
            resp.Close();
        }
    }
}
