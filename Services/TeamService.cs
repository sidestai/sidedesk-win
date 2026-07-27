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
    }

    #endregion

    /// <summary>
    /// 团队服务：对接后端团队接口（localhost:3007，与云盘服务同一鉴权与响应格式）。
    /// 后端接口尚未实现，前端按约定契约开发，失败时返回 null 由 UI 优雅降级。
    /// </summary>
    public sealed class TeamService
    {
        private static readonly Lazy<TeamService> _lazy = new(() => new TeamService());
        public static TeamService Instance => _lazy.Value;

        private const string BaseUrl = "http://localhost:3007";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private TeamService() { }

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
            if (!resp.IsSuccessStatusCode) return null;

            var root = JObject.Parse(json);
            if (root["code"]?.Value<int>() != 200) return null;
            return root["data"];
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
                System.Diagnostics.Debug.WriteLine($"[Team] 获取团队列表失败: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[Team] 创建团队失败: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
