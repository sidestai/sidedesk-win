using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// 桌面客户端「系统浏览器登录」：OAuth2 授权码 + PKCE(S256) + Loopback 回环（RFC 8252）。
    /// 流程：
    ///   1) 生成 code_verifier / code_challenge(S256) / state；
    ///   2) 起临时 <see cref="HttpListener"/> 监听 http://127.0.0.1:{随机端口}/callback；
    ///   3) <see cref="Process.Start(string)"/>(UseShellExecute) 用系统默认浏览器打开后端授权入口
    ///      {ServerBaseUrl}/api/oauth/authorize?...（后端校验后 302 到前端授权页，用户登录/授权）；
    ///   4) 前端回跳 loopback /callback?code&amp;state → 校验 state → 返回「可关闭此页」HTML；
    ///   5) POST {ServerBaseUrl}/api/oauth/token（授权码 + code_verifier）换取 access_token。
    /// 任一环节失败/超时/取消均返回 null，由调用方（App.StartLoginAsync）降级到 WebView2 内嵌登录窗。
    /// </summary>
    public static class OAuthLoginService
    {
        /// <summary>整个浏览器登录流程的等待上限（用户需在浏览器完成登录 + 授权，留足时间）</summary>
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(180);

        /// <summary>令牌交换请求超时（换取 access_token 是快速接口）</summary>
        private static readonly TimeSpan TokenHttpTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 发起系统浏览器 OAuth2 登录。成功返回 access_token；失败 / 超时 / 取消返回 null（调用方据此降级兜底）。
        /// </summary>
        public static async Task<string?> LoginAsync(CancellationToken cancellationToken = default)
        {
            HttpListener? listener = null;
            try
            {
                // 1) PKCE + state
                string codeVerifier = GenerateCodeVerifier();
                string codeChallenge = GenerateCodeChallenge(codeVerifier);
                string state = GenerateState();

                // 2) 起 loopback 监听（随机可用端口；绑定失败重试若干次）
                listener = StartLoopbackListener(out int port);
                if (listener == null)
                {
                    Debug.WriteLine("[OAuth] no loopback port available, abort browser login");
                    return null;
                }
                string redirectUri = $"http://127.0.0.1:{port}/callback";

                // 3) 系统默认浏览器打开后端授权入口
                string authorizeUrl = BuildAuthorizeUrl(redirectUri, codeChallenge, state);
                if (!TryOpenBrowser(authorizeUrl))
                {
                    Debug.WriteLine("[OAuth] failed to launch system browser");
                    return null;
                }

                // 4) 等待回环回调（带整体超时；GetContextAsync 不可取消，用信号任务竞速实现取消）
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(LoginTimeout);

                string? code;
                try
                {
                    code = await WaitForCallbackAsync(listener, state, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[OAuth] login timed out or cancelled");
                    return null;
                }
                if (string.IsNullOrEmpty(code)) return null;

                // 5) 授权码 + PKCE code_verifier 换 access_token（不再受整体超时约束，用 HttpClient 自身超时兜底）
                return await ExchangeCodeForTokenAsync(code!, codeVerifier, redirectUri).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OAuth] browser login error: {ex.Message}");
                return null;
            }
            finally
            {
                try { listener?.Stop(); } catch { }
                try { listener?.Close(); } catch { }
            }
        }

        // ══════════════════════ PKCE / state 生成 ══════════════════════

        /// <summary>code_verifier：base64url(随机 32 字节)，长度 43，符合 RFC 7636 的 [43,128] 约束</summary>
        private static string GenerateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        /// <summary>code_challenge：base64url(SHA256(ASCII(code_verifier)))，S256 方法</summary>
        private static string GenerateCodeChallenge(string verifier)
            => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        /// <summary>state：base64url(随机 16 字节)，防 CSRF（回调需与发起值一致）</summary>
        private static string GenerateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        /// <summary>base64url 编码（去掉填充 '='，'+'→'-'，'/'→'_'），与后端 PKCE 校验一致</summary>
        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ══════════════════════ Loopback 监听 ══════════════════════

        /// <summary>绑定一个 127.0.0.1 随机可用端口并启动 HttpListener；连续失败返回 null（触发降级）</summary>
        private static HttpListener? StartLoopbackListener(out int port)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int p = FindFreeLoopbackPort();
                var listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                    listener.Start();
                    port = p;
                    Debug.WriteLine($"[OAuth] loopback listener started on http://127.0.0.1:{p}/callback");
                    return listener;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OAuth] bind 127.0.0.1:{p} failed (attempt {attempt + 1}/5): {ex.Message}");
                    try { listener.Close(); } catch { }
                }
            }
            port = 0;
            return null;
        }

        /// <summary>用 TcpListener 探测一个空闲的 loopback 端口（端口 0 由系统分配）</summary>
        private static int FindFreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // ══════════════════════ 授权入口 URL / 打开浏览器 ══════════════════════

        /// <summary>拼接后端授权入口 URL（走 /api 前缀，由 nginx/Vite 反代到后端 /oauth/authorize）</summary>
        private static string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
        {
            var q = new StringBuilder("response_type=code");
            q.Append("&client_id=").Append(Uri.EscapeDataString(Constants.URLs.OAuthClientId));
            q.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            q.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            q.Append("&code_challenge_method=S256");
            q.Append("&state=").Append(Uri.EscapeDataString(state));
            return $"{Constants.URLs.ServerBaseUrl}/api/oauth/authorize?{q}";
        }

        /// <summary>用系统默认浏览器打开 URL（复用 LoginWindow.OpenRegisterInBrowser 的 UseShellExecute 模式）</summary>
        private static bool TryOpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OAuth] Process.Start(browser) failed: {ex.Message}");
                return false;
            }
        }

        // ══════════════════════ 等待回调 ══════════════════════

        /// <summary>
        /// 等待浏览器回跳 /callback：校验 state（防 CSRF）后返回授权码 code，并回「可关闭此页」HTML。
        /// 非回调路径（如 /favicon.ico）404 后继续等待；state 不匹配 / 缺 code / error → 返回 null；超时由 token 取消触发。
        /// </summary>
        private static async Task<string?> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken token)
        {
            // GetContextAsync 无法直接取消：用一个「随 token 取消而完成」的信号任务与之竞速
            var cancelSignal = Task.Delay(Timeout.Infinite, token);
            while (true)
            {
                var getCtx = listener.GetContextAsync();
                var completed = await Task.WhenAny(getCtx, cancelSignal).ConfigureAwait(false);
                if (completed == cancelSignal)
                {
                    // 超时/取消：await 抛出 OperationCanceledException，交由调用方转为「登录超时 → 降级」
                    await cancelSignal.ConfigureAwait(false);
                    return null;
                }

                HttpListenerContext ctx;
                try { ctx = await getCtx.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // 监听被停止等致命错误：结束等待
                    Debug.WriteLine($"[OAuth] listener accept error: {ex.Message}");
                    return null;
                }

                try
                {
                    var req = ctx.Request;
                    string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
                    if (path.Length == 0) path = "/";

                    if (path != "/callback")
                    {
                        WriteHtml(ctx.Response, 404, BuildCallbackHtml(false, "无效的回调路径。"));
                        continue;
                    }

                    var query = ParseQuery(req.Url?.Query);

                    if (query.TryGetValue("error", out var err) && !string.IsNullOrEmpty(err))
                    {
                        WriteHtml(ctx.Response, 400, BuildCallbackHtml(false, $"授权被拒绝：{err}"));
                        return null;
                    }

                    query.TryGetValue("code", out var code);
                    query.TryGetValue("state", out var recvState);

                    if (string.IsNullOrEmpty(code) || !string.Equals(recvState, expectedState, StringComparison.Ordinal))
                    {
                        Debug.WriteLine($"[OAuth] callback rejected: state mismatch or missing code (state={recvState})");
                        WriteHtml(ctx.Response, 400, BuildCallbackHtml(false, "登录状态校验失败（state 不匹配或缺少授权码），请返回客户端重试。"));
                        return null;
                    }

                    WriteHtml(ctx.Response, 200, BuildCallbackHtml(true, null));
                    return code;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OAuth] callback handling error: {ex.Message}");
                    return null;
                }
            }
        }

        // ══════════════════════ 令牌交换 ══════════════════════

        /// <summary>POST /api/oauth/token：授权码 + code_verifier 换 access_token；失败返回 null</summary>
        private static async Task<string?> ExchangeCodeForTokenAsync(string code, string codeVerifier, string redirectUri)
        {
            try
            {
                using var http = new HttpClient { Timeout = TokenHttpTimeout };
                // 字段名须与后端 OAuthTokenDto 完全一致（全局 ValidationPipe whitelist 会剔除未知字段）
                var payload = new
                {
                    grant_type = "authorization_code",
                    code,
                    code_verifier = codeVerifier,
                    redirect_uri = redirectUri,
                    client_id = Constants.URLs.OAuthClientId,
                };
                string json = JsonSerializer.Serialize(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Constants.URLs.ServerBaseUrl}/api/oauth/token")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                // 后端用 user-agent 构造 clientInfo，显式带上避免解析空值
                req.Headers.TryAddWithoutValidation("User-Agent", "SideDesk-Desktop/1.0 (OAuth2 PKCE loopback)");

                using var resp = await HttpLog.SendAsync(http, req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[OAuth] token exchange failed {(int)resp.StatusCode}: {body}");
                    return null;
                }
                string? accessToken = ExtractAccessToken(body);
                if (string.IsNullOrEmpty(accessToken))
                    Debug.WriteLine($"[OAuth] token response has no access_token: {body}");
                return accessToken;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OAuth] token exchange error: {ex.Message}");
                return null;
            }
        }

        /// <summary>从令牌响应中取 access_token：兼容标准 OAuth2（顶层）与 ResultData 包裹（data.access_token）两种形态</summary>
        private static string? ExtractAccessToken(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("access_token", out var t2) && t2.ValueKind == JsonValueKind.String)
                    return t2.GetString();
                return null;
            }
            catch { return null; }
        }

        // ══════════════════════ 工具方法 ══════════════════════

        /// <summary>解析 URL 查询串（a=1&amp;b=2，UTF-8 解码，'+' 视为空格）</summary>
        private static Dictionary<string, string> ParseQuery(string? query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var pair in query!.TrimStart('?').Split('&'))
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

        /// <summary>写出 HTML 响应</summary>
        private static void WriteHtml(HttpListenerResponse resp, int statusCode, string html)
        {
            try
            {
                resp.StatusCode = statusCode;
                resp.ContentType = "text/html; charset=utf-8";
                var buffer = Encoding.UTF8.GetBytes(html);
                resp.ContentLength64 = buffer.Length;
                resp.OutputStream.Write(buffer, 0, buffer.Length);
                resp.OutputStream.Close();
                resp.Close();
            }
            catch { }
        }

        private const string IconSuccess = @"<svg viewBox=""0 0 24 24"" fill=""none"" xmlns=""http://www.w3.org/2000/svg""><circle cx=""12"" cy=""12"" r=""11"" fill=""#22c55e""/><path d=""M7 12.5l3.2 3.2L17 9"" stroke=""#fff"" stroke-width=""2.2"" stroke-linecap=""round"" stroke-linejoin=""round""/></svg>";
        private const string IconError = @"<svg viewBox=""0 0 24 24"" fill=""none"" xmlns=""http://www.w3.org/2000/svg""><circle cx=""12"" cy=""12"" r=""11"" fill=""#ef4444""/><path d=""M8.5 8.5l7 7M15.5 8.5l-7 7"" stroke=""#fff"" stroke-width=""2.2"" stroke-linecap=""round""/></svg>";

        private const string CallbackHtmlTemplate = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
<title>SideDesk</title>
<style>
  html, body { height: 100%; margin: 0; }
  body { display: flex; align-items: center; justify-content: center;
         font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Microsoft YaHei', sans-serif;
         background: #f5f6f8; color: #1f2329; }
  .card { text-align: center; padding: 48px 36px; max-width: 460px; }
  .icon { width: 68px; height: 68px; margin: 0 auto 22px; }
  h1 { font-size: 20px; font-weight: 600; margin: 0 0 12px; }
  p { font-size: 14px; line-height: 1.7; color: #646a73; margin: 0; }
</style>
</head>
<body>
  <div class=""card"">
    <div class=""icon"">{ICON}</div>
    <h1 style=""color:{COLOR}"">{TITLE}</h1>
    <p>{DESC}</p>
  </div>
</body>
</html>";

        /// <summary>构造回环回调页 HTML（成功/失败双语提示，成功引导关闭页面返回客户端）</summary>
        private static string BuildCallbackHtml(bool success, string? message)
        {
            string title = success ? "登录成功 · Signed in" : "登录失败 · Sign-in failed";
            string color = success ? "#16a34a" : "#dc2626";
            string desc = success
                ? "你已登录 SideDesk，可以关闭此页面并返回桌面客户端。<br>You are signed in to SideDesk. You can close this page and return to the app."
                : WebUtility.HtmlEncode(message ?? "") +
                  "<br><br>请返回 SideDesk 重试（将自动使用客户端内登录窗口）。<br>Please return to SideDesk and try again.";
            return CallbackHtmlTemplate
                .Replace("{ICON}", success ? IconSuccess : IconError)
                .Replace("{COLOR}", color)
                .Replace("{TITLE}", title)
                .Replace("{DESC}", desc);
        }
    }
}
