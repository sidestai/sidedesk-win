using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace SideDesk.Services;

/// <summary>
/// 数据模板市场本地桥：处理 desktxp://sidedesk/data-templates/* 自定义协议请求
///（由 MarketWindow 的 WebView2 拦截转发），无需 WebSocket。
/// 协议：/data-templates/list 查询全部模板（内置 + 用户自建）；
/// /data-templates/open?tid=&lt;加密行ID&gt; 按模板 ID 唤起客户端原生数据项配置对话框；
/// /data-templates/add?config=&lt;urlencoded JSON&gt; 将市场页配置好的模板落地为侧边栏数据模块。
/// </summary>
public static class DataTemplateMarketBridge
{
    /// <summary>分发自定义协议请求，返回 (HTTP 状态码, JSON 响应体)</summary>
    public static (int Status, string Json) HandleScheme(string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
            switch (uri.AbsolutePath.TrimEnd('/'))
            {
                case "/data-templates/list":
                    return (200, GetTemplates());

                case "/data-templates/open":
                    return OpenTemplateDialog(uri.Query);

                case "/data-templates/add":
                    return AddTemplate(uri.Query);

                default:
                    return (404, "{\"ok\":false,\"message\":\"unknown path\"}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataTemplateMarket] scheme handling error: {ex.Message}");
            return (500, $"{{\"ok\":false,\"message\":{JsonConvert.SerializeObject(ex.Message)}}}");
        }
    }

    /// <summary>返回 data-templates/ 目录下全部模板（内置 + 用户自建）</summary>
    private static string GetTemplates()
    {
        var templates = Models.DataTemplateManager.Load().Templates;
        return JsonConvert.SerializeObject(templates);
    }

    /// <summary>
    /// 市场页点「添加」：按加密行 ID 后台拉取模板详情，成功后切 UI 线程唤起
    /// 原生数据项配置对话框（含变量名/团队/浏览器与占位符参数）。fire-and-forget，立即返回 ok。
    /// </summary>
    private static (int Status, string Json) OpenTemplateDialog(string query)
    {
        var tid = ParseQuery(query).GetValueOrDefault("tid") ?? "";
        if (string.IsNullOrEmpty(tid))
            return (400, "{\"ok\":false,\"message\":\"missing tid\"}");

        _ = Task.Run(async () =>
        {
            var tpl = await TeamService.Instance.GetMarketTemplateAsync(tid);
            if (tpl == null)
            {
                System.Diagnostics.Debug.WriteLine("[DataTemplateMarket] failed to fetch template details or it has been removed");
                return;
            }
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Controls.NativeModePanel.Instance?.ShowMarketTemplateDialog(tpl);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DataTemplateMarket] failed to open config dialog: {ex.Message}");
                }
            }));
        });
        return (200, "{\"ok\":true}");
    }

    /// <summary>
    /// 添加市场页配置好的模板（TargetUrl 占位符已由页面替换为实际值）：
    /// 存入 user-templates.json（Id 冲突自动追加时间戳，避免覆盖同名历史实例），
    /// 再切 UI 线程在当前侧边栏新建数据看板模块并同步轮询服务。
    /// </summary>
    private static (int Status, string Json) AddTemplate(string query)
    {
        var config = ParseQuery(query).GetValueOrDefault("config") ?? "";
        if (string.IsNullOrEmpty(config))
            return (400, "{\"ok\":false,\"message\":\"missing config\"}");

        var tpl = JsonConvert.DeserializeObject<Models.DataTemplate>(config);
        if (tpl == null || string.IsNullOrEmpty(tpl.Name)
            || string.IsNullOrEmpty(tpl.TargetUrl) || string.IsNullOrEmpty(tpl.Selector))
            return (400, "{\"ok\":false,\"message\":\"invalid template config\"}");
        if (tpl.RefreshIntervalSeconds <= 0) tpl.RefreshIntervalSeconds = 60;

        var manager = Models.DataTemplateManager.Load();
        if (string.IsNullOrEmpty(tpl.Id))
            tpl.Id = "market-" + Guid.NewGuid().ToString("N")[..8];
        // 同一市场模板可用不同参数反复添加；Id 冲突会覆盖旧实例，追加时间戳区分
        if (manager.Templates.Any(t => t.Id == tpl.Id))
            tpl.Id = $"{tpl.Id}-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
        manager.SaveUserTemplate(tpl);

        // 落地 UI：当前活跃的侧边栏面板新增数据模块
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Controls.NativeModePanel.Instance?.AddDataTemplateFromMarket(tpl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataTemplateMarket] failed to add module: {ex.Message}");
            }
        }));
        return (200, "{\"ok\":true}");
    }

    /// <summary>解析 query 字符串为字典（自动 URL 解码）</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict[key] = value;
        }
        return dict;
    }
}
