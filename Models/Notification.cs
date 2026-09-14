using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SideDesk.Models
{
    /// <summary>通知显示模式</summary>
    public enum NotificationDisplayMode
    {
        /// <summary>横幅：在通知栏内显示卡片，超时自动消失</summary>
        Banner,
        /// <summary>弹出：模态对话框，需用户操作</summary>
        Alert,
        /// <summary>静默：仅进入通知中心，不弹出</summary>
        Silent
    }

    /// <summary>通知优先级（越高越先显示）</summary>
    public enum NotificationPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>通知动作类型</summary>
    public enum NotificationActionType
    {
        OpenUrl,
        OpenWebView,
        OpenSettings,
        OpenFile,
        ShowFileLocation,
        ShowMessagebox,
        RunCommand,
        Snooze,
        InputReply,
        Dismiss,
        /// <summary>团队加入申请/邀请的审批（同意或拒绝），payload: applicationId / kind / decision</summary>
        TeamApplyHandle,
        Unknown
    }

    /// <summary>
    /// 通知动作定义：type + label + payload。
    /// payload 中可包含 url / path / tab / filePath / command / runInBackground /
    /// snoozeInterval / placeholder / replyActionId / buttons /
    /// applicationId / kind / decision 等字段。
    /// </summary>
    public class NotificationAction
    {
        public string Type { get; set; } = "";
        public string? Label { get; set; }
        public Dictionary<string, object>? Payload { get; set; }

        /// <summary>解析后的动作类型枚举</summary>
        public NotificationActionType ActionType => ParseActionType(Type);

        public static NotificationActionType ParseActionType(string? type)
        {
            return type?.Trim().ToLowerInvariant() switch
            {
                "open_url" => NotificationActionType.OpenUrl,
                "open_webview" => NotificationActionType.OpenWebView,
                "open_settings" => NotificationActionType.OpenSettings,
                "open_file" => NotificationActionType.OpenFile,
                "show_file_location" => NotificationActionType.ShowFileLocation,
                "show_messagebox" => NotificationActionType.ShowMessagebox,
                "run_command" => NotificationActionType.RunCommand,
                "snooze" or "remind_later" => NotificationActionType.Snooze,
                "input_reply" => NotificationActionType.InputReply,
                "dismiss" => NotificationActionType.Dismiss,
                "team_apply_handle" => NotificationActionType.TeamApplyHandle,
                _ => NotificationActionType.Unknown
            };
        }

        /// <summary>从 JSON 解析单个动作</summary>
        public static NotificationAction? FromJson(JToken? token)
        {
            if (token == null) return null;
            var type = (string?)token["type"] ?? "";
            if (string.IsNullOrEmpty(type)) return null;

            var action = new NotificationAction { Type = type, Label = (string?)token["label"] };

            var payloadToken = token["payload"] as JObject;
            if (payloadToken != null)
            {
                action.Payload = new Dictionary<string, object>();
                foreach (var prop in payloadToken.Properties())
                {
                    action.Payload[prop.Name] = prop.Value.Type == JTokenType.Null
                        ? ""
                        : prop.Value.ToObject<object>()!;
                }
            }

            return action;
        }

        /// <summary>获取 payload 中字符串值的便捷方法</summary>
        public string GetPayloadString(string key, string fallback = "")
        {
            if (Payload == null || !Payload.TryGetValue(key, out var val)) return fallback;
            return val?.ToString() ?? fallback;
        }

        /// <summary>获取 payload 中数值的便捷方法</summary>
        public double GetPayloadNumber(string key, double fallback = 0)
        {
            if (Payload == null || !Payload.TryGetValue(key, out var val)) return fallback;
            if (val is double d) return d;
            if (val is long l) return l;
            if (val is int i) return i;
            if (double.TryParse(val?.ToString(), out var parsed)) return parsed;
            return fallback;
        }

        /// <summary>获取 payload 中布尔值的便捷方法</summary>
        public bool GetPayloadBool(string key, bool fallback = false)
        {
            if (Payload == null || !Payload.TryGetValue(key, out var val)) return fallback;
            if (val is bool b) return b;
            if (bool.TryParse(val?.ToString(), out var parsed)) return parsed;
            return fallback;
        }
    }

    /// <summary>
    /// 通知载荷：对应后端 WebSocket 推送的 notification 事件 data 部分。
    /// JSON 字段使用 snake_case 命名。
    /// </summary>
    public class NotificationPayload
    {
        /// <summary>通知唯一 ID（服务端生成或客户端补全 GUID）</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>通知标题（粗体主文案）</summary>
        public string Title { get; set; } = "";
        /// <summary>通知正文（补充说明）</summary>
        public string? Body { get; set; }
        /// <summary>发送时间（不传则用收到时的本地时间）</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
        /// <summary>多少秒后自动消失（0 为常驻）</summary>
        public int Timeout { get; set; } = 8;
        /// <summary>是否允许关闭（✕ 按钮）</summary>
        public bool AllowClose { get; set; } = true;
        /// <summary>优先级</summary>
        public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
        /// <summary>分组 ID（同组只显示最新一条，可直接更新进度）</summary>
        public string? GroupId { get; set; }
        /// <summary>背景颜色（如 #1a1a2e）</summary>
        public string? BackgroundColor { get; set; }
        /// <summary>文字颜色</summary>
        public string? TextColor { get; set; }
        /// <summary>出现时是否播放声音</summary>
        public bool Sound { get; set; }
        /// <summary>进度值 0-100（null 表示不显示进度条）</summary>
        public int? Progress { get; set; }
        /// <summary>显示模式：banner / alert / silent</summary>
        public NotificationDisplayMode DisplayMode { get; set; } = NotificationDisplayMode.Banner;
        /// <summary>通知中的大图（URL 或本地路径）</summary>
        public string? LargeImage { get; set; }
        /// <summary>操作按钮列表</summary>
        public List<NotificationAction>? Buttons { get; set; }
        /// <summary>点击通知主体触发的默认行为</summary>
        public NotificationAction? ClickMain { get; set; }

        /// <summary>是否已读（通知中心使用）</summary>
        public bool IsRead { get; set; }

        /// <summary>服务端通知行 ID（encrypt2 密文）：与 /api/notification/read|clear 同步已读/清空状态；null 表示纯本地通知</summary>
        public string? ServerId { get; set; }

        /// <summary>通知免打扰标识（点击/关闭后记录到 Settings.DismissedNotices，配合 ShouldShowNotice 控制不再出现；null 表示不参与免打扰）</summary>
        public string? NoticeKey { get; set; }

        /// <summary>是否显示未读红点：未读且发送时间在 1 个月内（本地已标记已读的自然不亮；超一个月的陈旧未读也不再亮）</summary>
        public bool ShowUnread => !IsRead && DateTime.Now - Timestamp <= TimeSpan.FromDays(30);

        /// <summary>从 WebSocket JSON data 对象解析通知载荷（snake_case 字段映射）</summary>
        public static NotificationPayload FromJson(JObject data)
        {
            var n = new NotificationPayload();

            n.Id = (string?)data["id"] ?? Guid.NewGuid().ToString("N");
            n.Title = (string?)data["title"] ?? "";
            n.Body = (string?)data["body"] ?? (string?)data["content"];

            // timestamp: 毫秒时间戳，不传则用当前时间
            var ts = data["timestamp"];
            if (ts != null && ts.Type == JTokenType.Integer)
            {
                try { n.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)ts).LocalDateTime; }
                catch { n.Timestamp = DateTime.Now; }
            }

            n.Timeout = (int?)data["timeout"] ?? 8;
            n.AllowClose = (bool?)data["allow_close"] ?? true;

            var priority = ((string?)data["priority"] ?? "normal").ToLowerInvariant();
            n.Priority = priority switch
            {
                "low" => NotificationPriority.Low,
                "high" => NotificationPriority.High,
                "critical" => NotificationPriority.Critical,
                _ => NotificationPriority.Normal
            };

            n.GroupId = (string?)data["group_id"];
            if (string.IsNullOrWhiteSpace(n.GroupId)) n.GroupId = null;

            n.BackgroundColor = (string?)data["background_color"];
            n.TextColor = (string?)data["text_color"];
            n.Sound = (bool?)data["sound"] ?? false;
            n.Progress = (int?)data["progress"];

            var mode = ((string?)data["display_mode"] ?? "banner").ToLowerInvariant();
            n.DisplayMode = mode switch
            {
                "alert" => NotificationDisplayMode.Alert,
                "silent" => NotificationDisplayMode.Silent,
                _ => NotificationDisplayMode.Banner
            };

            n.LargeImage = (string?)data["large_image"];

            // buttons 数组
            if (data["buttons"] is JArray btnArray)
            {
                n.Buttons = new List<NotificationAction>();
                foreach (var btn in btnArray)
                {
                    var action = NotificationAction.FromJson(btn);
                    if (action != null) n.Buttons.Add(action);
                }
                if (n.Buttons.Count == 0) n.Buttons = null;
            }

            // click_main
            n.ClickMain = NotificationAction.FromJson(data["click_main"] ?? data["clickMain"]);

            n.NoticeKey = (string?)data["notice_key"];

            return n;
        }
    }
}
