using System;
using System.Runtime.InteropServices;

namespace SideDesk
{
    internal static class NativeMethods
    {
        // Shell AppBar
        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        // Window positioning
        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        // Window style
        [DllImport("user32.dll")]
        internal static extern long GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        internal static extern long SetWindowLongPtr(IntPtr hwnd, int index, long newStyle);

        // Register window message
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int RegisterWindowMessage(string msg);

        // Monitor enumeration
        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lpRect, MonitorEnumCallback callback, int dwData);

        [DllImport("user32.dll")]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // DPI
        [DllImport("shcore.dll")]
        internal static extern IntPtr GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        // DWM
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwmAttribute, ref int pvAttribute, int cbAttribute);

        internal delegate bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData);
    }

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPOS
    {
        public IntPtr hWnd;
        public IntPtr hWndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    #endregion

    #region Constants

    internal static class AppBarMessage
    {
        public const int ABM_NEW = 0;
        public const int ABM_REMOVE = 1;
        public const int ABM_QUERYPOS = 2;
        public const int ABM_SETPOS = 3;
    }

    internal static class AppBarEdge
    {
        public const int ABE_LEFT = 0;
        public const int ABE_TOP = 1;
        public const int ABE_RIGHT = 2;
        public const int ABE_BOTTOM = 3;
    }

    internal static class AppBarNotify
    {
        public const int ABN_STATECHANGE = 0;
        public const int ABN_POSCHANGED = 1;
        public const int ABN_FULLSCREENAPP = 2;
        public const int ABN_WINDOWARRANGE = 3;
    }

    internal static class WindowMessage
    {
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_ACTIVATE = 0x0006;
    }

    internal static class SetWindowPosFlags
    {
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;
    }

    internal static class WindowLongIndex
    {
        public const int GWL_EXSTYLE = -20;
    }

    internal static class WindowStyleEx
    {
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_NOACTIVATE = 0x08000000;
    }

    internal static class HwndConstants
    {
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    }

    #endregion
}
