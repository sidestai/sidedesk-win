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
        // 优先适配宿主主题（避免固定色在深/浅主题下看不清）
        ApplyHostTheme();

        // 初始化 CPU 计数器
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // 首次调用返回 0，需预热
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemCleaner] CPU counter init failed: {ex.Message}");
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

    /// <summary>
    /// 适配宿主主题：插件控件寄宿在 SideDesk 窗口内，可沿视觉树找到应用级主题画刷，
    /// 文字/卡片背景/进度条轨道全部跟随主题，避免固定颜色与当前主题背景对比度过低；
    /// 找不到资源时（如设计器预览）回退到深色硬编码值。
    /// </summary>
    private void ApplyHostTheme()
    {
        var main = TryFindResource("BrushTextMain") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.WhiteSmoke;
        var muted = TryFindResource("BrushTextMuted") as System.Windows.Media.Brush
            ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        var cardBG = TryFindResource("BrushInputBG") as System.Windows.Media.Brush
            ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var track = TryFindResource("BrushBorder") as System.Windows.Media.Brush
            ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        // 全部文字默认用次要色，再把标题与数值改为主色
        foreach (var tb in FindTextBlocks(this)) tb.Foreground = muted;
        foreach (var tb in new[] { txtMemTitle, txtCpuTitle, txtTotalMemory, txtUsedMemory, txtAvailMemory, txtMemoryPercent, txtCpuPercent, txtCpuCores })
            tb.Foreground = main;

        memoryCard.Background = cardBG;
        cpuCard.Background = cardBG;
        memoryBar.Background = track;
        cpuBar.Background = track;
    }

    /// <summary>递归遍历视觉树找出所有 TextBlock</summary>
    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb) yield return tb;
            foreach (var sub in FindTextBlocks(child)) yield return sub;
        }
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
            Debug.WriteLine($"[SystemCleaner] failed to refresh system info: {ex.Message}");
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
            Debug.WriteLine($"[SystemCleaner] CPU read failed: {ex.Message}");
        }
    }

    /// <summary>清理内存按钮点击</summary>
    private void BtnCleanMemory_Click(object sender, RoutedEventArgs e)
    {
        btnCleanMemory.IsEnabled = false;
        txtCleanResult.Text = SideDesk.Framework.Lang.T("systemCleaner.cleaningInProgress");

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
                Debug.WriteLine($"[SystemCleaner] cleanup error: {ex.Message}");
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
                    txtCleanResult.Text = SideDesk.Framework.Lang.TF("systemCleaner.freedMemoryAmount", $"{freedMB:F0}");
                else
                    txtCleanResult.Text = SideDesk.Framework.Lang.T("systemCleaner.memoryStateGood");

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
