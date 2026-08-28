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

    /// <summary>团队成员</summary>
    public class TeamMember
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>头像 URL（可为空，空时前端用首字占位圆）</summary>
        [JsonProperty("avatar")] public string? Avatar { get; set; }
    }

    /// <summary>团队</summary>
    public class Team
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("members")] public List<TeamMember> Members { get; set; } = new();
        /// <summary>团队码（加入团队凭证，供成员分享）</summary>
        [JsonProperty("inviteCode")] public string? InviteCode { get; set; }
        /// <summary>当前用户是否为该团队管理员（创建者）：可审批申请/邀请成员</summary>
        [JsonProperty("isAdmin")] public bool IsAdmin { get; set; }
    }

    /// <summary>团队加入申请（管理员视角）</summary>
    public class TeamApplication
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("teamId")] public string TeamId { get; set; } = "";
        [JsonProperty("teamName")] public string TeamName { get; set; } = "";
        [JsonProperty("applicantUserId")] public string ApplicantUserId { get; set; } = "";
        [JsonProperty("applicantName")] public string ApplicantName { get; set; } = "";
        [JsonProperty("applicantAvatar")] public string? ApplicantAvatar { get; set; }
    }

    /// <summary>团队邀请（被邀请人视角）</summary>
    public class TeamInvitation
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("teamId")] public string TeamId { get; set; } = "";
        [JsonProperty("teamName")] public string TeamName { get; set; } = "";
        [JsonProperty("inviterName")] public string InviterName { get; set; } = "";
    }

    /// <summary>团队数据模板（后端 team/list 返回项）</summary>
    public class TeamTemplateInfo
    {
        [JsonProperty("localId")] public string LocalId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("targetUrl")] public string TargetUrl { get; set; } = "";
        [JsonProperty("selector")] public string Selector { get; set; } = "";
        [JsonProperty("refreshInterval")] public int RefreshInterval { get; set; } = 60;
        [JsonProperty("selectorType")] public string SelectorType { get; set; } = "";
        [JsonProperty("creatorName")] public string CreatorName { get; set; } = "";
    }

    /// <summary>团队组件库条目</summary>
    public class TeamLibraryItem
    {
        [JsonProperty("itemServerId")] public string ItemServerId { get; set; } = "";
        [JsonProperty("title")] public string? Title { get; set; }
        [JsonProperty("itemType")] public string ItemType { get; set; } = "";
        [JsonProperty("itemData")] public string? ItemData { get; set; }
        [JsonProperty("corpId")] public string? CorpId { get; set; }  // 服务端加密串，与团队列表的 id 格式一致
        [JsonProperty("isMine")] public bool IsMine { get; set; }
        [JsonProperty("creatorName")] public string CreatorName { get; set; } = "";
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    /// <summary>模块归属信息（批量查创建者/团队）</summary>
    public class ItemOwnerInfo
    {
        [JsonProperty("itemServerId")] public string ItemServerId { get; set; } = "";
        [JsonProperty("creatorName")] public string CreatorName { get; set; } = "";
        [JsonProperty("ownerType")] public string OwnerType { get; set; } = "personal"; // personal/team
        [JsonProperty("corpName")] public string CorpName { get; set; } = "";
    }

    #endregion

    /// <summary>
    /// 团队服务：对接后端团队接口（基址见 Constants.URLs.ServerBaseUrl，与云盘服务同一鉴权与响应格式）。
    /// 后端接口尚未实现，前端按约定契约开发，失败时返回 null 由 UI 优雅降级。
    /// </summary>
    public sealed class TeamService
    {
        private static readonly Lazy<TeamService> _lazy = new(() => new TeamService());
        public static TeamService Instance => _lazy.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private TeamService() { }

        /// <summary>最近一次请求是否因认证失败（401/403），供 UI 层判断是否需要刷新登录状态</summary>
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

        /// <summary>解析通用响应 { code: 200, data: ... }，返回 data 的 JToken；失败返回 null</summary>
        private static async Task<JToken?> ParseResponseTokenAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Team] {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode)
            {
                // HTTP 层失败（极少见，后端通常统一返回 200）
                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    Instance.LastAuthFailed = true;
                return null;
            }

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;

            // 业务层认证失败（后端将 401/403 包装为 HTTP 200 + code:401/403）
            if (bizCode == 401 || bizCode == 403)
            {
                Instance.LastAuthFailed = true;
                return null;
            }
            Instance.LastAuthFailed = false;

            if (bizCode != 200) return null;
            return root["data"];
        }

        /// <summary>解析通用响应并携带业务消息：(ok, msg, data)，供需要展示后端提示语的接口使用</summary>
        private static async Task<(bool Ok, string Msg, JToken? Data)> ParseResponseWithMsgAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Team] {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    Instance.LastAuthFailed = true;
                return (false, "", null);
            }

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            string msg = root["msg"]?.Value<string>() ?? "";

            if (bizCode == 401 || bizCode == 403)
            {
                Instance.LastAuthFailed = true;
                return (false, msg, null);
            }
            Instance.LastAuthFailed = false;

            return (bizCode == 200, msg, root["data"]);
        }

        #region 1. 团队列表

        /// <summary>GET /api/team/list — 返回团队数组（含成员列表）</summary>
        public async Task<List<Team>?> ListTeamsAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/team/list");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;

                return arr.ToObject<List<Team>>() ?? new List<Team>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to get team list: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 2. 创建团队

        /// <summary>POST /api/team/create — Body: { name }</summary>
        public async Task<Team?> CreateTeamAsync(string name)
        {
            try
            {
                var body = new JObject { ["name"] = name };

                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/team/create");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;

                return obj.ToObject<Team>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to create team: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 3. 加入团队 / 邀请成员

        /// <summary>POST /api/team/join — Body: { code }，凭团队码申请加入（后端 WS 通知管理员）</summary>
        public async Task<(bool Ok, string Msg)> JoinTeamAsync(string code)
        {
            try
            {
                var body = new JObject { ["code"] = code };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/team/join");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to apply to join team: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        /// <summary>GET /api/team/applications — 管理员查看团队待处理加入申请</summary>
        public async Task<List<TeamApplication>?> GetApplicationsAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/team/applications");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<TeamApplication>>() ?? new List<TeamApplication>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to get join requests: {ex.Message}");
                return null;
            }
        }

        /// <summary>POST /api/team/application/handle — Body: { applicationId, action }，管理员审批加入申请</summary>
        public async Task<(bool Ok, string Msg)> HandleApplicationAsync(string applicationId, string action)
        {
            try
            {
                var body = new JObject { ["applicationId"] = applicationId, ["action"] = action };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/team/application/handle");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to handle join request: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        /// <summary>POST /api/team/invite — Body: { corpId, userName }，管理员按用户名邀请成员（后端 WS 通知对方）</summary>
        public async Task<(bool Ok, string Msg)> InviteMemberAsync(string corpId, string userName)
        {
            try
            {
                var body = new JObject { ["corpId"] = corpId, ["userName"] = userName };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/team/invite");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to invite member: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        /// <summary>GET /api/team/invitations — 查看我收到的待处理团队邀请</summary>
        public async Task<List<TeamInvitation>?> GetInvitationsAsync()
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/team/invitations");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<TeamInvitation>>() ?? new List<TeamInvitation>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to get team invitations: {ex.Message}");
                return null;
            }
        }

        /// <summary>POST /api/team/invitation/handle — Body: { applicationId, action }，处理收到的团队邀请</summary>
        public async Task<(bool Ok, string Msg)> HandleInvitationAsync(string applicationId, string action)
        {
            try
            {
                var body = new JObject { ["applicationId"] = applicationId, ["action"] = action };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/team/invitation/handle");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to handle team invitation: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        #endregion

        #region 4. 团队组件库

        /// <summary>GET /api/sidebar/item/team-library?corpId=X — 返回指定团队的共享模块列表</summary>
        public async Task<List<TeamLibraryItem>?> GetTeamLibraryAsync(string corpId)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/sidebar/item/team-library?corpId={corpId}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;

                return arr.ToObject<List<TeamLibraryItem>>() ?? new List<TeamLibraryItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to get team library: {ex.Message}");
                return null;
            }
        }

        /// <summary>GET /api/sidebar/item/creators?ids=... — 批量查询模块创建者与归属（供「我的数据」展示）</summary>
        public async Task<Dictionary<string, ItemOwnerInfo>?> GetItemCreatorsAsync(IEnumerable<string> serverIds)
        {
            try
            {
                var ids = string.Join(",", serverIds.Where(s => !string.IsNullOrEmpty(s)).Distinct());
                if (string.IsNullOrEmpty(ids)) return new Dictionary<string, ItemOwnerInfo>();

                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/sidebar/item/creators?ids={Uri.EscapeDataString(ids)}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;

                var list = arr.ToObject<List<ItemOwnerInfo>>() ?? new List<ItemOwnerInfo>();
                return list.Where(x => !string.IsNullOrEmpty(x.ItemServerId))
                           .GroupBy(x => x.ItemServerId)
                           .ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to query module creator: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 5. 团队数据模板（数据模板市场「团队模板」分类）

        /// <summary>POST /api/sidebar/data-template/team/save — 发布/更新团队模板（按 corpId + localId upsert）</summary>
        public async Task<(bool Ok, string Msg)> SaveTeamTemplateAsync(Models.DataTemplate tpl)
        {
            try
            {
                var body = new JObject
                {
                    ["corpId"] = tpl.TeamId,
                    ["localId"] = tpl.Id,
                    ["name"] = tpl.Name,
                    ["description"] = tpl.Description ?? "",
                    ["category"] = tpl.Category ?? "",
                    ["targetUrl"] = tpl.TargetUrl,
                    ["selector"] = tpl.Selector,
                    ["selectorType"] = tpl.SelectorType ?? "",
                    ["refreshInterval"] = tpl.RefreshIntervalSeconds
                };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/data-template/team/save");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to publish team template: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        /// <summary>POST /api/sidebar/data-template/team/delete — 删除团队模板（按 corpId + localId 定位，软删除）</summary>
        public async Task<(bool Ok, string Msg)> DeleteTeamTemplateAsync(string corpId, string localId)
        {
            try
            {
                var body = new JObject { ["corpId"] = corpId, ["localId"] = localId };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/sidebar/data-template/team/delete");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var (ok, msg, _) = await ParseResponseWithMsgAsync(resp).ConfigureAwait(false);
                return (ok, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to delete team template: {ex.Message}");
                return (false, Framework.Lang.T("common.networkError"));
            }
        }

        /// <summary>GET /api/sidebar/data-template/market/detail?tid=X — 按加密行 ID 取市场模板详情（官方/团队通用）</summary>
        public async Task<Models.DataTemplate?> GetMarketTemplateAsync(string tid)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/sidebar/data-template/market/detail?tid={Uri.EscapeDataString(tid)}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;
                return new Models.DataTemplate
                {
                    // Id 用后端 encrypt2 行 ID 密文（tid），不暴露内部主键/明文 localId；旧后端无 tid 时回退 localId
                    Id = obj.Value<string>("tid") ?? obj.Value<string>("localId") ?? "",
                    Name = obj.Value<string>("name") ?? "",
                    Description = obj.Value<string>("description") ?? "",
                    Category = obj.Value<string>("category") ?? "",
                    TargetUrl = obj.Value<string>("targetUrl") ?? "",
                    Selector = obj.Value<string>("selector") ?? "",
                    SelectorType = obj.Value<string>("selectorType") ?? "",
                    RefreshIntervalSeconds = obj.Value<int?>("refreshInterval") ?? 60,
                    TeamId = obj.Value<string>("teamId") ?? ""
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to get market template details: {ex.Message}");
                return null;
            }
        }

        /// <summary>GET /api/sidebar/data-template/team/list?corpId= — 团队内全部上架模板</summary>
        public async Task<List<TeamTemplateInfo>?> GetTeamTemplatesAsync(string corpId)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/sidebar/data-template/team/list?corpId={Uri.EscapeDataString(corpId)}");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<TeamTemplateInfo>>() ?? new List<TeamTemplateInfo>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Team] failed to pull team template list: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
