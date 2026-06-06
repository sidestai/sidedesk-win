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

        public static IntPtr FindNewWindow(string processName, ICollection<IntPtr> excludeHandles)
        {
            IntPtr found = IntPtr.Zero;
            var seen = new HashSet<IntPtr>(excludeHandles);
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;
                if (seen.Contains(hWnd))
                    return true;

                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);
                try
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        if (proc.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = hWnd;
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
