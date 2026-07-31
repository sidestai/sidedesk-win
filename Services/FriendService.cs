using System;
using System.Collections.Generic;
using System.Net.Http;
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
        [JsonProperty("userId")] public int UserId { get; set; }
        [JsonProperty("nickName")] public string NickName { get; set; } = "";
        [JsonProperty("userName")] public string UserName { get; set; } = "";
        [JsonProperty("avatar")] public string Avatar { get; set; } = "";
        [JsonProperty("isOnline")] public int IsOnline { get; set; }
        [JsonProperty("lastOnlineTime")] public DateTime? LastOnlineTime { get; set; }
    }

    /// <summary>好友请求</summary>
    public class FriendRequest
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonProperty("fromUser")] public FriendUser? FromUser { get; set; }
    }

    #endregion

    /// <summary>
    /// 好友服务：对接后端好友接口（localhost:3007，与团队服务同一鉴权与响应格式）。
    /// </summary>
    public sealed class FriendService
    {
        private static readonly Lazy<FriendService> _lazy = new(() => new FriendService());
        public static FriendService Instance => _lazy.Value;

        private const string BaseUrl = "http://localhost:3007";

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

        /// <summary>POST /api/friend/add — Body: { friendUserId }</summary>
        public async Task<(bool ok, string msg)> AddFriendAsync(int friendUserId)
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
                return (false, "网络错误");
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

        /// <summary>POST /api/friend/handle — Body: { requestId, action }</summary>
        public async Task<(bool ok, string msg)> HandleRequestAsync(int requestId, string action)
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
                return (false, "网络错误");
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

        /// <summary>DELETE /api/friend/remove — Body: { friendUserId }</summary>
        public async Task<(bool ok, string msg)> RemoveFriendAsync(int friendUserId)
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
                return (false, "网络错误");
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
                return (false, "网络错误");
            }
        }

        #endregion
    }
}
