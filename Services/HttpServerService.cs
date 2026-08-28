using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

                // ═══════════ 数据变量（外部程序查询/设置变量值） ═══════════
                if (path.StartsWith("/variable"))
                {
                    await HandleVariableAsync(context, method, path);
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
                    ["hasApiKey"] = !string.IsNullOrEmpty(AIConfigManager.GetApiKeyFor(cfg.SelectedModelId)),
                    ["apiKeyMasked"] = MaskKey(AIConfigManager.GetApiKeyFor(cfg.SelectedModelId)),
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
                    {
                        cfg.ApiKey = apiKey.Trim();
                        // 同步写入按服务商字典（归属当前选中服务商）
                        if (cfg.ApiKeys == null) cfg.ApiKeys = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        cfg.ApiKeys[cfg.SelectedModelId ?? "deepseek"] = apiKey.Trim();
                    }
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
                    WriteJson(resp, 200, new JObject { ["ok"] = false, ["result"] = Framework.Lang.TF("aiChat.toolExecutionFailed", ex.Message) }.ToString());
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
            string apiKey = AIConfigManager.GetApiKeyFor(modelInfo.Id);

            if (string.IsNullOrEmpty(modelInfo.ApiBaseUrl))
            {
                WriteJson(resp, 400, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.T("aiChat.providerNotConfigured") } }.ToString());
                return;
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                WriteJson(resp, 400, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.T("aiChat.apiKeyMissingHint") } }.ToString());
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
                upstream.Headers.Add("Authorization", "Bearer " + apiKey);
                upResp = await _proxyHttp.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception ex)
            {
                WriteJson(resp, 502, new JObject { ["error"] = new JObject { ["message"] = Framework.Lang.TF("system.forwardFailed", ex.Message) } }.ToString());
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

        /// <summary>数据变量相关端点分发（外部程序查询/设置 SideDesk 变量值）</summary>
        private async Task HandleVariableAsync(HttpListenerContext context, string method, string path)
        {
            var req = context.Request;
            var resp = context.Response;
            var svc = DataPollingService.Instance;

            // GET /variable/list → 全部变量当前值（含轮询项、变量集与手动值）
            if (method == "GET" && path == "/variable/list")
            {
                var arr = new JArray();
                foreach (var v in svc.GetVariableSnapshot())
                    arr.Add(new JObject
                    {
                        ["key"] = v.VarKey,
                        ["value"] = v.Value,
                        ["label"] = v.Label,
                        ["manual"] = v.Manual
                    });
                WriteJson(resp, 200, new JObject { ["ok"] = true, ["total"] = arr.Count, ["variables"] = arr }.ToString());
                return;
            }

            // GET /variable/teams → 当前登录用户可写的团队列表（外部程序据此获取 corpId）
            if (method == "GET" && path == "/variable/teams")
            {
                var teams = await TeamService.Instance.ListTeamsAsync();
                if (teams == null)
                {
                    WriteJson(resp, 401, new JObject { ["ok"] = false, ["error"] = "未登录或团队列表获取失败" }.ToString());
                    return;
                }
                var arr = new JArray();
                foreach (var t in teams)
                    arr.Add(new JObject { ["corpId"] = t.Id, ["name"] = t.Name });
                WriteJson(resp, 200, new JObject { ["ok"] = true, ["total"] = arr.Count, ["teams"] = arr }.ToString());
                return;
            }

            // GET /variable/set?key=xx&value=yy[&corpId=zz] → 设置单个变量（手动值优先，轮询不再覆盖）
            // POST /variable/set → 单个 {key, value} 或批量 {items: [{key, value}]}（兼容 varKey/varValue 字段名），顶层或单条可带 corpId
            // 已登录时一律同步服务器：不带 corpId → 个人作用域（后端 WS 推给本人其他设备）；
            // 带 corpId（团队 ID 密文，可先 GET /variable/teams 查询）→ 校验属于本人团队后提交服务端，WS 下发更新团队其他设备的值；
            // 未登录时只写本机手动值（forwarded=false）
            if (path == "/variable/set")
            {
                var pairs = new List<(string Key, string Value, string? CorpId)>();
                string? topCorpId = null;

                if (method == "GET")
                {
                    var q = ParseQuery(req.Url?.Query);
                    var key = q.TryGetValue("key", out var k) ? k : null;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        WriteJson(resp, 400, new JObject { ["ok"] = false, ["error"] = "缺少参数 key（用法: /variable/set?key=变量名&value=值[&corpId=团队ID]）" }.ToString());
                        return;
                    }
                    var value = q.TryGetValue("value", out var v) ? v : (q.TryGetValue("val", out var v2) ? v2 : "");
                    topCorpId = q.TryGetValue("corpId", out var c0) ? c0 : null;
                    pairs.Add((key!, value ?? "", null));
                }
                else if (method == "POST")
                {
                    var body = await ReadBodyAsync(req);
                    try
                    {
                        var json = JObject.Parse(body);
                        topCorpId = json.Value<string>("corpId");
                        if (json["items"] is JArray items)
                        {
                            foreach (var it in items.OfType<JObject>())
                            {
                                var key = it.Value<string>("key") ?? it.Value<string>("varKey");
                                var value = it.Value<string>("value") ?? it.Value<string>("varValue") ?? "";
                                var itemCorp = it.Value<string>("corpId"); // 单条 corpId 优先于顶层
                                if (!string.IsNullOrWhiteSpace(key)) pairs.Add((key!, value, itemCorp));
                            }
                        }
                        else
                        {
                            var key = json.Value<string>("key") ?? json.Value<string>("varKey");
                            var value = json.Value<string>("value") ?? json.Value<string>("varValue") ?? "";
                            if (!string.IsNullOrWhiteSpace(key)) pairs.Add((key!, value, null));
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteJson(resp, 400, new JObject { ["ok"] = false, ["error"] = "请求体非法 JSON: " + ex.Message }.ToString());
                        return;
                    }

                    if (pairs.Count == 0)
                    {
                        WriteJson(resp, 400, new JObject { ["ok"] = false, ["error"] = "缺少变量：支持 {key, value} 或 {items: [{key, value}]}，可带 corpId" }.ToString());
                        return;
                    }
                }
                else
                {
                    WriteJson(resp, 405, new JObject { ["ok"] = false, ["error"] = "仅支持 GET / POST" }.ToString());
                    return;
                }

                var results = new JArray();
                foreach (var (key, value, itemCorp) in pairs)
                {
                    var corpId = !string.IsNullOrWhiteSpace(itemCorp) ? itemCorp : topCorpId;
                    var found = svc.SetVariableValue(key, value, corpId); // 手动值与所属团队密文一并落本地表
                    var result = new JObject { ["key"] = key, ["value"] = value, ["found"] = found };
                    // 登录用户无论是否带 corpId 都同步服务器（个人/团队作用域）；未登录时静默跳过
                    var (fwdOk, fwdMsg) = await ForwardVariableToBackendAsync(key, value, corpId);
                    result["forwarded"] = fwdOk;
                    if (!fwdOk) result["forwardError"] = fwdMsg;
                    results.Add(result);
                    Debug.WriteLine($"[HttpServer] 设置变量 {key} = {value}（found={found}, corpId={corpId ?? "-"}, forwarded={fwdOk}）");
                }
                WriteJson(resp, 200, new JObject { ["ok"] = true, ["count"] = results.Count, ["results"] = results }.ToString());
                return;
            }

            // GET /variable/clear?key=xx[&corpId=zz] → 解除手动值，变量恢复轮询/聚合自动取值；已登录时同步服务器（带 corpId 时清除团队作用域的值）
            if (method == "GET" && path == "/variable/clear")
            {
                var q = ParseQuery(req.Url?.Query);
                var key = q.TryGetValue("key", out var k) ? k : null;
                if (string.IsNullOrWhiteSpace(key))
                {
                    WriteJson(resp, 400, new JObject { ["ok"] = false, ["error"] = "缺少参数 key（用法: /variable/clear?key=变量名[&corpId=团队ID]）" }.ToString());
                    return;
                }
                var corpId = q.TryGetValue("corpId", out var c) ? c : null;
                var removed = svc.ClearVariableValue(key!);
                var result = new JObject { ["ok"] = true, ["key"] = key, ["cleared"] = removed };
                // 后端无独立清除接口：转发 value=null 即把对应作用域的值置空并推送（本人其他设备/团队成员）
                var (fwdOk, fwdMsg) = await ForwardVariableToBackendAsync(key!, null, corpId);
                result["forwarded"] = fwdOk;
                if (!fwdOk) result["forwardError"] = fwdMsg;
                WriteJson(resp, 200, result.ToString());
                return;
            }

            WriteJson(resp, 404, new JObject { ["error"] = "not found", ["path"] = path }.ToString());
        }

        /// <summary>
        /// 以当前登录 JWT 把变量值转发到后端 /value/set（登录用户一律同步服务器；value=null 表示置空/清除）：
        /// 不带 corpId → 个人作用域，后端 WS 推给本人其他设备；带 corpId → 校验属于本人团队后提交，
        /// 团队作用域落库并 WS 下发更新团队其他设备的值。未登录/非本人团队/网络失败返回 (false, 原因)；本机手动值不受影响。
        /// </summary>
        internal static async Task<(bool ok, string msg)> ForwardVariableToBackendAsync(string key, string? value, string? corpId)
        {
            try
            {
                var app = (SideDesk.App)System.Windows.Application.Current;
                var token = app?.AppSettings?.AccountToken;
                if (string.IsNullOrEmpty(token)) return (false, "未登录，未同步到服务器");

                // 带 corpId 时需与本人所在团队的加密 ID 一致才提交服务端
                if (!string.IsNullOrWhiteSpace(corpId))
                {
                    var teams = await TeamService.Instance.ListTeamsAsync().ConfigureAwait(false);
                    if (teams == null) return (false, "团队列表获取失败，无法转发");
                    if (!teams.Any(t => string.Equals(t.Id, corpId, StringComparison.Ordinal)))
                        return (false, "corpId 与当前账号所在团队不匹配");
                }

                var body = new JObject
                {
                    ["key"] = key,
                    ["value"] = value ?? "", // 后端把空串视为 null（清除）
                };
                if (!string.IsNullOrWhiteSpace(corpId)) body["corpId"] = corpId;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Constants.URLs.ServerBaseUrl}/api/sidebar/data-variable/value/set");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _proxyHttp.SendAsync(req).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[HttpServer] 变量转发后端 {(int)resp.StatusCode}: {text}");
                var root = JObject.Parse(text);
                if ((root["code"]?.Value<int>() ?? 0) == 200) return (true, "");
                return (false, root["msg"]?.ToString() ?? "转发失败");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpServer] 变量转发后端异常: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>解析 URL 查询串（a=1&b=2，UTF-8 解码，+ 视为空格）</summary>
        private static Dictionary<string, string> ParseQuery(string? query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                if (pair.Length == 0) continue;
                var idx = pair.IndexOf('=');
                var name = idx < 0 ? pair : pair.Substring(0, idx);
                var val = idx < 0 ? "" : pair.Substring(idx + 1);
                if (name.Length == 0) continue;
                try { dict[Uri.UnescapeDataString(name.Replace("+", "%20"))] = Uri.UnescapeDataString(val.Replace("+", "%20")); }
                catch { dict[name] = val; }
            }
            return dict;
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
