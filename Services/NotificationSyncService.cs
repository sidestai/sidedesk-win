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

                // 落库的 data 不含 notificationId（只在实时推送时附加），列表同步时补回：
                // 既保证与实时推送生成同一个本地 Id（去重），也让依赖 nid 的事件重启后仍能恢复进中心
                if (!string.IsNullOrEmpty(serverId) && string.IsNullOrEmpty((string?)data["notificationId"]))
                    data["notificationId"] = serverId;

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
                case "team_membership_notice":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    var text = BuildTeamNoticeText(evt, data);
                    if (text == null) return null;
                    return BuildInfoPayload(evt + "-" + nid, text, "teams");
                }
                case "team_item_notice":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    var text = BuildTeamNoticeText(evt, data);
                    if (text == null) return null;
                    // 卡片/微屏操作告知：主体在侧边栏，设置团队页无对应视图 → 不带点击动作，仅作可回溯记录
                    return BuildInfoPayload(evt + "-" + nid, text, null);
                }
                case "share_revoked_notice":
                {
                    var nid = (string?)data["notificationId"];
                    if (string.IsNullOrEmpty(nid)) return null;
                    var text = BuildTeamNoticeText(evt, data);
                    if (text == null) return null;
                    // 「我的分享」是独立窗口而非设置页，无可跳转的 tab → 不带点击动作，仅作可回溯记录
                    return BuildInfoPayload(evt + "-" + nid, text, null);
                }
                case "notification":
                    // 富通知推送：载荷自带标题/按钮/点击动作，原样解析
                    return NotificationPayload.FromJson(data);
                default:
                    return null;
            }
        }

        /// <summary>
        /// 团队/分享类通知（成员身份失效、团队卡片·微屏的成员操作与移除、引用分享被取消）的本地化文案。
        /// WS 实时气泡与通知中心条目共用同一份文案，避免两处措辞不一致；不支持的事件返回 null。
        ///
        /// 两类语义不同，文案措辞也不同：
        /// - 「内容被移除」（deleted / moved / 身份失效 / 分享取消）：本地数据已由配套指令
        ///   （team_membership_revoked / team_item_removed / team_bar_removed / share_cancelled）即时清除，
        ///   所以文案明确说「已从您的微屏移除」；
        /// - 「成员操作」（created / renamed / updated / relocated / restored / joined / rows_changed）：
        ///   本地数据由 share_item_meta_updated · bar_meta_updated 刷新或等下次对账拉取，
        ///   文案只客观陈述「谁做了什么」，不承诺本地已发生对应变化。
        /// </summary>
        internal static string? BuildTeamNoticeText(string evt, JObject data)
        {
            switch (evt)
            {
                case "team_membership_notice":
                {
                    var teamName = (string?)data["teamName"] ?? "";
                    // disbanded=团队解散；其余（removed=被管理员移出）统一按「被移出团队」表述
                    return (string?)data["reason"] == "disbanded"
                        ? Framework.Lang.TF("nativePanel.teamDisbandedNotice", teamName)
                        : Framework.Lang.TF("nativePanel.teamRemovedNotice", teamName);
                }
                case "team_item_notice":
                {
                    var teamName = (string?)data["teamName"] ?? "";
                    var operatorName = ActorName((string?)data["operatorName"]);
                    bool isBar = (string?)data["kind"] == "bar";
                    var title = ContentTitle((string?)data["title"], (string?)data["panelType"], isBar);
                    // 改名类动作：服务端 title 传的是新名，oldTitle 才是改之前的名字
                    var oldTitle = ContentTitle((string?)data["oldTitle"], (string?)data["panelType"], isBar);
                    var reason = (string?)data["reason"] ?? "";
                    if (isBar)
                    {
                        return reason switch
                        {
                            "created" => Framework.Lang.TF("nativePanel.teamBarCreatedNotice", operatorName, teamName, title),
                            "renamed" => Framework.Lang.TF("nativePanel.teamBarRenamedNotice", operatorName, teamName, oldTitle, title),
                            "restored" => Framework.Lang.TF("nativePanel.teamBarRestoredNotice", operatorName, teamName, title),
                            _ => Framework.Lang.TF("nativePanel.teamBarDeletedNotice", operatorName, teamName, title),
                        };
                    }
                    return reason switch
                    {
                        "created" => Framework.Lang.TF("nativePanel.teamItemCreatedNotice", operatorName, teamName, title),
                        "restored" => Framework.Lang.TF("nativePanel.teamItemRestoredNotice", operatorName, teamName, title),
                        "renamed" => Framework.Lang.TF("nativePanel.teamItemRenamedNotice", operatorName, teamName, oldTitle, title),
                        "relocated" => Framework.Lang.TF("nativePanel.teamItemRelocatedNotice", operatorName, teamName, title, BarTitle(data)),
                        "joined" => Framework.Lang.TF("nativePanel.teamItemJoinedNotice", operatorName, teamName, title),
                        "updated" => Framework.Lang.TF("nativePanel.teamItemUpdatedNotice", operatorName, teamName, title),
                        "rows_changed" => Framework.Lang.TF("nativePanel.teamItemRowsChangedNotice", operatorName, teamName, title, RowsChangeDetail(data)),
                        "moved" => Framework.Lang.TF("nativePanel.teamItemMovedNotice", operatorName, teamName, title),
                        _ => Framework.Lang.TF("nativePanel.teamItemDeletedNotice", operatorName, teamName, title),
                    };
                }
                case "share_revoked_notice":
                {
                    var ownerName = ActorName((string?)data["ownerName"]);
                    bool isBar = (string?)data["shareLevel"] == "bar";
                    var title = ContentTitle((string?)data["title"], (string?)data["panelType"], isBar);
                    return Framework.Lang.TF(
                        isBar ? "nativePanel.shareCancelledBarNotice" : "nativePanel.shareCancelledItemNotice",
                        ownerName, title);
                }
                default:
                    return null;
            }
        }

        /// <summary>卡片被移动到的目标微屏名（服务端取不到时回退「未命名窗口」）</summary>
        private static string BarTitle(JObject data)
            => ContentTitle((string?)data["barTitle"], null, true);

        /// <summary>
        /// 行数据变更明细：只列出非零项（「新增 0 条」属于噪音），用本地化分隔符连接。
        /// 服务端仅在真有行变更时才发 rows_changed，三项全零不会发生；万一发生则回退「修改 0 条」而不是空括号。
        /// </summary>
        private static string RowsChangeDetail(JObject data)
        {
            int added = (int?)data["added"] ?? 0;
            int updated = (int?)data["updated"] ?? 0;
            int deleted = (int?)data["deleted"] ?? 0;
            var parts = new List<string>();
            if (added > 0) parts.Add(Framework.Lang.TF("nativePanel.teamRowsAddedPart", added));
            if (updated > 0) parts.Add(Framework.Lang.TF("nativePanel.teamRowsUpdatedPart", updated));
            if (deleted > 0) parts.Add(Framework.Lang.TF("nativePanel.teamRowsDeletedPart", deleted));
            if (parts.Count == 0) parts.Add(Framework.Lang.TF("nativePanel.teamRowsUpdatedPart", 0));
            return string.Join(Framework.Lang.T("nativePanel.teamRowsPartSeparator"), parts);
        }

        /// <summary>操作者名：服务端取不到昵称/账号名（账号已注销等）时回退「未知用户」</summary>
        private static string ActorName(string? name)
            => string.IsNullOrEmpty(name) ? Framework.Lang.T("nativePanel.unknownUser") : name!;

        /// <summary>
        /// 通知里的内容名：服务端显示名优先；无显示名的类型（便签/待办等 title 列为空）按 panelType
        /// 回退本地化类型名，再回退「未命名卡片/窗口」—— 不能只显示「删除了」而没有宾语。
        /// </summary>
        private static string ContentTitle(string? title, string? panelType, bool isBar)
        {
            if (!string.IsNullOrEmpty(title)) return title!;
            if (!string.IsNullOrEmpty(panelType) && Enum.TryParse<PanelType>(panelType, out var pt))
            {
                var typeName = SettingsWindow.GetPanelTypeName(pt);
                if (!string.IsNullOrEmpty(typeName)) return typeName;
            }
            return Framework.Lang.T(isBar ? "settings.untitledWindow" : "settings.untitledModule");
        }

        /// <summary>信息类通知：点击主体跳对应设置页（点击即触发已读回传）；settingsTab 为 null 时不带点击动作</summary>
        private static NotificationPayload BuildInfoPayload(string id, string title, string? settingsTab)
        {
            return new NotificationPayload
            {
                Id = id,
                Title = title,
                Timestamp = DateTime.Now,
                Timeout = 15,
                DisplayMode = NotificationDisplayMode.Silent,
                ClickMain = settingsTab == null
                    ? null
                    : new NotificationAction
                    {
                        Type = "open_settings",
                        Payload = new Dictionary<string, object> { ["tab"] = settingsTab }
                    }
            };
        }

        #endregion
    }
}
