using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace SideDesk.Services;

/// <summary>系统实时指标快照：CPU 占用率、物理内存、网络上下行速率。</summary>
public class SystemMetrics
{
    /// <summary>CPU 占用率 0~100；小于 0 表示尚未采到首个有效样本。</summary>
    public double CpuPercent;
    /// <summary>物理内存占用百分比 0~100（来自系统 GlobalMemoryStatusEx.dwMemoryLoad）。</summary>
    public int MemoryPercent;
    /// <summary>物理内存总量（字节）。</summary>
    public ulong MemoryTotalBytes;
    /// <summary>物理内存已用（字节）= 总量 - 可用。</summary>
    public ulong MemoryUsedBytes;
    /// <summary>全部已启用非回环网卡合计下行速率（字节/秒）。</summary>
    public double NetDownBytesPerSec;
    /// <summary>全部已启用非回环网卡合计上行速率（字节/秒）。</summary>
    public double NetUpBytesPerSec;
}

/// <summary>
/// 系统指标采样服务（进程级单例）：后台线程每 2 秒采集 CPU / 内存 / 网络速率并通过 <see cref="MetricsChanged"/> 广播。
/// 全部基于纯 Win32 P/Invoke（GetSystemTimes / GlobalMemoryStatusEx）与框架内置 NetworkInterface 实现，
/// 不依赖 System.Diagnostics.PerformanceCounter（主工程未引用该 NuGet 包）。
/// 事件在后台线程触发，订阅方（UI）需自行切回 Dispatcher。
/// </summary>
public sealed class SystemMetricsService : IDisposable
{
    private static readonly Lazy<SystemMetricsService> _instance = new(() => new SystemMetricsService());
    /// <summary>全局单例。</summary>
    public static SystemMetricsService Instance => _instance.Value;

    /// <summary>指标更新事件（后台线程触发；UI 侧需 Dispatcher 切回）。</summary>
    public event Action<SystemMetrics>? MetricsChanged;

    /// <summary>最近一次采样快照（供卡片首次渲染即时取值，避免空窗）。</summary>
    public SystemMetrics? Last { get; private set; }

    private readonly Timer _timer;
    private int _sampling; // Interlocked 防重入（网络枚举偶发耗时，避免回调叠跑）

    // CPU：GetSystemTimes 返回 100ns 计数，需要相邻两次样本求差
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuPrev;

    // 网络：累计字节计数 + 时间戳，求差得速率
    private long _prevRecv, _prevSent;
    private DateTime _prevNetTime;
    private bool _hasNetPrev;

    private const int IntervalMs = 2000;

    private SystemMetricsService()
    {
        // 首次 500ms 后出样（此时 CPU 尚无历史样本，显示 --；第二个样本起才有 CPU 值）
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(IntervalMs));
    }

    private void Sample()
    {
        // 单实例、跳过叠跑：上一轮未回来则放弃本轮
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var m = new SystemMetrics { CpuPercent = -1 };

            // ---- CPU ----
            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                ulong idle = idleFt.ToULong(), kernel = kernelFt.ToULong(), user = userFt.ToULong();
                if (_hasCpuPrev)
                {
                    ulong dIdle = idle - _prevIdle;
                    ulong dKernel = kernel - _prevKernel; // 已含 idle
                    ulong dUser = user - _prevUser;
                    ulong total = dKernel + dUser;
                    if (total > 0)
                        m.CpuPercent = (total - dIdle) * 100.0 / total; // busy% = (总-空闲)/总
                }
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user; _hasCpuPrev = true;
            }

            // ---- 内存 ----
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                m.MemoryPercent = (int)mem.dwMemoryLoad;
                m.MemoryTotalBytes = mem.ullTotalPhys;
                m.MemoryUsedBytes = mem.ullTotalPhys - mem.ullAvailPhys;
            }

            // ---- 网络（合计所有已启用非回环网卡）----
            long recv = 0, sent = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var s = ni.GetIPStatistics();
                    if (s.BytesReceived < 0 || s.BytesSent < 0) continue; // 部分虚拟网卡不支持，返回 -1
                    recv += s.BytesReceived;
                    sent += s.BytesSent;
                }
                catch { /* 个别网卡枚举可能抛异常，跳过 */ }
            }
            var now = DateTime.UtcNow;
            if (_hasNetPrev)
            {
                double sec = (now - _prevNetTime).TotalSeconds;
                if (sec > 0.05)
                {
                    m.NetDownBytesPerSec = Math.Max(0, recv - _prevRecv) / sec;
                    m.NetUpBytesPerSec = Math.Max(0, sent - _prevSent) / sec;
                }
            }
            _prevRecv = recv; _prevSent = sent; _prevNetTime = now; _hasNetPrev = true;

            Last = m;
            MetricsChanged?.Invoke(m);
        }
        catch { /* 采样失败静默，下轮再试 */ }
        finally { Interlocked.Exchange(ref _sampling, 0); }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
    }

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public ulong ToULong() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    #endregion
}
