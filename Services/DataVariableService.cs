using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    #region API 数据模型

    /// <summary>数据看板变量（后端 sidedesk_data_variable 表的一行）</summary>
    public class DataVariableInfo
    {
        [Newtonsoft.Json.JsonProperty("id")] public int Id { get; set; }
        [Newtonsoft.Json.JsonProperty("varKey")] public string VarKey { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("varValue")] public string? VarValue { get; set; }
        [Newtonsoft.Json.JsonProperty("label")] public string? Label { get; set; }
        [Newtonsoft.Json.JsonProperty("corpId")] public string? CorpId { get; set; } // 团队加密串（encrypt2）；null=个人
        [Newtonsoft.Json.JsonProperty("itemLocalId")] public string? ItemLocalId { get; set; }
    }

    /// <summary>数据看板变量集（后端 sidedesk_variable_set 表的一行）</summary>
    public class VariableSetInfo
    {
        [Newtonsoft.Json.JsonProperty("varKey")] public string VarKey { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("op")] public string Op { get; set; } = "SUM";
        [Newtonsoft.Json.JsonProperty("sourceKeys")] public List<string>? SourceKeys { get; set; }
        [Newtonsoft.Json.JsonProperty("pattern")] public string? Pattern { get; set; }
        [Newtonsoft.Json.JsonProperty("corpId")] public string? CorpId { get; set; } // 团队加密串；null=个人
    }

    #endregion

    /// <summary>
    /// 数据看板变量服务：对接后端变量接口（基址见 Constants.URLs.ServerBaseUrl，与团队服务同一鉴权与响应格式）。
    /// 变量作用域：corpId 有值=团队内唯一；为空=个人内唯一。
    /// 失败时返回 null/true 由 UI 优雅降级（不阻断用户操作）。
    /// </summary>
    public sealed class DataVariableService
    {
        private static readonly Lazy<DataVariableService> _lazy = new(() => new DataVariableService());
        public static DataVariableService Instance => _lazy.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        private DataVariableService() { }

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
            System.Diagnostics.Debug.WriteLine($"[DataVariable] {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode) return null;

            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            if (bizCode != 200) return null;
            return root["data"];
        }

        /// <summary>
        /// GET /api/sidebar/data-variable/check — 唯一性校验。
        /// varKey 在指定作用域（团队或个人）内是否可用。请求失败时返回 true（不阻断，交由同步时后端兜底）。
        /// </summary>
        public async Task<bool> CheckAsync(string varKey, string? corpId)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return true; // 未登录：仅本地校验
                var url = $"{BaseUrl}/api/sidebar/data-variable/check?varKey={Uri.EscapeDataString(varKey)}";
                if (!string.IsNullOrEmpty(corpId)) url += $"&corpId={Uri.EscapeDataString(corpId)}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                return data?["available"]?.Value<bool>() ?? true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataVariable] uniqueness check failed: {ex.Message}");
                return true; // 后端不可达时不阻断
            }
        }

        /// <summary>
        /// GET /api/sidebar/data-variable/list — 列出作用域内全部变量（供条件判断/显示引用）。
        /// corpId 为空=个人。失败返回 null。
        /// </summary>
        public async Task<List<DataVariableInfo>?> ListAsync(string? corpId)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return null;
                var url = $"{BaseUrl}/api/sidebar/data-variable/list";
                if (!string.IsNullOrEmpty(corpId)) url += $"?corpId={Uri.EscapeDataString(corpId)}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<DataVariableInfo>>() ?? new List<DataVariableInfo>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataVariable] failed to get variable list: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// GET /api/sidebar/data-variable/sets — 列出作用域内全部变量集。
        /// corpId 为空=个人。失败返回 null。
        /// </summary>
        public async Task<List<VariableSetInfo>?> ListSetsAsync(string? corpId)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return null;
                var url = $"{BaseUrl}/api/sidebar/data-variable/sets";
                if (!string.IsNullOrEmpty(corpId)) url += $"?corpId={Uri.EscapeDataString(corpId)}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;
                return arr.ToObject<List<VariableSetInfo>>() ?? new List<VariableSetInfo>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataVariable] failed to get variable set list: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// POST /api/sidebar/data-variable/set/delete — 删除服务端登记的变量集（按 作用域 + 变量集名）。
        /// corpId 为空=个人。请求失败返回 false。
        /// </summary>
        public async Task<bool> DeleteSetAsync(string varKey, string? corpId)
        {
            try
            {
                if (string.IsNullOrEmpty(GetToken())) return false;
                var url = $"{BaseUrl}/api/sidebar/data-variable/set/delete";
                var body = new JObject
                {
                    ["varKey"] = varKey,
                    ["corpId"] = string.IsNullOrEmpty(corpId) ? null : corpId,
                };
                using var req = CreateRequest(HttpMethod.Post, url);
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), System.Text.Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[DataVariable] {(int)resp.StatusCode}: {json}");
                if (!resp.IsSuccessStatusCode) return false;
                // 业务码兑底：ResultData.ok() 无载荷，仅校验 code=200
                var root = JObject.Parse(json);
                return (root["code"]?.Value<int>() ?? 0) == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataVariable] failed to delete server variable set: {ex.Message}");
                return false;
            }
        }
    }
}
