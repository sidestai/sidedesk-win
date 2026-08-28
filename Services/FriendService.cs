using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    #region API 数据模型

    /// <summary>好友用户信息</summary>
    public class FriendUser
    {
        // 后端加密串（encrypt2 确定性密文，不暴露真实数字 ID）；本地存储/匹配与回传均用此串
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("nickName")] public string NickName { get; set; } = "";
        [JsonProperty("userName")] public string UserName { get; set; } = "";
        [JsonProperty("avatar")] public string Avatar { get; set; } = "";
        [JsonProperty("isOnline")] public int IsOnline { get; set; }
        [JsonProperty("lastOnlineTime")] public DateTime? LastOnlineTime { get; set; }
    }

    /// <summary>好友请求</summary>
    public class FriendRequest
    {
        // 请求记录加密 ID（encrypt2 串），处理时原样回传后端
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonProperty("fromUser")] public FriendUser? FromUser { get; set; }
    }

    #endregion

    /// <summary>
    /// 好友服务：对接后端好友接口（基址见 Constants.URLs.ServerBaseUrl，与团队服务同一鉴权与响应格式）。
    /// </summary>
    public sealed class FriendService
    {
        private static readonly Lazy<FriendService> _lazy = new(() => new FriendService());
        public static FriendService Instance => _lazy.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private FriendService() { }

        /// <summary>最近一次请求是否因认证失败（401/403）</summary>
        public bool LastAuthFailed { get; private set; } = false;

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

        /// <summary>解析通用响应 { code: 200, msg, data }，返回 data 的 JToken；失败返回 null</summary>
        private static async Task<JToken?> ParseResponseTokenAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Friend] {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    Instance.LastAuthFailed = true;
                return null;
            }

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;

            if (bizCode == 401 || bizCode == 403)
            {
                Instance.LastAuthFailed = true;
                return null;
            }
            Instance.LastAuthFailed = false;

            if (bizCode != 200) return null;
            return root["data"];
        }

        /// <summary>解析响应并返回业务错误消息（用于 add/handle/remove 等操作）</summary>
        private static async Task<(bool ok, string msg)> ParseResultAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Friend] {(int)resp.StatusCode}: {json}");

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            string msg = root["msg"]?.Value<string>() ?? "";

            if (bizCode == 401 || bizCode == 403)
                Instance.LastAuthFailed = true;

            return (bizCode == 200, msg);
        }

        #region 1. 搜索用户

        /// <summary>GET /api/friend/search?keyword=xxx</summary>
        public async Task<List<FriendUser>?> SearchUserAsync(string keyword)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/friend/search?keyword={Uri.EscapeDataString(keyword)}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<FriendUser>>() ?? new List<FriendUser>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 搜索用户失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 2. 发送好友请求

        /// <summary>POST /api/friend/add — Body: { friendUserId(加密串) }</summary>
        public async Task<(bool ok, string msg)> AddFriendAsync(string friendUserId)
        {
            try
            {
                var body = new JObject { ["friendUserId"] = friendUserId };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/friend/add");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseResultAsync(resp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 发送好友请求失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 3. 好友请求列表

        /// <summary>GET /api/friend/requests</summary>
        public async Task<List<FriendRequest>?> GetRequestsAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/friend/requests");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<FriendRequest>>() ?? new List<FriendRequest>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 获取好友请求失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 4. 处理好友请求

        /// <summary>POST /api/friend/handle — Body: { requestId(加密串), action }</summary>
        public async Task<(bool ok, string msg)> HandleRequestAsync(string requestId, string action)
        {
            try
            {
                var body = new JObject { ["requestId"] = requestId, ["action"] = action };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/friend/handle");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseResultAsync(resp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 处理好友请求失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 5. 好友列表

        /// <summary>GET /api/friend/list</summary>
        public async Task<List<FriendUser>?> GetFriendListAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/friend/list");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<FriendUser>>() ?? new List<FriendUser>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 获取好友列表失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 6. 删除好友

        /// <summary>DELETE /api/friend/remove — Body: { friendUserId(加密串) }</summary>
        public async Task<(bool ok, string msg)> RemoveFriendAsync(string friendUserId)
        {
            try
            {
                var body = new JObject { ["friendUserId"] = friendUserId };
                using var req = CreateRequest(HttpMethod.Delete, $"{BaseUrl}/api/friend/remove");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseResultAsync(resp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 删除好友失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 7. 我的好友码

        /// <summary>GET /api/friend/myCode，返回（好友码, 是否允许被搜索）</summary>
        public async Task<(string? code, bool allowSearch)> GetMyCodeAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/friend/myCode");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                var code = data?["friendCode"]?.Value<string>();
                var allow = (data?["allowFriendSearch"]?.Value<int>() ?? 1) == 1;
                return (code, allow);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 获取好友码失败: {ex.Message}");
                return (null, true);
            }
        }

        #endregion

        #region 8. 更换好友码

        /// <summary>POST /api/friend/refreshCode</summary>
        public async Task<string?> RefreshCodeAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/friend/refreshCode");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                return data?["friendCode"]?.Value<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 更换好友码失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 9. 设置是否允许被搜索

        /// <summary>POST /api/friend/setSearchable</summary>
        public async Task<(bool ok, string msg)> SetSearchableAsync(bool allow)
        {
            try
            {
                var body = new JObject { ["allow"] = allow ? 1 : 0 };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/friend/setSearchable");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseResultAsync(resp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 设置可搜索状态失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 10. 修改本人资料（昵称/头像）

        /// <summary>PUT /api/friend/profile — Body: { nickName?, avatar? }，无权限要求，变更后后端实时通知好友</summary>
        public async Task<(bool ok, string msg)> UpdateMyProfileAsync(string? nickName, string? avatar)
        {
            try
            {
                var body = new JObject();
                if (nickName != null) body["nickName"] = nickName;
                if (avatar != null) body["avatar"] = avatar;
                using var req = CreateRequest(HttpMethod.Put, $"{BaseUrl}/api/friend/profile");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return await ParseResultAsync(resp).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 修改本人资料失败: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 11. 头像缓存直传（OSS 缓存区）

        /// <summary>云盘缓存配置（后端 ossCache 配置下发）</summary>
        private sealed class OssCacheConfig
        {
            [JsonProperty("bucket")] public string Bucket { get; set; } = "";
            [JsonProperty("region")] public string Region { get; set; } = "";
            [JsonProperty("domain")] public string Domain { get; set; } = "";
            [JsonProperty("avatarPrefix")] public string AvatarPrefix { get; set; } = "cache/avatar";
        }

        private static OssCacheConfig? _ossCacheConfig;

        /// <summary>GET /api/friend/ossCacheConfig（结果缓存）</summary>
        private static async Task<OssCacheConfig?> GetOssCacheConfigAsync()
        {
            if (_ossCacheConfig != null) return _ossCacheConfig;
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/friend/ossCacheConfig");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;
                _ossCacheConfig = obj.ToObject<OssCacheConfig>();
                return _ossCacheConfig;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 获取云盘缓存配置失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 头像图片直传 OSS 缓存区（STS 凭证 → V1 签名直传），返回公读访问 URL；失败返回 null。
        /// ossKey 格式：{avatarPrefix}/{yyyyMMddHHmmssfff}_{随机}.jpg
        /// </summary>
        public async Task<string?> UploadAvatarToCacheAsync(byte[] imageBytes)
        {
            try
            {
                if (imageBytes == null || imageBytes.Length == 0) return null;

                var cfg = await GetOssCacheConfigAsync().ConfigureAwait(false);
                if (cfg == null || string.IsNullOrEmpty(cfg.Domain)) return null;

                // STS 凭证复用云盘服务的通用上传凭证接口（/api/common/upload/request-sts-token）
                var sts = await CloudDriveService.Instance.RequestStsTokenAsync().ConfigureAwait(false);
                if (sts == null || string.IsNullOrEmpty(sts.AccessKeyId)) return null;

                string ossKey = $"{cfg.AvatarPrefix.Trim('/')}/{DateTime.Now:yyyyMMddHHmmssfff}_{Random.Shared.Next(1000, 10000)}.jpg";
                // 上传目标 bucket/region 以后端 ossCache 配置为准（STS 响应里的 bucket 仅作兜底）
                string bucket = string.IsNullOrEmpty(cfg.Bucket) ? sts.Bucket : cfg.Bucket;
                string region = string.IsNullOrEmpty(cfg.Region) ? sts.Region : cfg.Region;
                // bucket 为私有 ACL 时，头像对象须单独设 public-read 才能匿名访问
                bool uploaded = await PutBytesToOssAsync(sts, bucket, region, ossKey, imageBytes, "image/jpeg", publicRead: true).ConfigureAwait(false);
                if (!uploaded) return null;

                return $"{cfg.Domain.TrimEnd('/')}/{ossKey}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 头像缓存上传失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>OSS V1 签名直传（字节数组版，可指定 Content-Type；publicRead=true 时对象 ACL 设为公读）</summary>
        private static async Task<bool> PutBytesToOssAsync(StsTokenResult sts, string bucket, string region, string ossKey, byte[] data, string contentType, bool publicRead = false)
        {
            try
            {
                string url = $"https://{bucket}.{region}.aliyuncs.com/{ossKey}";
                string date = DateTime.UtcNow.ToString("R");

                // x-oss-* 头须按字典序拼接参与签名（object-acl 在 security-token 之前）
                string canonicalizedOssHeaders = publicRead
                    ? $"x-oss-object-acl:public-read\nx-oss-security-token:{sts.SecurityToken}\n"
                    : $"x-oss-security-token:{sts.SecurityToken}\n";
                string canonicalizedResource = $"/{bucket}/{ossKey}";
                string stringToSign = $"PUT\n\n{contentType}\n{date}\n{canonicalizedOssHeaders}{canonicalizedResource}";
                using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sts.AccessKeySecret));
                string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

                using var content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Date", date);
                req.Headers.TryAddWithoutValidation("Authorization", $"OSS {sts.AccessKeyId}:{signature}");
                if (publicRead) req.Headers.TryAddWithoutValidation("x-oss-object-acl", "public-read");
                req.Headers.TryAddWithoutValidation("x-oss-security-token", sts.SecurityToken);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[Friend] 头像OSS上传失败: HTTP {(int)resp.StatusCode} - {errBody}");
                    return false;
                }
                await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Friend] 头像OSS上传异常: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
