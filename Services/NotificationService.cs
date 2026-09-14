using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Threading;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 通知服务（单例）：统一管理通知的入队、限流、分组合并、进度更新、稍后提醒和通知中心历史。
    /// 可从任意线程调用 Enqueue；UI 层通过事件订阅后在 Dispatcher 中渲染。
    /// </summary>
    public sealed class NotificationService
    {
        private static readonly Lazy<NotificationService> _instance = new(() => new NotificationService());
        public static NotificationService Instance => _instance.Value;

        /// <summary>最大同时显示的通知数量</summary>
        public const int MaxConcurrent = 3;

        /// <summary>通知中心最大保留条数</summary>
        private const int MaxHistoryCount = 100;

        private readonly object _lock = new();

        /// <summary>等待显示的队列（按 priority 降序 + timestamp 升序排列）</summary>
        private readonly List<NotificationPayload> _queue = new();

        /// <summary>正在显示的通知 ID 集合</summary>
        private readonly HashSet<string> _activeIds = new();

        /// <summary>正在显示的通知 group_id 集合（用于分组原地更新判断）</summary>
        private readonly HashSet<string> _activeGroupIds = new();

        /// <summary>稍后提醒的定时器列表（防止被 GC 回收）</summary>
        private readonly List<Timer> _snoozeTimers = new();

        /// <summary>App 尚未就绪时的兜底已读 ID 列表（避免启动极早期时序下已读状态丢失）</summary>
        private static readonly List<string> _fallbackReadIds = new();

        /// <summary>本地持久化的已读通知 ID（存于 AppSettings，重启不丢）</summary>
        private static List<string> ReadIds =>
            (System.Windows.Application.Current as App)?.AppSettings.ReadNotificationIds ?? _fallbackReadIds;

        /// <summary>通知中心历史记录（最新在前）</summary>
        public List<NotificationPayload> History { get; } = new();

        /// <summary>未读通知数量（陈旧超一个月的未读不计入红点）</summary>
        public int UnreadCount
        {
            get { lock (_lock) { return History.Count(h => h.ShowUnread); } }
        }

        /// <summary>有新通知需要显示时触发（参数：通知载荷；订阅者需在 UI 线程渲染）</summary>
        public event Action<NotificationPayload>? NotificationReady;

        /// <summary>已显示的同组通知需要原地更新时触发（进度/内容变更）</summary>
        public event Action<NotificationPayload>? NotificationUpdated;

        /// <summary>通知中心数据变更时触发（新增/已读/清空）</summary>
        public event Action? HistoryChanged;

        private NotificationService() { }

        /// <summary>
        /// 通知入队（线程安全）。
        /// silent 模式仅存入通知中心；同组通知正在显示时原地更新；队列中同组通知替换为最新一条。
        /// </summary>
        public void Enqueue(NotificationPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.Title)) return;

            // 加入通知中心历史
            AddToHistory(payload);

            // silent 模式：仅进入通知中心，不弹出
            if (payload.DisplayMode == NotificationDisplayMode.Silent)
            {
                Debug.WriteLine($"[Notification] silent notification stored in notification center: {payload.Title}");
                return;
            }

            bool isGroupUpdate = false;
            lock (_lock)
            {
                // 分组处理：同组通知正在显示 → 原地更新
                if (!string.IsNullOrEmpty(payload.GroupId) && _activeGroupIds.Contains(payload.GroupId))
                {
                    isGroupUpdate = true;
                }
                else
                {
                    // 分组处理：队列中已有同组通知 → 替换为最新一条
                    if (!string.IsNullOrEmpty(payload.GroupId))
                    {
                        var existingIndex = _queue.FindIndex(q => q.GroupId == payload.GroupId);
                        if (existingIndex >= 0)
                        {
                            Debug.WriteLine($"[Notification] same-group notification replaced in queue: group={payload.GroupId}");
                            _queue[existingIndex] = payload;
                            SortQueue();
                            return;
                        }
                    }

                    // 按优先级插入队列
                    _queue.Add(payload);
                    SortQueue();
                    Debug.WriteLine($"[Notification] enqueued: {payload.Title} (priority={payload.Priority}, queueLength={_queue.Count})");
                }
            }

            // 事件在锁外触发，避免与 UI 线程死锁
            if (isGroupUpdate)
            {
                Debug.WriteLine($"[Notification] same-group notification is showing, updating in place: group={payload.GroupId}");
                NotificationUpdated?.Invoke(payload);
                return;
            }

            ProcessQueue();
        }

        /// <summary>
        /// 更新指定分组的通知进度（快捷通道，对应 notification_progress 事件）。
        /// 若该组通知正在显示则原地更新进度条；若在队列中则更新载荷；否则忽略。
        /// </summary>
        public void UpdateProgress(string groupId, int? progress, string? body = null)
        {
            if (string.IsNullOrEmpty(groupId)) return;

            bool isActive = false;
            lock (_lock)
            {
                isActive = _activeGroupIds.Contains(groupId);

                // 在队列中 → 更新载荷
                if (!isActive)
                {
                    var queued = _queue.FirstOrDefault(q => q.GroupId == groupId);
                    if (queued != null)
                    {
                        queued.Progress = progress;
                        if (body != null) queued.Body = body;
                    }
                }
            }

            // 正在显示 → 事件在锁外触发，避免与 UI 线程死锁
            if (isActive)
            {
                var update = new NotificationPayload
                {
                    GroupId = groupId,
                    Progress = progress,
                    Body = body
                };
                NotificationUpdated?.Invoke(update);
            }
        }

        /// <summary>
        /// 通知已被 UI 关闭/超时消失时调用，释放显示槽位并处理下一个。
        /// </summary>
        public void OnNotificationDismissed(NotificationPayload payload)
        {
            lock (_lock)
            {
                _activeIds.Remove(payload.Id);
                if (!string.IsNullOrEmpty(payload.GroupId))
                    _activeGroupIds.Remove(payload.GroupId);
            }
            // 本地通知（无服务端 ID、带免打扰标识，如欢迎引导）：气泡收起即视为已阅，
            // 本地标记已读并持久化，避免重启后同条通知再次亮红点；
            // 同时记录免打扰：重启后气泡不再弹，但中心仍以已读条目保留直到用户清空
            if (payload.ServerId == null && !string.IsNullOrEmpty(payload.NoticeKey))
            {
                MarkRead(payload.Id);
                if (System.Windows.Application.Current is App app)
                {
                    app.AppSettings.DismissNotice(payload.NoticeKey);
                    app.AppSettings.Save();
                }
            }
            Debug.WriteLine($"[Notification] notification dismissed: {payload.Title}");
            ProcessQueue();
        }

        /// <summary>
        /// 稍后提醒：到期后重新入队显示。
        /// </summary>
        /// <param name="payload">原通知载荷</param>
        /// <param name="minutes">延迟分钟数（默认 5）</param>
        public void Snooze(NotificationPayload payload, double minutes = 5)
        {
            if (minutes <= 0) minutes = 5;
            Debug.WriteLine($"[Notification] snoozed: {payload.Title}, in {minutes} minutes");

            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (_lock) { if (timer != null) _snoozeTimers.Remove(timer); }
                // 重新入队（生成新 ID 避免冲突）
                payload.Id = Guid.NewGuid().ToString("N");
                payload.Timestamp = DateTime.Now;
                Enqueue(payload);
            }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

            lock (_lock) { _snoozeTimers.Add(timer); }
        }

        /// <summary>标记所有通知为已读</summary>
        public void MarkAllRead()
        {
            lock (_lock)
            {
                foreach (var item in History)
                    item.IsRead = true;
            }
            HistoryChanged?.Invoke();
        }

        /// <summary>标记单条通知为已读（点过即已读；触发 HistoryChanged，红点随之更新）</summary>
        public void MarkRead(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            bool changed = false;
            string? noticeKey = null;
            lock (_lock)
            {
                var item = History.FirstOrDefault(h => h.Id == id);
                if (item != null && !item.IsRead)
                {
                    item.IsRead = true;
                    changed = true;
                    noticeKey = item.NoticeKey;
                }
            }
            if (changed)
            {
                PersistReadId(id);
                // 本地通知的 Id 每次启动随机，需同时按 NoticeKey 落标记才能跨重启生效
                if (!string.IsNullOrEmpty(noticeKey)) PersistReadId(noticeKey);
                HistoryChanged?.Invoke();
            }
        }

        /// <summary>指定 ID 是否已在本地持久化已读记录中（供同步服务与服务端状态对齐）</summary>
        public bool IsLocallyRead(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock) { return ReadIds.Contains(id); }
        }

        /// <summary>持久化已读 ID（插入序，封顶 500 条防止配置无限增长）</summary>
        private static void PersistReadId(string id)
        {
            try
            {
                var list = ReadIds;
                if (list.Contains(id)) return;
                list.Add(id);
                while (list.Count > 500) list.RemoveAt(0);
                (System.Windows.Application.Current as App)?.AppSettings.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Notification] persist read id failed: {ex.Message}");
            }
        }

        /// <summary>按 ID 查找历史条目（返回引用，供同步服务对齐服务端已读状态）</summary>
        public NotificationPayload? FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) { return History.FirstOrDefault(h => h.Id == id); }
        }

        /// <summary>服务端重通知 bump：指定条目重置为未读、刷新时间并置顶（触发 HistoryChanged，红点随之更新）；同时清掉本地持久化已读标记，避免重启后被旧标记拦回已读</summary>
        public void Bump(string id, DateTime timestamp)
        {
            lock (_lock)
            {
                var item = History.FirstOrDefault(h => h.Id == id);
                if (item == null) return;
                item.IsRead = false;
                item.Timestamp = timestamp;
                History.Remove(item);
                History.Insert(0, item);
                ReadIds.Remove(id);
            }
            (System.Windows.Application.Current as App)?.AppSettings.Save();
            HistoryChanged?.Invoke();
        }

        /// <summary>清除指定 ID 的本地持久化已读标记（服务端重通知需恢复未读，不能被旧标记拦回已读）</summary>
        public void ForgetReadMarker(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            bool removed;
            lock (_lock) { removed = ReadIds.Remove(id); }
            if (removed) (System.Windows.Application.Current as App)?.AppSettings.Save();
        }

        /// <summary>
        /// 按 ID 移除通知中心里的单条历史（业务已处理完毕时调用，避免留下过期未读）。
        /// 返回是否命中；未命中不触发 HistoryChanged。
        /// </summary>
        public bool RemoveById(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            bool removed;
            lock (_lock)
            {
                removed = History.RemoveAll(h => h.Id == id) > 0;
            }
            if (removed)
            {
                Debug.WriteLine($"[Notification] history item removed: {id}");
                HistoryChanged?.Invoke();
            }
            return removed;
        }

        /// <summary>通知中心是否已存在指定 ID 的历史（供外部去重，避免同一事件重复入队）</summary>
        public bool ContainsId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock) { return History.Any(h => h.Id == id); }
        }

        /// <summary>清空通知中心历史</summary>
        public void ClearHistory()
        {
            lock (_lock) { History.Clear(); }
            HistoryChanged?.Invoke();
        }

        #region 内部逻辑

        /// <summary>队列排序：priority 降序，同优先级按 timestamp 升序（先到先显示）</summary>
        private void SortQueue()
        {
            var sorted = _queue
                .OrderByDescending(q => q.Priority)
                .ThenBy(q => q.Timestamp)
                .ToList();
            _queue.Clear();
            _queue.AddRange(sorted);
        }

        /// <summary>从队列中取出通知显示（受 MaxConcurrent 限流）</summary>
        private void ProcessQueue()
        {
            List<NotificationPayload> toShow = new();

            lock (_lock)
            {
                while (_queue.Count > 0 && _activeIds.Count < MaxConcurrent)
                {
                    var next = _queue[0];
                    _queue.RemoveAt(0);
                    _activeIds.Add(next.Id);
                    if (!string.IsNullOrEmpty(next.GroupId))
                        _activeGroupIds.Add(next.GroupId);
                    toShow.Add(next);
                }
            }

            foreach (var payload in toShow)
            {
                // 播放提示音（异步，不阻塞）
                if (payload.Sound)
                {
                    try { SystemSounds.Asterisk.Play(); } catch { }
                }

                Debug.WriteLine($"[Notification] showing notification: {payload.Title} (mode={payload.DisplayMode})");
                try { NotificationReady?.Invoke(payload); }
                catch (Exception ex) { Debug.WriteLine($"[Notification] NotificationReady handling error: {ex.Message}"); }
            }
        }

        /// <summary>添加到通知中心历史（最新在前，超出上限移除最旧的）</summary>
        private void AddToHistory(NotificationPayload payload)
        {
            lock (_lock)
            {
                // 已读状态恢复：本地持久化已读的 ID/NoticeKey 重启/重新同步后直接以已读入历史（防红点复现）
                if (!payload.IsRead && (ReadIds.Contains(payload.Id)
                    || (!string.IsNullOrEmpty(payload.NoticeKey) && ReadIds.Contains(payload.NoticeKey))))
                    payload.IsRead = true;
                History.Insert(0, payload);
                while (History.Count > MaxHistoryCount)
                    History.RemoveAt(History.Count - 1);
            }
            HistoryChanged?.Invoke();
        }

        #endregion
    }
}
