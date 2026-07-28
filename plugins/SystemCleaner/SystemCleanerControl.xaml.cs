using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SystemCleaner;

/// <summary>
/// SystemCleaner 插件 UI 控件
/// 提供内存/CPU 实时监控和内存清理功能
/// </summary>
public partial class SystemCleanerControl : UserControl
{
    private DispatcherTimer? _timer;
    private PerformanceCounter? _cpuCounter;

    public SystemCleanerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 初始化 CPU 计数器
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // 首次调用返回 0，需预热
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemCleaner] CPU 计数器初始化失败: {ex.Message}");
        }

        txtCpuCores.Text = Environment.ProcessorCount.ToString();

        // 定时刷新（每 2 秒）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 立即刷新一次
        RefreshSystemInfo();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _cpuCounter?.Dispose();
        _cpuCounter = null;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        RefreshSystemInfo();
    }

    /// <summary>刷新内存和 CPU 数据</summary>
    private void RefreshSystemInfo()
    {
        try
        {
            RefreshMemoryInfo();
            RefreshCpuInfo();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemCleaner] 刷新系统信息失败: {ex.Message}");
        }
    }

    /// <summary>获取并显示内存信息</summary>
    private void RefreshMemoryInfo()
    {
        var status = new MEMORYSTATUSEX();
        status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

        if (GlobalMemoryStatusEx(ref status))
        {
            double totalGB = status.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGB = status.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGB = totalGB - availGB;
            double percent = (usedGB / totalGB) * 100;

            txtTotalMemory.Text = $"{totalGB:F1} GB";
            txtUsedMemory.Text = $"{usedGB:F1} GB";
            txtAvailMemory.Text = $"{availGB:F1} GB";
            txtMemoryPercent.Text = $"{percent:F1}%";
            memoryBar.Value = percent;

            // 使用率过高时变红色警告
            if (percent > 85)
                memoryBar.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF5350"));
            else
                memoryBar.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4FC3F7"));
        }
    }

    /// <summary>获取并显示 CPU 信息</summary>
    private void RefreshCpuInfo()
    {
        if (_cpuCounter == null) return;

        try
        {
            float cpuUsage = _cpuCounter.NextValue();
            txtCpuPercent.Text = $"{cpuUsage:F1}%";
            cpuBar.Value = cpuUsage;

            if (cpuUsage > 85)
                cpuBar.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF5350"));
            else
                cpuBar.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#81C784"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemCleaner] CPU 读取失败: {ex.Message}");
        }
    }

    /// <summary>清理内存按钮点击</summary>
    private void BtnCleanMemory_Click(object sender, RoutedEventArgs e)
    {
        btnCleanMemory.IsEnabled = false;
        txtCleanResult.Text = "正在清理...";

        // 记录清理前的可用内存
        var beforeStatus = new MEMORYSTATUSEX();
        beforeStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref beforeStatus);
        long beforeAvail = (long)beforeStatus.ullAvailPhys;

        // 执行内存清理
        Task.Run(() =>
        {
            try
            {
                // 1. 清空工作集（将当前进程的物理内存页换出到页面文件）
                var process = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(process.Handle, -1, -1);

                // 2. 调用 EmptyWorkingSet 对所有可换出页面进行修剪
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);

                // 3. 触发 GC 回收托管内存
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemCleaner] 清理异常: {ex.Message}");
            }
        }).ContinueWith(_ =>
        {
            // 回到 UI 线程更新结果
            Dispatcher.Invoke(() =>
            {
                var afterStatus = new MEMORYSTATUSEX();
                afterStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                GlobalMemoryStatusEx(ref afterStatus);
                long afterAvail = (long)afterStatus.ullAvailPhys;

                long freedBytes = afterAvail - beforeAvail;
                double freedMB = freedBytes / (1024.0 * 1024.0);

                if (freedMB > 0)
                    txtCleanResult.Text = $"✅ 已释放约 {freedMB:F0} MB 内存";
                else
                    txtCleanResult.Text = "ℹ 当前内存状态良好，无需额外释放";

                btnCleanMemory.IsEnabled = true;
                RefreshSystemInfo();
            });
        });
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
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
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, long dwMinimumWorkingSetSize, long dwMaximumWorkingSetSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    #endregion
}
