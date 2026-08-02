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

        /// <summary>通知中心历史记录（最新在前）</summary>
        public List<NotificationPayload> History { get; } = new();

        /// <summary>未读通知数量</summary>
        public int UnreadCount
        {
            get { lock (_lock) { return History.Count(h => !h.IsRead); } }
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
                Debug.WriteLine($"[Notification] silent 通知已存入通知中心: {payload.Title}");
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
                            Debug.WriteLine($"[Notification] 队列中同组通知已替换: group={payload.GroupId}");
                            _queue[existingIndex] = payload;
                            SortQueue();
                            return;
                        }
                    }

                    // 按优先级插入队列
                    _queue.Add(payload);
                    SortQueue();
                    Debug.WriteLine($"[Notification] 入队: {payload.Title} (priority={payload.Priority}, 队列长度={_queue.Count})");
                }
            }

            // 事件在锁外触发，避免与 UI 线程死锁
            if (isGroupUpdate)
            {
                Debug.WriteLine($"[Notification] 同组通知正在显示，原地更新: group={payload.GroupId}");
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
            Debug.WriteLine($"[Notification] 通知已消失: {payload.Title}");
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
            Debug.WriteLine($"[Notification] 稍后提醒: {payload.Title}, {minutes} 分钟后");

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

                Debug.WriteLine($"[Notification] 显示通知: {payload.Title} (mode={payload.DisplayMode})");
                try { NotificationReady?.Invoke(payload); }
                catch (Exception ex) { Debug.WriteLine($"[Notification] NotificationReady 处理异常: {ex.Message}"); }
            }
        }

        /// <summary>添加到通知中心历史（最新在前，超出上限移除最旧的）</summary>
        private void AddToHistory(NotificationPayload payload)
        {
            lock (_lock)
            {
                History.Insert(0, payload);
                while (History.Count > MaxHistoryCount)
                    History.RemoveAt(History.Count - 1);
            }
            HistoryChanged?.Invoke();
        }

        #endregion
    }
}
