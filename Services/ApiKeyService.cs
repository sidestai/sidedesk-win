using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    /// <summary>API 密钥信息（列表展示用：不含完整 Key，后端只返回前缀）</summary>
    public class ApiKeyInfo
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("prefix")] public string Prefix { get; set; } = "";
        [JsonProperty("corpId")] public int? CorpId { get; set; }
        /// <summary>团队 ID 密文（encrypt2）：外部程序调接口时传的就是它</summary>
        [JsonProperty("corpCode")] public string? CorpCode { get; set; }
        [JsonProperty("corpName")] public string? CorpName { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; } = "";
        [JsonProperty("status")] public int Status { get; set; }
        [JsonProperty("expiresAt")] public DateTime? ExpiresAt { get; set; }
        [JsonProperty("lastUsedAt")] public DateTime? LastUsedAt { get; set; }
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    /// <summary>
    /// API 密钥服务：对接后端密钥管理接口（创建/列表/删除），供设置窗开发者页使用。
    /// 鉴权与响应格式与团队/好友服务一致（登录 JWT + { code, msg, data }）。
    /// </summary>
    public sealed class ApiKeyService
    {
        private static readonly Lazy<ApiKeyService> _lazy = new(() => new ApiKeyService());
        public static ApiKeyService Instance => _lazy.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private ApiKeyService() { }

        private static string GetToken()
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            return app.AppSettings.AccountToken ?? "";
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + GetToken());
            return req;
        }

        /// <summary>解析通用响应 { code: 200, msg, data }：成功返回 data，失败返回 (null, 错误消息)</summary>
        private static async Task<(JToken? data, string err)> ParseAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[ApiKey] {(int)resp.StatusCode}: {json}");
            try
            {
                var root = JObject.Parse(json);
                int bizCode = root["code"]?.Value<int>() ?? 0;
                if (bizCode == 200) return (root["data"], "");
                return (null, root["msg"]?.ToString() ?? Framework.Lang.T("settings.apiKey.requestFailed"));
            }
            catch
            {
                return (null, Framework.Lang.T("settings.apiKey.responseParseFailed"));
            }
        }

        /// <summary>GET /api/sidebar/api-key/list → 我的密钥列表；失败返回 null</summary>
        public async Task<List<ApiKeyInfo>?> ListAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/sidebar/api-key/list");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (data, _) = await ParseAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<ApiKeyInfo>>() ?? new List<ApiKeyInfo>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiKey] 获取列表失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>POST /api/sidebar/api-key/create → 创建密钥（完整 Key 仅此一次返回；corpId 空=个人）。
        /// 注意：客户端侧团队 ID 是 encrypt2 密文字符串，需原样传给后端，由后端 parseCorpId（decrypt2）反解入库。</summary>
        public async Task<(bool ok, string msg, string key)> CreateAsync(string name, string? corpId)
        {
            try
            {
                var body = new JObject { ["name"] = name };
                if (!string.IsNullOrEmpty(corpId)) body["corpId"] = corpId;
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/api-key/create");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (data, err) = await ParseAsync(resp).ConfigureAwait(false);
                if (data == null) return (false, err, "");
                return (true, "", data.Value<string>("key") ?? "");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiKey] 创建失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"), "");
            }
        }

        /// <summary>POST /api/sidebar/api-key/delete → 删除密钥（立即失效）</summary>
        public async Task<(bool ok, string msg)> DeleteAsync(int id)
        {
            try
            {
                var body = new JObject { ["id"] = id };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/api-key/delete");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (data, err) = await ParseAsync(resp).ConfigureAwait(false);
                if (err != "") return (false, err);
                return (true, "");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiKey] 删除失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }
    }
}
