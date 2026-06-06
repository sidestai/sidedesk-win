using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SideDesk
{
    /// <summary>
    /// 每隔 3 秒判断一次当前前台窗口是否为微信，若是则只截取该窗口区域
    /// （仅在内存中处理，不落盘保存图像），并在坐标变化时将时间与坐标写入日志。
    /// </summary>
    public class ScreenCaptureLogger : IDisposable
    {
        private const int IntervalSeconds = 3;

        // 微信相关的进程名（老版 WeChat / 新版 Weixin）
        private static readonly string[] WeChatProcessNames = new[] { "wechat", "weixin" };

        private Timer _timer;
        private readonly string _logFolder;
        private string _currentLogFile;
        private DateTime _currentDate;
        private readonly object _lock = new object();
        private bool _isRunning;
        private string _lastCoordKey;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private static Rectangle GetVirtualScreenBounds()
        {
            return new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        public ScreenCaptureLogger()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logFolder = Path.Combine(appData, "SideDesk", "ScreenCaptureLogs");
            if (!Directory.Exists(_logFolder))
            {
                Directory.CreateDirectory(_logFolder);
            }
            UpdateLogFilePath();
        }

        private void UpdateLogFilePath()
        {
            DateTime now = DateTime.Now;
            if (_currentDate.Date != now.Date || string.IsNullOrEmpty(_currentLogFile))
            {
                _currentDate = now.Date;
                _currentLogFile = Path.Combine(_logFolder, $"screenlog_{_currentDate:yyyyMMdd}.txt");
            }
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            // 立即执行一次，随后每 3 秒执行一次
            SafeCaptureAndLog();
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(IntervalSeconds));
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnTimerTick(object state)
        {
            SafeCaptureAndLog();
        }

        private void SafeCaptureAndLog()
        {
            try
            {
                CaptureAndLog();
            }
            catch
            {
                // 忽略单次采集异常，保持后台运行
            }
        }

        /// <summary>
        /// 仅当前台窗口为微信时，截取该窗口所在区域到内存 Bitmap，
        /// 并在坐标变化时写入日志。Bitmap 在方法结束时由 using 释放，不落盘。
        /// </summary>
        private void CaptureAndLog()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
            {
                return;
            }

            // 判断前台窗口是否属于微信进程
            string processName = GetProcessName(hwnd);
            if (!IsWeChatProcess(processName))
            {
                return;
            }

            if (!GetWindowRect(hwnd, out RECT rect))
            {
                return;
            }

            int winX = rect.Left;
            int winY = rect.Top;
            int winW = rect.Right - rect.Left;
            int winH = rect.Bottom - rect.Top;
            if (winW <= 0 || winH <= 0)
            {
                return;
            }

            // 与虚拟屏幕相交，避免最小化/离屏时无效截图
            Rectangle vs = GetVirtualScreenBounds();
            int ix1 = Math.Max(winX, vs.X);
            int iy1 = Math.Max(winY, vs.Y);
            int ix2 = Math.Min(winX + winW, vs.X + vs.Width);
            int iy2 = Math.Min(winY + winH, vs.Y + vs.Height);
            int capW = ix2 - ix1;
            int capH = iy2 - iy1;
            if (capW <= 0 || capH <= 0)
            {
                return;
            }

            var sbTitle = new StringBuilder(512);
            GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString().Replace("\t", " ").Replace("\n", " ").Replace("\r", " ");

            // 仅截取当前窗口可见区域到内存位图
            using (var bitmap = new Bitmap(capW, capH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(ix1, iy1, 0, 0, new Size(capW, capH), CopyPixelOperation.SourceCopy);
                }

                // 基于窗口截图做一次简单采样分析（平均颜色），用于日志记录
                var avg = SampleAverageColor(bitmap);

                // 坐标唯一键（进程+标题+矩形），未变化则跳过日志
                string coordKey = $"{processName}|{title}|{winX},{winY},{winW}x{winH}";
                if (coordKey == _lastCoordKey)
                {
                    return;
                }
                _lastCoordKey = coordKey;

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                lock (_lock)
                {
                    UpdateLogFilePath();
                    string logLine = string.Format(
                        "{0}\tProcess={1}\tTitle=[{2}]\tHwnd=0x{3:X}\tRect=(X={4},Y={5},W={6},H={7})\tCapture=({8}x{9})\tAvgColor=#{10:X2}{11:X2}{12:X2}",
                        timestamp,
                        processName,
                        title,
                        hwnd.ToInt64(),
                        winX, winY, winW, winH,
                        capW, capH,
                        avg.R, avg.G, avg.B);
                    File.AppendAllText(_currentLogFile, logLine + Environment.NewLine);
                }
            }
        }

        private static string GetProcessName(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                {
                    return string.Empty;
                }
                using (var p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsWeChatProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return false;
            }
            string lower = processName.ToLowerInvariant();
            foreach (var n in WeChatProcessNames)
            {
                if (lower == n)
                {
                    return true;
                }
            }
            return false;
        }

        private struct AvgColor
        {
            public byte R;
            public byte G;
            public byte B;
        }

        /// <summary>
        /// 在内存位图上做 10x10 网格采样，计算平均颜色。
        /// </summary>
        private static AvgColor SampleAverageColor(Bitmap bitmap)
        {
            var result = new AvgColor();
            const int sampleGrid = 10;
            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            for (int iy = 0; iy < sampleGrid; iy++)
            {
                int py = (int)((iy + 0.5) * bitmap.Height / sampleGrid);
                if (py < 0 || py >= bitmap.Height) continue;
                for (int ix = 0; ix < sampleGrid; ix++)
                {
                    int px = (int)((ix + 0.5) * bitmap.Width / sampleGrid);
                    if (px < 0 || px >= bitmap.Width) continue;
                    Color c = bitmap.GetPixel(px, py);
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    count++;
                }
            }
            if (count > 0)
            {
                result.R = (byte)(sumR / count);
                result.G = (byte)(sumG / count);
                result.B = (byte)(sumB / count);
            }
            return result;
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
