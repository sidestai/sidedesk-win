using System;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace SideDesk
{
    public partial class LoginWindow : Window
    {
        // 客户端专用登录页（无滑动验证，前端 hash 路由）
        private static readonly string LoginUrl = Constants.URLs.FrontendBaseUrl + "/#/client-login";
        // 需要拦截的登录接口路径（匹配请求 URL 中包含该片段的响应）
        // 登录接口：POST /api/auth/login，响应 { code:200, msg, data:{ token } }
        private const string TargetApiPath = "/api/auth/login";

        /// <summary>最近一次成功拦截到的登录接口 token（供外部读取）</summary>
        public static string? LastToken { get; private set; }

        /// <summary>最近一次登录解析到的用户名称（尽力提取，可能为空）</summary>
        public static string? LastUserName { get; private set; }

        private string? _targetRequestId;
        private bool _tokenHandled;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
        }

        private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 初始化 WebView2
                await webView.EnsureCoreWebView2Async(null);
                var core = webView.CoreWebView2;

                // 启用 CDP Network 域，并在导航前挂好监听，确保能捕获登录接口
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

                core.GetDevToolsProtocolEventReceiver("Network.responseReceived")
                    .DevToolsProtocolEventReceived += OnResponseReceived;
                core.GetDevToolsProtocolEventReceiver("Network.loadingFinished")
                    .DevToolsProtocolEventReceived += OnLoadingFinished;

                // 导航到登录页面
                core.Navigate(LoginUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("加载登录页面失败：{0}", ex.Message), Framework.Lang.T("错误"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>响应到达：记录目标登录接口的 requestId</summary>
        private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("url", out var urlEl))
                {
                    string url = urlEl.GetString() ?? "";
                    if (url.Contains(TargetApiPath, StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("requestId", out var reqEl))
                    {
                        _targetRequestId = reqEl.GetString();
                    }
                }
            }
            catch { }
        }

        /// <summary>响应体加载完成：拉取目标接口响应体并提取 token</summary>
        private async void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            if (_tokenHandled || string.IsNullOrEmpty(_targetRequestId)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (!doc.RootElement.TryGetProperty("requestId", out var reqEl)) return;
                if (reqEl.GetString() != _targetRequestId) return;

                var core = webView.CoreWebView2;
                string param = JsonSerializer.Serialize(new { requestId = _targetRequestId });
                string result = await core.CallDevToolsProtocolMethodAsync("Network.getResponseBody", param);

                using var resultDoc = JsonDocument.Parse(result);
                var resultRoot = resultDoc.RootElement;
                string body = resultRoot.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";
                bool base64 = resultRoot.TryGetProperty("base64Encoded", out var b64El) && b64El.GetBoolean();
                if (base64 && !string.IsNullOrEmpty(body))
                {
                    var bytes = Convert.FromBase64String(body);
                    body = Encoding.UTF8.GetString(bytes);
                }

                _tokenHandled = true;
                string? token = ExtractToken(body);

                if (!string.IsNullOrEmpty(token))
                {
                    LastToken = token;
                    LastUserName = ExtractUserName(body);
                    // 登录成功：关闭窗口并返回成功（由调用方读取 LastToken 处理后续）
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try { DialogResult = true; } catch { }
                            Close();
                        });
                    }
                    catch { }
                }
                else
                {
                    MessageBox.Show(Framework.Lang.TF("已拦截到登录接口响应，但未自动识别到 token 字段。\n原始响应如下：\n\n{0}", body),
                        Framework.Lang.T("接口响应"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Login] 拦截 token 失败: {ex.Message}");
            }
        }

        /// <summary>从登录接口响应 JSON 中尽力提取 token（兼容常见字段与嵌套结构）</summary>
        private static string? ExtractToken(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return FindToken(doc.RootElement);
            }
            catch { return null; }
        }

        /// <summary>从登录接口响应 JSON 中尽力提取用户名称（兼容常见字段与嵌套结构）</summary>
        private static string? ExtractUserName(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return FindUserName(doc.RootElement);
            }
            catch { return null; }
        }

        private static string? FindUserName(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name.ToLowerInvariant();
                    if ((name == "nickname" || name == "nick_name" || name == "username"
                         || name == "user_name" || name == "realname" || name == "real_name"
                         || name == "name")
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
                foreach (var prop in el.EnumerateObject())
                {
                    var found = FindUserName(prop.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindUserName(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return null;
        }

        private static string? FindToken(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // 先在当前层查找常见 token 字段
                foreach (var prop in el.EnumerateObject())
                {
                    string name = prop.Name.ToLowerInvariant();
                    if ((name == "token" || name == "access_token" || name == "accesstoken")
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        return prop.Value.GetString();
                    }
                }
                // 再递归子节点
                foreach (var prop in el.EnumerateObject())
                {
                    var found = FindToken(prop.Value);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindToken(item);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            return null;
        }
    }
}
