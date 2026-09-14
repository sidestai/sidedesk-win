using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services;

/// <summary>
/// 数字员工后端 API 服务：从后端获取用户已选择的数字员工信息与状态。
/// 卡片展示的员工来自"我的员工"列表（sidedesk_user_digital_employee），多处展示数据唯一。
/// </summary>
public sealed class DigitalEmployeeApiService
{
    private static readonly Lazy<DigitalEmployeeApiService> _lazy = new(() => new DigitalEmployeeApiService());
    public static DigitalEmployeeApiService Instance => _lazy.Value;

    private const string BaseUrl = Constants.URLs.ServerBaseUrl;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private DigitalEmployeeApiService() { }

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
        System.Diagnostics.Debug.WriteLine($"[DigitalEmployee] {(int)resp.StatusCode}: {json}");
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                Instance.LastAuthFailed = true;
            return null;
        }

        var root = JObject.Parse(json);
        int bizCode = root["code"]?.Value<int>() ?? 0;
        if (bizCode == 401 || bizCode == 403)
            Instance.LastAuthFailed = true;
        if (bizCode != 200) return null;
        return root["data"];
    }

    #region 1. 获取我的数字员工列表（卡片展示数据源）

    /// <summary>GET /api/digital-employee/my — 返回用户已选择的员工（含团队/状态设置）</summary>
    public async Task<List<RemoteDigitalEmployee>?> GetMyListAsync()
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"{BaseUrl}/api/digital-employee/my");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
            if (data is not JArray arr) return null;
            return arr.ToObject<List<RemoteDigitalEmployee>>() ?? new List<RemoteDigitalEmployee>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DigitalEmployee] failed to get my digital employee list: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region 2. 更新我的员工设置（状态/团队/备注）

    /// <summary>POST /api/digital-employee/updateMySetting — Body: { id, status?, teamName?, remark? }</summary>
    public async Task<(bool ok, string msg)> UpdateMySettingAsync(int id, string? status = null, string? teamName = null)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/digital-employee/updateMySetting");
            var body = new JObject { ["id"] = id };
            if (status != null) body["status"] = status;
            if (teamName != null) body["teamName"] = teamName;
            req.Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var root = JObject.Parse(json);
            int bizCode = root["code"]?.Value<int>() ?? 0;
            string msg = root["msg"]?.Value<string>() ?? "";
            return (bizCode == 200, msg);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DigitalEmployee] failed to update employee settings: {ex.Message}");
            return (false, ex.Message);
        }
    }

    #endregion
}

/// <summary>后端"我的数字员工"数据结构（选择记录 + 员工基础信息合并）</summary>
public class RemoteDigitalEmployee
{
    /// <summary>选择记录ID（sidedesk_user_digital_employee.id）</summary>
    public int Id { get; set; }
    /// <summary>员工库ID（sidedesk_digital_employee.id）</summary>
    public int EmployeeId { get; set; }
    public int IsSystem { get; set; }
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Role { get; set; } = "";
    public string Department { get; set; } = "";
    public string Skills { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>所属团队</summary>
    public string TeamName { get; set; } = "";
    /// <summary>状态：online/busy/offline</summary>
    public string Status { get; set; } = "online";
    /// <summary>备注</summary>
    public string Remark { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>状态显示文本</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string StatusText => Status switch
    {
        "online" => Framework.Lang.T("common.online"),
        "busy" => Framework.Lang.T("digitalEmployee.busy"),
        "offline" => Framework.Lang.T("common.offline"),
        _ => Framework.Lang.T("common.online")
    };
}
