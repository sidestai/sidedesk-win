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
        /// AppBar 宽度（像素，物理像素）
        /// </summary>
        public int AppBarWidth { get; set; } = 400;

        /// <summary>
        /// 目标显示器索引 (0 = 主显示器)
        /// </summary>
        public int ScreenIndex { get; set; } = 0;

        #endregion

        #region State

        public bool IsAppBarRegistered { get; private set; } = false;

        private int _callbackMessageId;
        private bool _isMoveLocked = false;
        private bool _isPositioning = false;
        private HwndSource? _hwndSource;
        private IntPtr _registeredHwnd = IntPtr.Zero; // 保存注册时的窗口句柄

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

            UnlockPosition();

            _hwndSource?.RemoveHook(AppBarWndProc);

            // 使用保存的句柄，避免窗口关闭后 Handle 已无效
            var hwnd = _registeredHwnd != IntPtr.Zero ? _registeredHwnd : new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var data = CreateAppBarData(hwnd);
                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_REMOVE, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] ABM_REMOVE called with hwnd={hwnd}");
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
                var monitor = GetTargetMonitor();

                // 使用 rcMonitor（完整显示器区域）计算左右边界
                // 使用 rcWork 的上下边界（避开任务栏）
                var monitorRect = monitor.rcMonitor;
                var workArea = monitor.rcWork;

                System.Diagnostics.Debug.WriteLine($"[AppBar] Monitor: L={monitorRect.Left} T={monitorRect.Top} R={monitorRect.Right} B={monitorRect.Bottom}");
                System.Diagnostics.Debug.WriteLine($"[AppBar] WorkArea: L={workArea.Left} T={workArea.Top} R={workArea.Right} B={workArea.Bottom}");

                // 计算 AppBar 占用区域（物理像素）
                var data = CreateAppBarData(hwnd);
                data.uEdge = DockSide == DockEdge.Left ? AppBarEdge.ABE_LEFT : AppBarEdge.ABE_RIGHT;

                if (DockSide == DockEdge.Left)
                {
                    data.rc.Left = monitorRect.Left;
                    data.rc.Top = workArea.Top;
                    data.rc.Right = monitorRect.Left + AppBarWidth;
                    data.rc.Bottom = workArea.Bottom;
                }
                else
                {
                    data.rc.Left = monitorRect.Right - AppBarWidth;
                    data.rc.Top = workArea.Top;
                    data.rc.Right = monitorRect.Right;
                    data.rc.Bottom = workArea.Bottom;
                }

                System.Diagnostics.Debug.WriteLine($"[AppBar] Before QUERYPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                // 查询并设置位置
                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_QUERYPOS, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] After QUERYPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                NativeMethods.SHAppBarMessage(AppBarMessage.ABM_SETPOS, ref data);
                System.Diagnostics.Debug.WriteLine($"[AppBar] After SETPOS: L={data.rc.Left} T={data.rc.Top} R={data.rc.Right} B={data.rc.Bottom}");

                // 检查返回的 RECT 是否有效
                if (data.rc.Width <= 0 || data.rc.Height <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[AppBar] ERROR: Invalid rect from SETPOS, skipping window move");
                    return;
                }

                // 获取 DPI 缩放
                GetMonitorDpi(out double dpiX, out double dpiY);
                double scaleX = dpiX / 96.0;
                double scaleY = dpiY / 96.0;
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
            }
            finally
            {
                _isPositioning = false;
            }
        }

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
            UnregisterAppBar();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterAppBar();
            base.OnClosed(e);
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
                        // 全屏应用进入/退出
                        if (lParam.ToInt32() == 1)
                        {
                            // 全屏进入，置底
                            NativeMethods.SetWindowPos(hwnd, HwndConstants.HWND_BOTTOM, 0, 0, 0, 0,
                                SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
                        }
                        else
                        {
                            // 全屏退出，置顶
                            NativeMethods.SetWindowPos(hwnd, HwndConstants.HWND_TOPMOST, 0, 0, 0, 0,
                                SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
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
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.flags |= 0x0002; // SWP_NOMOVE
                Marshal.StructureToPtr(pos, lParam, true);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private MONITORINFO GetTargetMonitor()
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
            return sorted[index].info;
        }

        private void GetMonitorDpi(out double dpiX, out double dpiY)
        {
            dpiX = 96;
            dpiY = 96;

            try
            {
                var monitors = new List<IntPtr>();
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData) =>
                {
                    monitors.Add(hMonitor);
                    return true;
                }, 0);

                if (monitors.Count > 0)
                {
                    int index = Math.Min(ScreenIndex, monitors.Count - 1);
                    NativeMethods.GetDpiForMonitor(monitors[index], 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out uint dy);
                    dpiX = dx;
                    dpiY = dy;
                }
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
}
