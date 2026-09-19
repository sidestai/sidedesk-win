using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SideDesk.Windows
{
    public static class WindowFinder
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>判断顶层窗口是否属于指定进程名</summary>
        private static bool MatchesProcess(IntPtr hWnd, string processName)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            try
            {
                using (var proc = Process.GetProcessById((int)pid))
                    return proc.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>启动前快照：指定进程当前全部可见顶层窗口句柄。
        /// 供启动后轮询排除「启动前就存在」的窗口，避免新窗口创建延迟期间误绑定用户自开的同浏览器窗口</summary>
        public static HashSet<IntPtr> SnapshotWindows(string processName)
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd) && MatchesProcess(hWnd, processName))
                    set.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        public static IntPtr FindNewWindow(string processName, ICollection<IntPtr> excludeHandles, HashSet<IntPtr>? excludeSnapshot = null)
        {
            IntPtr found = IntPtr.Zero;
            var seen = new HashSet<IntPtr>(excludeHandles);
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;
                if (seen.Contains(hWnd))
                    return true;
                if (excludeSnapshot != null && excludeSnapshot.Contains(hWnd))
                    return true;

                if (MatchesProcess(hWnd, processName))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
