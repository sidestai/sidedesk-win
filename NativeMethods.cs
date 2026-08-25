using System;
using System.Runtime.InteropServices;
using System.Text;

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

        // 32位版本（用于x86兼容性）
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        internal static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        internal static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        // 窗口样式常量
        public const int GWL_EXSTYLE = -20;
        public const int GWL_HWNDPARENT = -8; // 属主窗口句柄（注意：不是窗口创建时间，Win32 无直接取创建时间的 API）
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_APPWINDOW = 0x00040000; // 强制在任务栏显示（与 TOOLWINDOW 同时存在时优先，去任务栏需一并清除）

        // Register window message
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int RegisterWindowMessage(string msg);

        // Monitor enumeration
        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lpRect, MonitorEnumCallback callback, int dwData);

        [DllImport("user32.dll")]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        // MonitorFromWindow/MonitorFromPoint 的 dwFlags
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        // Cursor / window rect (for hover-expand hit-testing)
        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // SystemParametersInfo
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        internal const uint SPI_GETWORKAREA = 0x0030;
        internal const uint SPI_SETWORKAREA = 0x002F;
        internal const uint SPIF_SENDCHANGE = 0x0002;

        // FindWindow (用于定位任务栏)
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        internal static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        internal const uint WM_SETTINGCHANGE = 0x001A;

        // SendMessageTimeout：广播消息时用，避免目标窗口无响应导致死锁
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        // DPI
        [DllImport("shcore.dll")]
        internal static extern IntPtr GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        // DWM
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // DWM 桌面合成是否启用（渲染诊断用，Win8+ 恒为 true）
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

        // 显示适配器枚举（渲染诊断：识别主显示器是否为虚拟/远程显示驱动）
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)] public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            [MarshalAs(UnmanagedType.U4)] public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        internal const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        internal const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        // Window visibility & class
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        // Window enumeration
        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Window text
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        // Clipboard format listener
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        // Low-level keyboard hook
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // Module handle
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);
        
        // Key state
        [DllImport("user32.dll")]
        internal static extern short GetKeyState(int nVirtKey);

        // Window state
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        internal const int SW_HIDE = 0;
        internal const int SW_RESTORE = 9;
        internal const int SW_SHOW = 5;
        internal const int SW_SHOWNORMAL = 1;
        internal const uint WM_CLOSE = 0x0010;
        internal const uint WM_LBUTTONDOWN = 0x0201;
        internal const uint WM_LBUTTONUP = 0x0202;
        internal const int MK_LBUTTON = 0x0001;

        // ITaskbarList COM interface
        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        // Delegates
        internal delegate bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdc, ref RECT pRect, int dwData);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // Shell Properties
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        internal const uint SEE_MASK_INVOKEIDLIST = 0x0C;

        /// <summary>显示文件/文件夹的系统属性对话框</summary>
        internal static void ShowFileProperties(string path)
        {
            var info = new SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(info);
            info.lpVerb = "properties";
            info.lpFile = path;
            info.fMask = SEE_MASK_INVOKEIDLIST;
            info.nShow = 1; // SW_SHOWNORMAL
            ShellExecuteEx(ref info);
        }

        #region Shell Context Menu (Explorer 右键菜单)

        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig]
            int InvokeCommand(ref CMINVOKECOMMANDINFO lpici);
            [PreserveSig]
            int GetCommandString(uint idCmd, uint uType, IntPtr pwReserved, StringBuilder pszName, uint cchMax);
        }

        [ComImport, Guid("000214f2-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IContextMenu2 : IContextMenu
        {
            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IContextMenu3 : IContextMenu2
        {
            [PreserveSig]
            int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr plResult);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CMINVOKECOMMANDINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            void GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr lpName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [DllImport("shell32.dll")]
        internal static extern int SHGetDesktopFolder(out IntPtr ppshf);

        [DllImport("shell32.dll")]
        internal static extern int SHGetPathFromIDListW(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszPath);

        [DllImport("ole32.dll")]
        internal static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("user32.dll")]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        internal static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        internal static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hwnd, IntPtr lprc);

        internal const uint CMF_NORMAL = 0x00000000;
        internal const uint CMF_EXTENDEDVERBS = 0x00000100;
        internal const uint TPM_RETURNCMD = 0x0100;
        internal const uint TPM_RIGHTBUTTON = 0x0002;

        /// <summary>显示 Windows 资源管理器风格的右键菜单</summary>
        internal static void ShowExplorerContextMenu(IntPtr ownerHwnd, string filePath, int x, int y)
        {
            IntPtr desktopPtr;
            if (SHGetDesktopFolder(out desktopPtr) != 0) return;
            var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
            Marshal.Release(desktopPtr);

            uint eaten = 0, attrs = 0;
            IntPtr pidl;
            try
            {
                desktop.ParseDisplayName(ownerHwnd, IntPtr.Zero, filePath, ref eaten, out pidl, ref attrs);
            }
            catch { return; }

            Guid iidContextMenu = typeof(IContextMenu).GUID;
            IntPtr ctxPtr;
            // apidl 必须是指向 PIDL 指针数组的指针
            IntPtr apidl = Marshal.AllocCoTaskMem(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(apidl, pidl);
                desktop.GetUIObjectOf(ownerHwnd, 1, apidl, ref iidContextMenu, IntPtr.Zero, out ctxPtr);
            }
            catch { CoTaskMemFree(pidl); Marshal.FreeCoTaskMem(apidl); return; }
            Marshal.FreeCoTaskMem(apidl);

            var ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);
            Marshal.Release(ctxPtr);

            IntPtr hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) { CoTaskMemFree(pidl); return; }

            try
            {
                ctxMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXTENDEDVERBS);

                var ctxMenu2 = ctxMenu as IContextMenu2;
                uint cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, 0, ownerHwnd, IntPtr.Zero);

                if (cmd > 0)
                {
                    var ici = new CMINVOKECOMMANDINFO();
                    ici.cbSize = Marshal.SizeOf(ici);
                    ici.hwnd = ownerHwnd;
                    ici.lpVerb = (IntPtr)(cmd - 1);
                    ici.nShow = 1;
                    ctxMenu.InvokeCommand(ref ici);
                }
            }
            finally
            {
                DestroyMenu(hMenu);
                CoTaskMemFree(pidl);
            }
        }

        #endregion

        #region Memory

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessWorkingSetSize(IntPtr hProcess, long dwMinimumWorkingSetSize, long dwMaximumWorkingSetSize);

        #endregion
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
    internal struct POINT
    {
        public int X;
        public int Y;
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
        public const int WM_CLIPBOARDUPDATE = 0x031D;
    }

    internal static class HookConstants
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
    }

    internal static class SetWindowPosFlags
    {
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
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

    #region Shell Properties

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    #endregion
}
