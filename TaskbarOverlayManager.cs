using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SideDesk.Framework;
using SideDesk.Windows;

namespace SideDesk
{
    public static class TaskbarOverlayManager
    {
        #region Windows API

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        // GetWindowLong / SetWindowLong — 32/64 兼容封装
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private const uint WM_GETICON = 0x7F;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL = 0;
        private const int GWL_STYLE = -16;
        // 标题锁定标志位
        private const long TITLE_LOCK_FLAG = 0x00000800L;

        #endregion

        #region Window Enumeration

        public class WindowInfo
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; }
            public string ProcessName { get; set; }
            public BitmapSource Icon { get; set; }
        }

        public static List<WindowInfo> GetTaskbarWindows()
        {
            var result = new List<WindowInfo>();

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                string title = GetWindowTitle(hWnd);
                if (string.IsNullOrEmpty(title))
                    return true;

                // Skip system windows
                string className = GetWindowClassName(hWnd);
                if (IsSystemWindow(className, title))
                    return true;

                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);

                string processName = "Unknown";
                string exePath = null;
                try
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        processName = proc.ProcessName;
                        try { exePath = proc.MainModule?.FileName; } catch { }
                    }
                }
                catch { }

                // Skip our own window
                if (processName.Equals("SideDesk", StringComparison.OrdinalIgnoreCase))
                    return true;

                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = processName,
                    Icon = GetExeIconSafe(exePath)
                });

                return true;
            }, IntPtr.Zero);

            return result.OrderBy(w => w.ProcessName).ThenBy(w => w.Title).ToList();
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool IsSystemWindow(string className, string title)
        {
            var sysClasses = new[] { "Shell_TrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
                "ApplicationFrameWindow", "Windows.Internal.Shell.TabProxyWindow" };
            if (sysClasses.Any(c => className.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            if (title == "Program Manager" || title == "设置" || title == "Settings")
                return true;
            return false;
        }

        // 注意：原 GetWindowIcon 通过 SendMessage(WM_GETICON) 跨进程同步获取图标，
        // 一旦目标窗口 UI 线程繁忙/挂起，会导致本进程被无限期阻塞（点击"任务栏标记"卡死的根因）。
        // 改为基于 PID → exe 路径 → ExtractAssociatedIcon 的本地文件 IO 方式，永不卡。
        private static BitmapSource GetExeIconSafe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                {
                    if (icon == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    if (src.CanFreeze) src.Freeze();
                    return src;
                }
            }
            catch { }
            return null;
        }

        #endregion

        #region Window Details

        public class WindowDetails
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; }
            public string ClassName { get; set; }
            public long StyleFlags { get; set; }
            public long ExStyleFlags { get; set; }
            public int ProcessId { get; set; }
            public int ParentProcessId { get; set; }
            public string ProcessName { get; set; }
            public string ExecutablePath { get; set; }
            public string CommandLine { get; set; }
            public string WorkingDirectory { get; set; }
            public string UserName { get; set; }
            public DateTime? StartTime { get; set; }
            public string ParentProcessName { get; set; }
        }

        private const int GWL_EXSTYLE = -20;

        /// <summary>
        /// 获取指定窗口的详细信息，包括进程路径、命令行、父进程、样式位等。
        /// </summary>
        public static WindowDetails GetWindowDetails(IntPtr hwnd, bool fullDetails = true)
        {
            var d = new WindowDetails
            {
                Handle = hwnd,
                Title = GetWindowTitle(hwnd),
                ClassName = GetWindowClassName(hwnd)
            };

            try
            {
                d.StyleFlags = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                d.ExStyleFlags = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            }
            catch { }

            uint pid = 0;
            GetWindowThreadProcessId(hwnd, out pid);
            d.ProcessId = (int)pid;

            try
            {
                using (var proc = Process.GetProcessById(d.ProcessId))
                {
                    d.ProcessName = proc.ProcessName;
                    try { d.StartTime = proc.StartTime; } catch { }
                    try { d.ExecutablePath = proc.MainModule?.FileName; } catch { }
                }
            }
            catch { }

            // 从 ExecutablePath 推导工作目录（无需 WMI）
            if (!string.IsNullOrEmpty(d.ExecutablePath))
            {
                try { d.WorkingDirectory = System.IO.Path.GetDirectoryName(d.ExecutablePath); } catch { }
            }

            // 仅在需要完整信息且 Process API 未能获取足够数据时才走 WMI
            if (fullDetails)
            {
                try
                {
                    string query = $"SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = {d.ProcessId}";
                    using (var searcher = new ManagementObjectSearcher(query))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject mo in results)
                        {
                            d.CommandLine = mo["CommandLine"]?.ToString();
                            if (string.IsNullOrEmpty(d.ExecutablePath))
                            {
                                d.ExecutablePath = mo["ExecutablePath"]?.ToString();
                                if (!string.IsNullOrEmpty(d.ExecutablePath))
                                    try { d.WorkingDirectory = System.IO.Path.GetDirectoryName(d.ExecutablePath); } catch { }
                            }
                            if (mo["ParentProcessId"] != null)
                                d.ParentProcessId = Convert.ToInt32(mo["ParentProcessId"]);

                            // 获取所有者
                            try
                            {
                                var outParams = new object[] { string.Empty, string.Empty };
                                mo.InvokeMethod("GetOwner", outParams);
                                string user = outParams[0] as string;
                                string domain = outParams[1] as string;
                                if (!string.IsNullOrEmpty(user))
                                    d.UserName = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
                            }
                            catch { }

                            mo.Dispose();
                            break;
                        }
                    }

                    // 查找父进程名
                    if (d.ParentProcessId > 0)
                    {
                        try
                        {
                            using (var parent = Process.GetProcessById(d.ParentProcessId))
                            {
                                d.ParentProcessName = parent.ProcessName;
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GetWindowDetails][WMI] {ex.Message}");
                }
            }

            return d;
        }

        #endregion

        #region Lock / Unlock Title

        // 记录被锁定窗口的原始标题，用于解锁时还原
        private static readonly Dictionary<IntPtr, string> _originalTitles = new Dictionary<IntPtr, string>();

        // 被锁定窗口 → 期望显示的标题文字（守望狗依据此表周期性覆盖）
        private static readonly Dictionary<IntPtr, string> _lockedTitles = new Dictionary<IntPtr, string>();
        private static readonly object _lockSync = new object();
        private static System.Threading.Timer _watchdogTimer;
        private static System.Threading.Timer _rescanTimer;
        // 默认刷新间隔 5秒；AppSettings.TitleLockWatchdogInterval 将覆盖为用户配置值。
        private const int DefaultWatchdogIntervalMs = 5000;
        // 默认重扫全部窗口间隔 15秒（用于检测重开的应用）。
        private const int DefaultRescanIntervalMs = 15000;

        private static int GetWatchdogInterval()
        {
            try
            {
                int v = AppSettings.Instance.TitleLockWatchdogInterval;
                if (v < 500) v = 500;
                if (v > 60000) v = 60000;
                return v;
            }
            catch
            {
                return DefaultWatchdogIntervalMs;
            }
        }

        /// <summary>
        /// 在设置修改后调用以应用新的刷新间隔。未启动看门狗时不做事。
        /// </summary>
        public static void UpdateWatchdogInterval(int intervalMs)
        {
            if (intervalMs < 500) intervalMs = 500;
            if (intervalMs > 60000) intervalMs = 60000;
            lock (_lockSync)
            {
                if (_watchdogTimer != null)
                {
                    try { _watchdogTimer.Change(intervalMs, intervalMs); } catch { }
                }
            }
        }

        private static int GetRescanInterval()
        {
            try
            {
                int v = AppSettings.Instance.TitleLockRescanInterval;
                if (v < 2000) v = 2000;
                if (v > 600000) v = 600000;
                return v;
            }
            catch
            {
                return DefaultRescanIntervalMs;
            }
        }

        /// <summary>
        /// 在设置修改后调用以应用新的重扫间隔。未启动重扫定时器时不做事。
        /// </summary>
        public static void UpdateRescanInterval(int intervalMs)
        {
            if (intervalMs < 2000) intervalMs = 2000;
            if (intervalMs > 600000) intervalMs = 600000;
            lock (_lockSync)
            {
                if (_rescanTimer != null)
                {
                    try { _rescanTimer.Change(intervalMs, intervalMs); } catch { }
                }
            }
        }

        /// <summary>
        /// 启动重扫定时器：周期性全量枚举当前窗口、匹配本地规则，对未锁定的命中项调用锁定。
        /// 重复调用幂等。
        /// </summary>
        public static void EnsureRescanStarted()
        {
            lock (_lockSync)
            {
                if (_rescanTimer != null) return;
                int interval = GetRescanInterval();
                _rescanTimer = new System.Threading.Timer(RescanTick, null, interval, interval);
            }
        }

        public static void StopRescan()
        {
            lock (_lockSync)
            {
                if (_rescanTimer == null) return;
                try { _rescanTimer.Dispose(); } catch { }
                _rescanTimer = null;
            }
        }

        private static void RescanTick(object state)
        {
            RescanPass();
        }

        /// <summary>
        /// 立即同步执行一轮「枚举窗口 → 匹配规则 → 锁定命中项」，返回本轮锁定数。
        /// 供规则编辑保存后立即生效使用（后台重扫定时器未启动时也能应用规则）。
        /// </summary>
        public static int ApplyRulesNow()
        {
            return RescanPass();
        }

        private static int RescanPass()
        {
            int applied = 0;
            try
            {
                var rules = TitleLockRuleManager.GetAll();
                if (rules == null || rules.Count == 0) return 0;

                HashSet<IntPtr> alreadyLocked;
                lock (_lockSync)
                {
                    alreadyLocked = new HashSet<IntPtr>(_lockedTitles.Keys);
                }

                var windows = GetTaskbarWindows();
                foreach (var w in windows)
                {
                    // 已被看门狗看顾的句柄跳过
                    if (alreadyLocked.Contains(w.Handle)) continue;

                    try
                    {
                        var details = GetWindowDetails(w.Handle, fullDetails: false);
                        if (details == null || string.IsNullOrEmpty(details.ProcessName)) continue;

                        var match = TitleLockRuleManager.FindMatch(
                            details.ProcessName,
                            details.Title,
                            details.WorkingDirectory);
                        if (match == null) continue;

                        // 命中 → 锁定。persistRule=false，避免重复写规则。
                        if (LockWindowTitleCore(w.Handle, match.LockText, match.MatchPattern, match.MatchMode, persistRule: false))
                            applied++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Rescan][{w.Title}] {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rescan] {ex.Message}");
            }
            return applied;
        }

        /// <summary>
        /// 锁定窗口标题：设置标题锁定标志位后写入新标题，并启动守望狗每 5 秒覆盖一次。text 建议为 1-2 个字符。
        /// </summary>
        public static bool LockWindowTitle(IntPtr hwnd, string text)
        {
            return LockWindowTitle(hwnd, text, null, TitleMatchMode.Contains);
        }

        /// <summary>
        /// 锁定窗口标题。
        /// </summary>
        /// <param name="hwnd">目标窗口句柄</param>
        /// <param name="text">锁定后显示的文字（1-2 个字符）</param>
        /// <param name="matchPattern">可选的模糊匹配字符串，用于下次启动自动恢复</param>
        /// <param name="matchMode">匹配模式，仅当 matchPattern 非空时生效</param>
        public static bool LockWindowTitle(IntPtr hwnd, string text, string matchPattern, TitleMatchMode matchMode)
        {
            return LockWindowTitleCore(hwnd, text, matchPattern, matchMode, persistRule: true);
        }

        /// <summary>
        /// 内部核心实现。persistRule=false 时不写入本地规则（用于启动自动恢复，避免重复 Upsert）。
        /// </summary>
        private static bool LockWindowTitleCore(IntPtr hwnd, string text, string matchPattern, TitleMatchMode matchMode, bool persistRule)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // 备份原标题（仅首次）
                string originalTitle = null;
                if (!_originalTitles.ContainsKey(hwnd))
                {
                    originalTitle = GetWindowTitle(hwnd);
                    if (!string.IsNullOrEmpty(originalTitle))
                        _originalTitles[hwnd] = originalTitle;
                }
                else
                {
                    originalTitle = _originalTitles[hwnd];
                }

                // 获取当前窗口样式并追加锁定标志
                IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
                long styleVal = style.ToInt64();
                styleVal |= TITLE_LOCK_FLAG;
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(styleVal));

                // 写入新标题
                string desired = text ?? string.Empty;
                SetWindowText(hwnd, desired);

                // 记录期望标题并启动守望狗
                lock (_lockSync)
                {
                    _lockedTitles[hwnd] = desired;
                    EnsureWatchdogStarted();
                }

                // 手动锁定后也保证重扫定时器在跑，以便该应用重开产生新句柄后能被重新锁定
                if (persistRule)
                {
                    EnsureRescanStarted();
                }

                // 持久化规则（进程名 + 锁定前标题 + 进程目录 + 锁定后文字 + 可选匹配模式）
                // 自动恢复路径下 persistRule=false，跳过此步避免重复写入
                if (persistRule)
                {
                    try
                    {
                        var details = GetWindowDetails(hwnd);
                        if (details != null && !string.IsNullOrEmpty(details.ProcessName))
                        {
                            TitleLockRuleManager.Upsert(new TitleLockRule
                            {
                                ProcessName = details.ProcessName,
                                OriginalTitle = originalTitle ?? string.Empty,
                                ExecutableDir = details.WorkingDirectory ?? string.Empty,
                                LockText = desired,
                                MatchPattern = string.IsNullOrEmpty(matchPattern) ? null : matchPattern,
                                MatchMode = matchMode
                            });
                        }
                    }
                    catch (Exception saveEx)
                    {
                        Debug.WriteLine($"[LockWindowTitle][SaveRule] {saveEx.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LockWindowTitle] Failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解锁窗口标题：清除锁定标志位并还原原标题（如果之前有备份）。
        /// </summary>
        public static bool UnlockWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // 处理持久化规则删除（需在覆盖标题前读取 ProcessName）
                string processNameForRule = null;
                string originalTitleForRule = null;
                try
                {
                    var details = GetWindowDetails(hwnd);
                    processNameForRule = details?.ProcessName;
                    _originalTitles.TryGetValue(hwnd, out originalTitleForRule);
                }
                catch { }

                // 从守望表中移除
                lock (_lockSync)
                {
                    _lockedTitles.Remove(hwnd);
                    if (_lockedTitles.Count == 0)
                        StopWatchdog();
                }

                IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
                long styleVal = style.ToInt64();
                styleVal &= ~TITLE_LOCK_FLAG;
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(styleVal));

                if (_originalTitles.TryGetValue(hwnd, out string original))
                {
                    SetWindowText(hwnd, original);
                    _originalTitles.Remove(hwnd);
                }

                // 同步删除本地规则
                if (!string.IsNullOrEmpty(processNameForRule))
                {
                    try { TitleLockRuleManager.Remove(processNameForRule, originalTitleForRule); }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UnlockWindowTitle] Failed: {ex.Message}");
                return false;
            }
        }

        private static void EnsureWatchdogStarted()
        {
            if (_watchdogTimer != null) return;
            int interval = GetWatchdogInterval();
            _watchdogTimer = new System.Threading.Timer(
                WatchdogTick, null, interval, interval);
        }

        private static void StopWatchdog()
        {
            if (_watchdogTimer == null) return;
            try { _watchdogTimer.Dispose(); } catch { }
            _watchdogTimer = null;
        }

        private static void WatchdogTick(object state)
        {
            List<KeyValuePair<IntPtr, string>> snapshot;
            lock (_lockSync)
            {
                snapshot = _lockedTitles.ToList();
            }

            var staleHandles = new List<IntPtr>();
            foreach (var kv in snapshot)
            {
                IntPtr hwnd = kv.Key;
                string desired = kv.Value;

                // 窗口已销毁 → 从守望列表移除
                if (!IsWindow(hwnd))
                {
                    staleHandles.Add(hwnd);
                    continue;
                }

                try
                {
                    string current = GetWindowTitle(hwnd);
                    if (!string.Equals(current, desired, StringComparison.Ordinal))
                    {
                        // 记录该进程自己修改后的最新原始标题，用于后续还原
                        if (!string.IsNullOrEmpty(current))
                            _originalTitles[hwnd] = current;

                        // 重新追加标志位并覆盖标题
                        IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
                        long styleVal = style.ToInt64() | TITLE_LOCK_FLAG;
                        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(styleVal));
                        SetWindowText(hwnd, desired);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Watchdog] hwnd=0x{hwnd.ToInt64():X} {ex.Message}");
                }
            }

            if (staleHandles.Count > 0)
            {
                lock (_lockSync)
                {
                    foreach (var h in staleHandles)
                        _lockedTitles.Remove(h);
                    if (_lockedTitles.Count == 0)
                        StopWatchdog();
                }
            }
        }

        /// <summary>
        /// 返回当前被锁定的窗口 → 锁定文字快照。
        /// </summary>
        public static IReadOnlyDictionary<IntPtr, string> GetLockedWindows()
        {
            lock (_lockSync)
            {
                return new Dictionary<IntPtr, string>(_lockedTitles);
            }
        }

        /// <summary>
        /// 根据本地保存的规则自动恢复锁定：枚举当前所有可见窗口，
        /// 匹配到《进程名 + 锁定前标题 +（进程目录可选）》后调用 LockWindowTitle。
        /// 应在应用启动后调用一次。
        /// </summary>
        /// <returns>本次自动恢复的窗口数量</returns>
        public static int RestoreLocksFromRules()
        {
            int restored = 0;
            try
            {
                var rules = TitleLockRuleManager.GetAll();
                if (rules == null || rules.Count == 0) return 0;

                var windows = GetTaskbarWindows();
                foreach (var w in windows)
                {
                    try
                    {
                        var details = GetWindowDetails(w.Handle, fullDetails: false);
                        if (details == null || string.IsNullOrEmpty(details.ProcessName)) continue;

                        var match = TitleLockRuleManager.FindMatch(
                            details.ProcessName,
                            details.Title,
                            details.WorkingDirectory);
                        if (match == null) continue;

                        // 符合条件 → 自动锁定。注意：persistRule=false，避免重复写规则。
                        if (LockWindowTitleCore(w.Handle, match.LockText, match.MatchPattern, match.MatchMode, persistRule: false))
                            restored++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RestoreLocksFromRules][{w.Title}] {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RestoreLocksFromRules] {ex.Message}");
            }

            // 启动重扫定时器：即使本轮未命中任何窗口，后续有应用启动时也能被识别。
            try
            {
                if (TitleLockRuleManager.GetAll().Count > 0)
                    EnsureRescanStarted();
            }
            catch { }
            return restored;
        }

        #endregion
    }
}
