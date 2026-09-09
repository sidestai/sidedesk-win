using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SideDesk
{
    /// <summary>
    /// AppBar 窗口基类，实现窗口停靠到屏幕边缘并保留屏幕空间
    /// </summary>
    public class AppBarWindow : Window
    {
        #region Configuration

        /// <summary>
        /// 停靠边缘 (Left / Right)
        /// </summary>
        public DockEdge DockSide { get; set; } = DockEdge.Left;

        /// <summary>
        /// AppBar 宽度（逻辑像素 DIP，恒定不随 DPI 缩放；注册给系统的物理宽度 = DIP * 该屏缩放）
        /// </summary>
        public int AppBarWidth { get; set; } = 400;

        /// <summary>
        /// 目标显示器索引 (0 = 主显示器)
        /// </summary>
        public int ScreenIndex { get; set; } = 0;

        /// <summary>
        /// 鼠标悬停时的展开宽度（逻辑像素 DIP，恒定；物理 = DIP * 该屏缩放）
        /// </summary>
        public int ExpandedWidth { get; set; } = 200;

        #endregion

        #region State

        public bool IsAppBarRegistered { get; private set; } = false;

        private int _callbackMessageId;
        private bool _isMoveLocked = false;
        private bool _isPositioning = false;
        private HwndSource? _hwndSource;
        private IntPtr _registeredHwnd = IntPtr.Zero; // 保存注册时的窗口句柄
        private bool _isHoverExpanded = false;
        private bool _allowZOrderChange = false; // 自身主动发起 Z 序变更（全屏让位）时置 true，PreventMoveHook 据此放行

        #endregion

        #region Public Methods

        /// <summary>
        /// 注册 AppBar 并停靠窗口到指定边缘
        /// </summary>
        public void RegisterAppBar()
        {
            if (IsAppBarRegistered) return;

            _hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            if (_hwndSource == null) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            _registeredHwnd = hwnd; // 保存句柄用于关闭时注销

            // 注册回调消息
            _callbackMessageId = NativeMethods.RegisterWindowMessage("SideDesk_AppBarMessage");

            // 注册 AppBar
            var data = CreateAppBarData(hwnd);
            data.uCallbackMessage = _callbackMessageId;
            NativeMethods.SHAppBarMessage(AppBarMessage.ABM_NEW, ref data);
            
            System.Diagnostics.Debug.WriteLine($"[AppBar] ABM_NEW registered - hwnd={hwnd}");
            LogEvent($"AppBarRegistered - hwnd={hwnd}");

            IsAppBarRegistered = true;

            // 设置位置
            PositionAppBar();

            // 添加消息 Hook
            _hwndSource.AddHook(AppBarWndProc);

            // 锁定位置防止拖动
            LockPosition();
        }

        /// <summary>
        /// 注销 AppBar，释放保留的屏幕空间
        /// </summary>
        public void UnregisterAppBar()
        {
            if (!IsAppBarRegistered) return;

            System.Diagnostics.Debug.WriteLine($"[AppBar] UnregisterAppBar called");
            LogEvent("AppBarUnregister");

            UnlockPosition();

            _hwndSource?.RemoveHook(AppBarWndProc);

            // 使用保存的句柄，避免窗口关闭后 Handle 已无效
            var hwnd = _registeredHwnd != IntPtr.Zero ? _registeredHwnd : new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var data = CreateAppBarData(hwnd);
                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_REMOVE, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] ABM_REMOVE called with hwnd={hwnd}");
                LogEvent($"AppBarRemoved - hwnd={hwnd}");
            }

            _registeredHwnd = IntPtr.Zero;
            IsAppBarRegistered = false;
        }

        /// <summary>
        /// 重新计算并应用 AppBar 位置
        /// </summary>
        public void PositionAppBar()
        {
            if (!IsAppBarRegistered) return;
            if (_isPositioning) return; // 防止重入

            try
            {
                _isPositioning = true;

                var hwnd = new WindowInteropHelper(this).Handle;
                var (monitorHandle, monitor) = GetTargetMonitorEx();

                // 使用 rcMonitor（完整显示器区域）计算左右边界
                // 使用 rcWork 的上下边界（避开任务栏）
                var monitorRect = monitor.rcMonitor;
                var workArea = monitor.rcWork;

                // 先取目标屏 DPI：AppBarWidth 为逻辑像素(DIP)恒定，注册给系统的物理宽度 = DIP * 该屏缩放，
                // 保证高 DPI 屏上逻辑宽度不缩小（否则 150% 屏内容/字体被挤压）
                GetMonitorDpi(monitorHandle, out double dpiX, out double dpiY);
                double scaleX = dpiX / 96.0;
                double scaleY = dpiY / 96.0;
                int appBarWidthPhysical = (int)Math.Round(AppBarWidth * scaleX);

                System.Diagnostics.Debug.WriteLine($"[AppBar] Monitor: L={monitorRect.Left} T={monitorRect.Top} R={monitorRect.Right} B={monitorRect.Bottom}");
                System.Diagnostics.Debug.WriteLine($"[AppBar] WorkArea: L={workArea.Left} T={workArea.Top} R={workArea.Right} B={workArea.Bottom}");

                // 计算 AppBar 占用区域（物理像素）
                var data = CreateAppBarData(hwnd);
                data.uEdge = DockSide == DockEdge.Left ? AppBarEdge.ABE_LEFT : AppBarEdge.ABE_RIGHT;

                if (DockSide == DockEdge.Left)
                {
                    data.rc.Left = monitorRect.Left;
                    data.rc.Top = workArea.Top;
                    data.rc.Right = monitorRect.Left + appBarWidthPhysical;
                    data.rc.Bottom = workArea.Bottom;
                }
                else
                {
                    data.rc.Left = monitorRect.Right - appBarWidthPhysical;
                    data.rc.Top = workArea.Top;
                    data.rc.Right = monitorRect.Right;
                    data.rc.Bottom = workArea.Bottom;
                }

                System.Diagnostics.Debug.WriteLine($"[AppBar] Before QUERYPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                // 保存期望矩形：当系统 AppBar 协调失败返回无效矩形时用于回退，
                // 避免窗口停留在初始的居中大尺寸位置
                RECT expectedRc = data.rc;

                // 查询并设置位置
                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_QUERYPOS, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] After QUERYPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_SETPOS, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] After SETPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                // 系统返回无效矩形时回退到期望矩形，确保窗口仍停靠到屏幕边缘
                if (data.rc.Width <= 0 || data.rc.Height <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[AppBar] SETPOS returned invalid rect, falling back to expected rect");
                    LogEvent($"PositionAppBar fallback - expected L={expectedRc.Left} T={expectedRc.Top} R={expectedRc.Right} B={expectedRc.Bottom}");
                    data.rc = expectedRc;
                    // 回退矩形必须重新注册给系统，否则工作区不会为 AppBar 保留空间，
                    // 最大化窗口将占满整屏并覆盖侧边栏（副屏坐标异常时易触发）
                    NativeMethods.SHAppBarMessage(AppBarMessage.ABM_SETPOS, ref data);
                }

                System.Diagnostics.Debug.WriteLine($"[AppBar] DPI: {dpiX}x{dpiY}, Scale: {scaleX}x{scaleY}");

                // 移动窗口到 AppBar 位置（转换为 WPF 逻辑像素）
                double newLeft = data.rc.Left / scaleX;
                double newTop = data.rc.Top / scaleY;
                double newWidth = data.rc.Width / scaleX;
                double newHeight = data.rc.Height / scaleY;
                System.Diagnostics.Debug.WriteLine($"[AppBar] Window pos: Left={newLeft} Top={newTop} Width={newWidth} Height={newHeight}");

                UnlockPosition();
                this.Left = newLeft;
                this.Top = newTop;
                this.Width = newWidth;
                this.Height = newHeight;
                LockPosition();

                // 位置重算后宽度已恢复为 AppBarWidth，同步重置悬停展开标记，
                // 避免分辨率变化等场景下标记与真实宽度不一致导致后续收回失效
                _isHoverExpanded = false;
            }
            finally
            {
                _isPositioning = false;
            }
        }

        /// <summary>
        /// 鼠标悬停展开/收回视觉宽度。仅调整窗口 Left/Width，不重新注册 AppBar。
        /// 右屏保持外侧边缘固定、向屏幕内侧延伸；左屏 Left 不变。
        /// </summary>
        public void SetHoverExpanded(bool expanded)
        {
            if (!IsAppBarRegistered || expanded == _isHoverExpanded) return;

            var (monitorHandle, monitor) = GetTargetMonitorEx();
            GetMonitorDpi(monitorHandle, out double dpiX, out _);
            double scaleX = dpiX / 96.0;

            var monitorRect = monitor.rcMonitor;

            // 逻辑像素(DIP)恒定，物理 = DIP * scale（与 PositionAppBar 一致）
            int targetLogical = expanded ? ExpandedWidth : AppBarWidth;
            int targetPhysical = (int)Math.Round(targetLogical * scaleX);
            double targetWidthLogical = targetLogical;

            UnlockPosition();

            // 右屏：外侧固定，向内延伸（重新计算 Left，非增量累加）
            if (DockSide == DockEdge.Right)
                this.Left = (monitorRect.Right - targetPhysical) / scaleX;

            this.Width = targetWidthLogical;

            LockPosition();

            _isHoverExpanded = expanded;
        }

        /// <summary>
        /// 计算“贴着本侧边栏弹出”的浮窗左边缘 X 坐标（DIP）：
        /// 左停靠 → 浮窗在侧边栏右侧；右停靠 → 浮窗在侧边栏左侧（减去浮窗宽度）。
        /// 保证浮窗始终落在被点击侧边栏所在的显示器，而不是弹到相邻显示器/屏幕外。
        /// </summary>
        /// <param name="flyoutWidth">浮窗自身宽度（DIP），右停靠时用于向左偏移</param>
        public double GetFlyoutLeft(double flyoutWidth)
            => DockSide == DockEdge.Left ? Left + Width : Left - flyoutWidth;

        #endregion

        #region Window Overrides

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 设置窗口样式：工具窗口（不在任务栏/Alt+Tab显示）
            var hwnd = new WindowInteropHelper(this).Handle;
            long style = NativeMethods.GetWindowLongPtr(hwnd, WindowLongIndex.GWL_EXSTYLE);
            style |= WindowStyleEx.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, WindowLongIndex.GWL_EXSTYLE, style);

            // 禁止 Aero Peek 遮挡
            int excludeFromPeek = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, 12 /* DWMWA_EXCLUDED_FROM_PEEK */, ref excludeFromPeek, sizeof(int));
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 确保关闭前释放 AppBar
            System.Diagnostics.Debug.WriteLine($"[AppBarWindow] OnClosing - Reason: {e}");
            LogEvent("OnClosing");
            UnregisterAppBar();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[AppBarWindow] OnClosed");
            LogEvent("OnClosed");
            UnregisterAppBar();
            base.OnClosed(e);
        }

        #endregion
        
        #region Logging
        
        /// <summary>
        /// 记录窗口事件到日志
        /// </summary>
        private void LogEvent(string eventName)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logLine = $"[{timestamp}] [AppBarWindow] {eventName} - Title={Title}, Handle={new System.Windows.Interop.WindowInteropHelper(this).Handle}";
                
                System.Diagnostics.Debug.WriteLine(logLine);
                
                // 写入文件日志
                string logDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk",
                    "Logs");
                System.IO.Directory.CreateDirectory(logDir);
                
                string logFile = System.IO.Path.Combine(logDir, "window.log");
                System.IO.File.AppendAllText(logFile, logLine + Environment.NewLine);
            }
            catch { }
        }
        
        #endregion

        #region Private Methods

        private APPBARDATA CreateAppBarData(IntPtr hwnd)
        {
            var data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(data);
            data.hWnd = hwnd;
            return data;
        }

        private IntPtr AppBarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Win+D 显示桌面等场景可能发送 SC_MINIMIZE，AppBar 侧边栏不应被最小化，直接拦截
            if (msg == WindowMessage.WM_SYSCOMMAND && wParam.ToInt32() == SysCommand.SC_MINIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == _callbackMessageId)
            {
                int notify = wParam.ToInt32();
                switch (notify)
                {
                    case AppBarNotify.ABN_POSCHANGED:
                        // 不自动重新定位，避免无限循环
                        // 原 SidebarDiagnostics 项目也禁用了此处理
                        break;

                    case AppBarNotify.ABN_FULLSCREENAPP:
                        // 全屏让位的 Z 序调整是自身主动行为，需放行（PreventMoveHook 据 _allowZOrderChange 跳过 Z 序保护）
                        _allowZOrderChange = true;
                        try
                        {
                            if (lParam.ToInt32() == 1)
                            {
                                // 全屏进入，置底
                                NativeMethods.SetWindowPos(hwnd, HwndConstants.HWND_BOTTOM, 0, 0, 0, 0,
                                    SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
                            }
                            else
                            {
                                // 全屏退出，恢复常驻置顶（与 MainWindow.xaml Topmost=True 保持一致）
                                NativeMethods.SetWindowPos(hwnd, HwndConstants.HWND_TOPMOST, 0, 0, 0, 0,
                                    SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
                            }
                        }
                        finally
                        {
                            _allowZOrderChange = false;
                        }
                        break;
                }
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void LockPosition()
        {
            if (_isMoveLocked || _hwndSource == null) return;
            _isMoveLocked = true;
            _hwndSource.AddHook(PreventMoveHook);
        }

        private void UnlockPosition()
        {
            if (!_isMoveLocked || _hwndSource == null) return;
            _isMoveLocked = false;
            _hwndSource.RemoveHook(PreventMoveHook);
        }

        private IntPtr PreventMoveHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WindowMessage.WM_WINDOWPOSCHANGING)
            {
                // 自身主动发起的操作（全屏让位）：完全放行
                if (_allowZOrderChange)
                    return IntPtr.Zero;

                // AppBar 位置锁定：防止被拖动移位、防止被隐藏。
                // 常驻 topmost（MainWindow.xaml Topmost=True）后侧边栏处于置顶层，Win+D 不会再把它压到桌面下，
                // 故不再需要 Z 序下压拦截 / burst 重呈现那套动态恢复逻辑。
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.flags |= SetWindowPosFlags.SWP_NOMOVE; // 防止位置被改
                pos.flags &= ~0x0080u; // 清除 SWP_HIDEWINDOW，防止被隐藏
                Marshal.StructureToPtr(pos, lParam, true);
            }
            return IntPtr.Zero;
        }

        /// <summary>返回目标显示器的句柄+信息（句柄用于查该屏 DPI，保证 DPI 与坐标同源）。</summary>
        private (IntPtr handle, MONITORINFO info) GetTargetMonitorEx()
        {
            var monitors = new List<(IntPtr handle, MONITORINFO info)>();

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData) =>
            {
                var info = new MONITORINFO();
                info.cbSize = Marshal.SizeOf<MONITORINFO>();
                NativeMethods.GetMonitorInfo(hMonitor, ref info);
                monitors.Add((hMonitor, info));
                return true;
            }, 0);

            // 按主显示器优先排序
            var sorted = monitors.OrderByDescending(m => m.info.dwFlags & 1).ToList();

            int index = Math.Min(ScreenIndex, sorted.Count - 1);
            return sorted[index];
        }

        private MONITORINFO GetTargetMonitor() => GetTargetMonitorEx().info;

        /// <summary>查询指定显示器句柄的 DPI。必须传入与坐标同源的 monitor 句柄（见 GetTargetMonitorEx），
        /// 不可再按 ScreenIndex 独立枚举——枚举顺序不保证与排序后的目标屏一致，异 DPI 多屏下会取错屏导致定位错乱。</summary>
        private void GetMonitorDpi(IntPtr hMonitor, out double dpiX, out double dpiY)
        {
            dpiX = 96;
            dpiY = 96;

            try
            {
                NativeMethods.GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out uint dy);
                dpiX = dx;
                dpiY = dy;
            }
            catch
            {
                // 回退到 96 DPI
            }
        }

        #endregion
    }

    public enum DockEdge
    {
        Left,
        Right
    }

    public enum LayoutMode
    {
        // 顺序不可重排：旧版 settings.json 以整数序列化枚举（Left=0/Right=1/Both=2），
        // 新增的 None 必须追加在末尾，否则会使旧配置的 Left 被误读为 None。
        Left,
        Right,
        Both,
        None
    }

    /// <summary>
    /// 单个物理显示器信息（供多屏停靠配置使用）
    /// </summary>
    public struct MonitorDisplayInfo
    {
        public bool IsPrimary;
        public int PixelWidth;
        public int PixelHeight;
    }

    /// <summary>
    /// 枚举所有物理显示器，按「主显示器优先」排序返回，
    /// 顺序与 <see cref="AppBarWindow"/> 的 ScreenIndex/GetTargetMonitor 保持一致。
    /// </summary>
    public static class MonitorEnumerator
    {
        public static List<MonitorDisplayInfo> GetMonitorsPrimaryFirst()
        {
            var monitors = new List<MONITORINFO>();

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData) =>
                {
                    var info = new MONITORINFO();
                    info.cbSize = Marshal.SizeOf<MONITORINFO>();
                    if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                        monitors.Add(info);
                    return true;
                }, 0);

            // 主显示器优先（MONITORINFOF_PRIMARY = 1），与 GetTargetMonitor 排序一致
            return monitors
                .OrderByDescending(m => m.dwFlags & 1)
                .Select(m => new MonitorDisplayInfo
                {
                    IsPrimary = (m.dwFlags & 1) != 0,
                    PixelWidth = m.rcMonitor.Width,
                    PixelHeight = m.rcMonitor.Height
                })
                .ToList();
        }
    }
}
