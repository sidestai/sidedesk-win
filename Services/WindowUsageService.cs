using System;
using SideDesk.Windows;

namespace SideDesk.Services
{
    /// <summary>
    /// 窗口使用时长采集服务：按需启动 WindowLogger，后台每 5 秒轮询前台窗口并写入日志，
    /// 供 WindowUsageStats / WindowUsagePanel 统计与展示。仅在“使用时长分析”开启时运行。
    /// </summary>
    public sealed class WindowUsageService
    {
        private static readonly Lazy<WindowUsageService> _instance = new(() => new WindowUsageService());
        public static WindowUsageService Instance => _instance.Value;

        private readonly object _lock = new();
        private WindowLogger? _logger;

        private WindowUsageService() { }

        /// <summary>采集器是否正在运行</summary>
        public bool IsRunning { get; private set; }

        /// <summary>启动采集（幂等）</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (IsRunning) return;
                _logger = new WindowLogger();
                _logger.Start();
                IsRunning = true;
            }
        }

        /// <summary>停止采集并释放资源（幂等）</summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!IsRunning) return;
                try { _logger?.Dispose(); } catch { }
                _logger = null;
                IsRunning = false;
            }
        }

        /// <summary>根据开关状态启动/停止采集</summary>
        public void ApplyEnabled(bool enabled)
        {
            if (enabled) Start();
            else Stop();
        }
    }
}
