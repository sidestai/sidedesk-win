using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 团队加入申请 / 团队邀请的通知服务（单例）：
    /// 把服务端事件（WS 实时推送 + 登录后补拉待处理项）统一转成 <see cref="NotificationPayload"/> 进入通知中心，
    /// 并挂上「同意 / 拒绝」按钮（team_apply_handle 动作）与「打开设置 → 团队页」的点击主体动作。
    /// 通知 ID 由 applicationId 派生（<c>team-apply-{id}</c> / <c>team-invite-{id}</c>），同一申请只会出现一次；
    /// 审批完成后调用 <see cref="RemoveForApplication"/> 清除，未读数（红点）随之更新。
    /// </summary>
    public sealed class TeamNotificationService
    {
        private static readonly Lazy<TeamNotificationService> _instance = new(() => new TeamNotificationService());
        public static TeamNotificationService Instance => _instance.Value;

        /// <summary>加入申请（管理员视角）通知 ID 前缀（NotificationSyncService 复用同一规则保证去重）</summary>
        internal const string JoinIdPrefix = "team-apply-";

        /// <summary>团队邀请（被邀请人视角）通知 ID 前缀（NotificationSyncService 复用同一规则保证去重）</summary>
        internal const string InviteIdPrefix = "team-invite-";

        /// <summary>本次运行内已入队过的申请/邀请 ID：防止 WS 重复推送或多次补拉造成重复条目</summary>
        private readonly HashSet<string> _seen = new();

        private TeamNotificationService() { }

        #region WS 实时事件入队

        /// <summary>
        /// WS <c>team_join_request</c>（管理员收到）：窗口内弹出通知卡片，可直接同意/拒绝。
        /// data 字段：applicationId / teamId / teamName / applicantUserId / applicantName
        /// </summary>
        public void EnqueueJoinRequest(JObject data)
        {
            var applicationId = (string?)data["applicationId"];
            if (string.IsNullOrEmpty(applicationId)) return;

            var applicantName = (string?)data["applicantName"] ?? Framework.Lang.T("nativePanel.unknownUser");
            var teamName = (string?)data["teamName"] ?? "";

            var payload = BuildApplyPayload(
                id: JoinIdPrefix + applicationId,
                title: Framework.Lang.TF("nativePanel.teamApplyNotification", applicantName, teamName),
                applicationId: applicationId!,
                kind: "join",
                silent: false);
            payload.ServerId = (string?)data["notificationId"];
            EnqueueOrRepush(payload);
        }

        /// <summary>
        /// WS <c>team_invite</c>（被邀请人收到）：窗口内弹出通知卡片，可直接接受/拒绝。
        /// data 字段：applicationId / teamId / teamName / inviterName
        /// </summary>
        public void EnqueueInvitation(JObject data)
        {
            var applicationId = (string?)data["applicationId"];
            if (string.IsNullOrEmpty(applicationId)) return;

            var inviterName = (string?)data["inviterName"] ?? Framework.Lang.T("common.admin");
            var teamName = (string?)data["teamName"] ?? "";

            var payload = BuildApplyPayload(
                id: InviteIdPrefix + applicationId,
                title: Framework.Lang.TF("nativePanel.teamInviteNotification", inviterName, teamName),
                applicationId: applicationId!,
                kind: "invite",
                silent: false);
            payload.ServerId = (string?)data["notificationId"];
            EnqueueOrRepush(payload);
        }

        #endregion

        #region 登录后补拉（离线期间错过的申请/邀请）

        /// <summary>
        /// 从服务端补拉待处理的加入申请与团队邀请，转成静默通知写入通知中心（不弹窗，只亮红点）。
        /// 服务端是权威数据源：重启客户端后红点与待办能自动恢复；已处理过的申请不会再出现在 pending 列表中。
        /// 调用时机：后端 WS 连接成功（含登录、断线重连）后。未登录或接口不可达时静默跳过。
        /// </summary>
        public async Task BackfillPendingAsync()
        {
            var app = System.Windows.Application.Current as App;
            if (app == null || string.IsNullOrEmpty(app.AppSettings.AccountToken)) return;

            try
            {
                var applies = await TeamService.Instance.GetApplicationsAsync().ConfigureAwait(false);
                if (applies != null)
                {
                    foreach (var a in applies)
                    {
                        if (string.IsNullOrEmpty(a.Id)) continue;
                        var p = BuildApplyPayload(
                            id: JoinIdPrefix + a.Id,
                            title: Framework.Lang.TF("nativePanel.teamApplyNotification", a.ApplicantName, a.TeamName),
                            applicationId: a.Id,
                            kind: "join",
                            silent: true);
                        Enqueue(p);
                    }
                }

                var invites = await TeamService.Instance.GetInvitationsAsync().ConfigureAwait(false);
                if (invites != null)
                {
                    foreach (var inv in invites)
                    {
                        if (string.IsNullOrEmpty(inv.Id)) continue;
                        var p = BuildApplyPayload(
                            id: InviteIdPrefix + inv.Id,
                            title: Framework.Lang.TF("nativePanel.teamInviteNotification", inv.InviterName, inv.TeamName),
                            applicationId: inv.Id,
                            kind: "invite",
                            silent: true);
                        Enqueue(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TeamNotify] backfill pending team notifications failed: {ex.Message}");
            }
        }

        #endregion

        #region 审批后清理

        /// <summary>
        /// 审批完成（同意或拒绝）后移除对应通知：通知中心不再留下过期条目，未读红点随之更新。
        /// 两种前缀都尝试移除，调用方无需区分是申请还是邀请。
        /// </summary>
        public void RemoveForApplication(string applicationId)
        {
            if (string.IsNullOrEmpty(applicationId)) return;
            NotificationService.Instance.RemoveById(JoinIdPrefix + applicationId);
            NotificationService.Instance.RemoveById(InviteIdPrefix + applicationId);
        }

        #endregion

        #region 内部逻辑

        /// <summary>构建审批类通知载荷：高优先级 + 同意/拒绝按钮 + 点击主体跳设置团队页（同步服务复用）</summary>
        internal static NotificationPayload BuildApplyPayload(string id, string title, string applicationId, string kind, bool silent)
        {
            return new NotificationPayload
            {
                Id = id,
                Title = title,
                Timestamp = DateTime.Now,
                // 15 秒后自动收起（仍留在通知中心）；避免常驻卡片长期占用 MaxConcurrent 显示槽位
                Timeout = 15,
                Priority = NotificationPriority.High,
                // 补拉的历史待办只进通知中心亮红点，不在启动时连环弹窗
                DisplayMode = silent ? NotificationDisplayMode.Silent : NotificationDisplayMode.Banner,
                ClickMain = new NotificationAction
                {
                    Type = "open_settings",
                    Payload = new Dictionary<string, object> { ["tab"] = "teams" }
                },
                Buttons = new List<NotificationAction>
                {
                    BuildHandleAction(applicationId, kind, "accept", Framework.Lang.T(kind == "invite" ? "common.accept" : "common.agree")),
                    BuildHandleAction(applicationId, kind, "reject", Framework.Lang.T("common.decline")),
                }
            };
        }

        /// <summary>构建单个审批按钮动作（team_apply_handle）</summary>
        private static NotificationAction BuildHandleAction(string applicationId, string kind, string decision, string label)
        {
            return new NotificationAction
            {
                Type = "team_apply_handle",
                Label = label,
                Payload = new Dictionary<string, object>
                {
                    ["applicationId"] = applicationId,
                    ["kind"] = kind,
                    ["decision"] = decision
                }
            };
        }

        /// <summary>
        /// 入队或重推：服务端「重新通知」（如重复提交加入申请）时，无论旧条目当前是否还在中心，
        /// 都先彻底清掉去重痕迹（本次运行已入队标记 _seen + 本地持久化已读标记 + 中心旧条目）再重新入队，
        /// 保证重通知一定能再弹卡片、再亮红点。（旧实现仅在中心还有该条时才清 _seen，
        /// 若该条已被清空/审批移除但仍在 _seen 中，Enqueue 会撞 _seen 提前 return，导致重推被静默吞掉。）
        /// </summary>
        private void EnqueueOrRepush(NotificationPayload payload)
        {
            NotificationService.Instance.ForgetReadMarker(payload.Id);
            NotificationService.Instance.RemoveById(payload.Id);
            lock (_seen) _seen.Remove(payload.Id);
            Debug.WriteLine($"[TeamNotify] repush team notification: {payload.Id}");
            Enqueue(payload);
        }

        /// <summary>入队（按通知 ID 去重；已在通知中心或本次运行内已入队过则跳过）</summary>
        private void Enqueue(NotificationPayload payload)
        {
            lock (_seen)
            {
                if (_seen.Contains(payload.Id)) return;
                _seen.Add(payload.Id);
            }

            if (NotificationService.Instance.ContainsId(payload.Id)) return;

            NotificationService.Instance.Enqueue(payload);
            Debug.WriteLine($"[TeamNotify] enqueued team notification: {payload.Id} (mode={payload.DisplayMode})");
        }

        #endregion
    }
}
