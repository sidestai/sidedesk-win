using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 通知服务端状态同步服务（单例）：服务端 system_notification 表是已读/未读的权威源。
    /// - WS 连接成功（含登录/断线重连）后拉 /api/notification/list：按服务端已读状态恢复通知中心历史与未读红点；
    /// - 点过即已读：点击通知主体/按钮回传 /api/notification/read；打开通知中心=全部已读；清空=服务端同清；
    /// - 接口不可用（如旧后端未建表）时降级为 <see cref="TeamNotificationService.BackfillPendingAsync"/> 只补团队待办。
    /// </summary>
    public sealed class NotificationSyncService
    {
        private static readonly Lazy<NotificationSyncService> _instance = new(() => new NotificationSyncService());
        public static NotificationSyncService Instance => _instance.Value;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private NotificationSyncService() { }

        #region 列表同步

        /// <summary>拉取服务端通知列表并合并进本地通知中心（WS 连接成功后调用）</summary>
        public async Task SyncAsync()
        {
            var app = System.Windows.Application.Current as App;
            if (app == null || string.IsNullOrEmpty(app.AppSettings.AccountToken)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Constants.URLs.ServerBaseUrl + "/api/notification/list?limit=100");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[NotificationSync] {(int)resp.StatusCode}: {json}");

                var obj = JObject.Parse(json);
                if ((int?)obj["code"] != 200 || obj["data"] is not JArray arr)
                    throw new InvalidOperationException("unexpected response shape");
                MergeServerRows(arr);
                // 服务端通知表只记部署后发生的事件：待处理加入申请/邀请以 system_team_apply 为准，
                // 同步成功后仍补拉一次，避免表里无对应行（离线期间/落库失败/部署前事件）时通知中心漏条目（Id 去重防重复）
                await TeamNotificationService.Instance.BackfillPendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationSync] list sync failed, fallback to team pending backfill: {ex.Message}");
                await TeamNotificationService.Instance.BackfillPendingAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 把服务端通知行合并进本地历史：本地没有的按服务端已读状态补入（未读只亮红点不弹窗）；
        /// 本地已有的对齐 ServerId 与已读状态（本地已读但服务端未读时补一次已读回传）。
        /// </summary>
        private static void MergeServerRows(JArray arr)
        {
            var toMarkRead = new List<string>();
            // 服务端最新在前；按从旧到新顺序入队，保持本地历史「最新在前」
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is not JObject row) continue;
                var serverId = (string?)row["id"];
                var evt = (string?)row["event"] ?? "";
                var data = row["data"] as JObject ?? new JObject();
                bool read = row["read"]?.ToObject<bool>() == true;

                var payload = BuildPayloadForEvent(evt, data);
                if (payload == null) continue;
                payload.ServerId = serverId;
                // 发送时间以服务端创建时间为准（「超一个月未读不亮红点」规则依赖真实时间）
                var ct = row["createdAt"];
                if (ct != null && ct.Type == JTokenType.Date) payload.Timestamp = ct.ToObject<DateTime>();

                // 本地持久化已读但服务端仍未读（此前回传失败/旧数据）：以本地为准，并顺便回传对齐
                if (!read && NotificationService.Instance.IsLocallyRead(payload.Id))
                {
                    read = true;
                    if (!string.IsNullOrEmpty(serverId)) toMarkRead.Add(serverId);
                }

                var existing = NotificationService.Instance.FindById(payload.Id);
                if (existing != null)
                {
                    // 服务端重通知（如重复提交加入申请会新建一行并重新推送）：同 Id 但服务端行已换新且未读，
                    // 本地条目重新置顶为未读并改指最新行（否则旧行已读会把新未读行反向对齐掉，红点不再亮）
                    bool newerServerRow = !string.IsNullOrEmpty(serverId) && existing.ServerId != serverId
                        && !read && ct != null && ct.Type == JTokenType.Date
                        && ct.ToObject<DateTime>() > existing.Timestamp.AddSeconds(60);
                    if (newerServerRow)
                    {
                        existing.ServerId = serverId;
                        NotificationService.Instance.Bump(existing.Id, payload.Timestamp);
                        continue;
                    }
                    if (string.IsNullOrEmpty(existing.ServerId)) existing.ServerId = serverId;
                    if (existing.IsRead && !read && !string.IsNullOrEmpty(serverId))
                        toMarkRead.Add(serverId); // 本地点过但服务端未读（回传失败/旧数据）：对齐一次
                    else if (!existing.IsRead && read)
                        NotificationService.Instance.MarkRead(existing.Id); // 其他设备已读
                    continue;
                }

                payload.IsRead = read;
                // 同步回来的历史只进通知中心：未读亮红点，不连环弹窗
                payload.DisplayMode = NotificationDisplayMode.Silent;
                NotificationService.Instance.Enqueue(payload);
            }

            if (toMarkRead.Count > 0) _ = Instance.PostReadAsync(toMarkRead, all: false);
        }

        /// <summary>
        /// WS 实时气泡类事件（审批结果/成员入团/好友请求）：同步写一条进通知中心，
        /// 参与已读状态与红点（气泡本身只是瞬时提示，中心里的条目才是可回溯记录）。
        /// </summary>
        public void EnqueueLiveSilent(string evt, JObject data)
        {
            var payload = BuildPayloadForEvent(evt, data);
            if (payload == null) return;
            payload.ServerId = (string?)data["notificationId"];
            payload.DisplayMode = NotificationDisplayMode.Silent;
            if (NotificationService.Instance.ContainsId(payload.Id)) return;
            NotificationService.Instance.Enqueue(payload);
        }

        #endregion

        #region 已读/清空回传

        /// <summary>点过即已读：本地标记 + 回传服务端（即发即忘，失败下次同步对齐）</summary>
        public void MarkRead(NotificationPayload? n)
        {
            if (n == null || n.IsRead) return;
            NotificationService.Instance.MarkRead(n.Id);
            if (!string.IsNullOrEmpty(n.ServerId))
                _ = PostReadAsync(new List<string> { n.ServerId! }, all: false);
        }

        /// <summary>打开通知中心=全部已读：本地 MarkAllRead 由窗口自行调用，这里只回传服务端</summary>
        public void MarkAllReadServer() => _ = PostReadAsync(null, all: true);

        /// <summary>清空通知中心：服务端同清，避免下次同步把已清条目拉回来</summary>
        public void ClearServer() => _ = PostAsync("/api/notification/clear", new JObject());

        private Task PostReadAsync(List<string>? ids, bool all)
        {
            var body = new JObject { ["all"] = all };
            body["ids"] = ids == null ? new JArray() : JArray.FromObject(ids);
            return PostAsync("/api/notification/read", body);
        }

        private async Task PostAsync(string path, JObject body)
        {
            try
            {
                var app = System.Windows.Application.Current as App;
                if (app == null || string.IsNullOrEmpty(app.AppSettings.AccountToken)) return;
                using var req = new HttpRequestMessage(HttpMethod.Post, Constants.URLs.ServerBaseUrl + path);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[NotificationSync] POST {path} -> {(int)resp.StatusCode}: {json}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationSync] POST {path} failed: {ex.Message}");
            }
        }

        #endregion

        #region 事件 → 通知载荷映射（与 WS 实时分支共用同一套 Id 规则保证去重）

        /// <summary>按服务端事件类型重建通知载荷；不支持的事件返回 null（不参与通知中心）</summary>
        internal static NotificationPayload? BuildPayloadForEvent(string evt, JObject data)
        {
            switch (evt)
            {
                case "team_join_request":
                {
                    var applicationId = (string?)data["applicationId"];
                    if (string.IsNullOrEmpty(applicationId)) return null;
                    var applicantName = (string?)data["applicantName"] ?? Framework.Lang.T("nativePanel.unknownUser");
                    return TeamNotificationService.BuildApplyPayload(
                        TeamNotificationService.JoinIdPrefix + applicationId,
                        Framework.Lang.TF("nativePanel.teamApplyNotification", applicantName, (string?)data["teamName"] ?? ""),
                        applicationId, "join", silent: true);
                }
                case "team_invite":
                {
                    var applicationId = (string?)data["applicationId"];
                    if (string.IsNullOrEmpty(applicationId)) return null;
                    var inviterName = (string?)data["inviterName"] ?? Framework.Lang.T("common.admin");
                    return TeamNotificationService.BuildApplyPayload(
                        TeamNotificationService.InviteIdPrefix + applicationId,
                        Framework.Lang.TF("nativePanel.teamInviteNotification", inviterName, (string?)data["teamName"] ?? ""),
                        applicationId, "invite", silent: true);
                }
                case "team_application_result":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    bool accepted = data["accepted"]?.ToObject<bool>() == true;
                    return BuildInfoPayload(
                        "team-result-" + nid,
                        Framework.Lang.TF(accepted ? "nativePanel.teamJoinApproved" : "nativePanel.teamJoinDeclined", (string?)data["teamName"] ?? ""),
                        "teams");
                }
                case "team_member_joined":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    var memberName = (string?)data["memberName"] ?? Framework.Lang.T("team.newMember");
                    return BuildInfoPayload(
                        "team-joined-" + nid,
                        Framework.Lang.TF("nativePanel.joinedTeam", memberName, (string?)data["teamName"] ?? ""),
                        "teams");
                }
                case "friend_request":
                {
                    var requestId = (string?)data["requestId"];
                    if (string.IsNullOrEmpty(requestId)) return null;
                    var fromName = (string?)data["fromName"] ?? Framework.Lang.T("nativePanel.unknownUser");
                    return BuildInfoPayload(
                        "friend-request-" + requestId,
                        Framework.Lang.TF("nativePanel.friendRequestNotification", fromName),
                        "friends");
                }
                case "friend_request_result":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    bool accepted = data["accepted"]?.ToObject<bool>() == true;
                    var friendName = (string?)data["friendName"] ?? Framework.Lang.T("nativePanel.unknownUser");
                    return BuildInfoPayload(
                        "friend-result-" + nid,
                        Framework.Lang.TF(accepted ? "nativePanel.friendRequestApproved" : "nativePanel.friendRequestDeclined", friendName),
                        "friends");
                }
                case "notification":
                    // 富通知推送：载荷自带标题/按钮/点击动作，原样解析
                    return NotificationPayload.FromJson(data);
                default:
                    return null;
            }
        }

        /// <summary>信息类通知：点击主体跳对应设置页（点击即触发已读回传）</summary>
        private static NotificationPayload BuildInfoPayload(string id, string title, string settingsTab)
        {
            return new NotificationPayload
            {
                Id = id,
                Title = title,
                Timestamp = DateTime.Now,
                Timeout = 15,
                DisplayMode = NotificationDisplayMode.Silent,
                ClickMain = new NotificationAction
                {
                    Type = "open_settings",
                    Payload = new Dictionary<string, object> { ["tab"] = settingsTab }
                }
            };
        }

        #endregion
    }
}
