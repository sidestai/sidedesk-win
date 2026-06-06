using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace SideDesk.Windows
{
    public class WindowLogger : IDisposable
    {
        private Timer _timer;
        private readonly string _logFolder;
        private string _currentLogFile;
        private DateTime _currentDate;
        private readonly object _lock = new object();
        private bool _isRunning = false;
        private string _lastWindowKey = null;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public WindowLogger()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logFolder = Path.Combine(appData, "SideDesk", "WindowLogs");
            if (!Directory.Exists(_logFolder))
                Directory.CreateDirectory(_logFolder);

            UpdateLogFilePath();
        }

        private void UpdateLogFilePath()
        {
            DateTime now = DateTime.Now;
            if (_currentDate.Date != now.Date || string.IsNullOrEmpty(_currentLogFile))
            {
                _currentDate = now.Date;
                _currentLogFile = Path.Combine(_logFolder, $"windowlog_{_currentDate:yyyyMMdd}.txt");
            }
        }

        public void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            LogCurrentWindow();
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnTimerTick(object state)
        {
            try
            {
                LogCurrentWindow();
            }
            catch
            {
                // Ignore errors during logging
            }
        }

        private void LogCurrentWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return;

            var sbTitle = new StringBuilder(512);
            var sbClass = new StringBuilder(256);
            GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
            GetClassName(hwnd, sbClass, sbClass.Capacity);

            uint processId = 0;
            GetWindowThreadProcessId(hwnd, out processId);

            string processName = "Unknown";
            string exePath = "Unknown";
            try
            {
                var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                try
                {
                    exePath = process.MainModule?.FileName ?? processName;
                }
                catch
                {
                    exePath = processName;
                }
            }
            catch
            {
                // Process may have exited
            }

            string title = sbTitle.ToString().Replace("\t", " ").Replace("\n", " ").Replace("\r", " ");
            string className = sbClass.ToString();

            // TODO: integrate later - WeChat region recognizer for contact name detection
            // 微信特殊处理暂时跳过

            // Try to get browser URL
            string url = "";
            if (IsBrowserProcess(processName))
            {
                try
                {
                    url = TryGetBrowserUrl(hwnd, processName) ?? "";
                }
                catch
                {
                    // Ignore UIA errors
                }
            }

            // Only log if window info changed
            string windowKey = $"{className}|{title}|{processName}|{exePath}|{url}";
            if (windowKey == _lastWindowKey)
                return;

            _lastWindowKey = windowKey;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            lock (_lock)
            {
                UpdateLogFilePath();
                string logLine = $"{timestamp}\t{className}\t{title}\t{processName}\t{exePath}\t{url}";
                File.AppendAllText(_currentLogFile, logLine + Environment.NewLine);
            }
        }

        private bool IsBrowserProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;
            string p = processName.ToLowerInvariant();
            return p == "chrome" || p == "msedge" || p == "firefox" || p == "opera" || p == "brave" || p == "vivaldi" || p == "waterfox";
        }

        private string TryGetBrowserUrl(IntPtr hwnd, string processName)
        {
            try
            {
                var element = AutomationElement.FromHandle(hwnd);
                if (element == null)
                    return null;

                // Chrome / Edge / Brave / Vivaldi
                if (processName.ToLowerInvariant() == "chrome" || processName.ToLowerInvariant() == "msedge" || processName.ToLowerInvariant() == "brave" || processName.ToLowerInvariant() == "vivaldi")
                {
                    var cond = new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Address and search bar"),
                        new PropertyCondition(AutomationElement.NameProperty, "地址和搜索栏"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "addressEditBox")
                    );

                    var addrBar = element.FindFirst(TreeScope.Descendants, cond);
                    if (addrBar != null)
                    {
                        if (addrBar.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                        {
                            return ((ValuePattern)pattern).Current.Value;
                        }
                    }

                    // Fallback: search by ControlType.Edit
                    var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                    var edits = element.FindAll(TreeScope.Descendants, editCond);
                    for (int i = 0; i < edits.Count; i++)
                    {
                        var edit = edits[i];
                        string name = edit.Current.Name ?? "";
                        string nameLower = name.ToLowerInvariant();
                        if (nameLower.Contains("address") || nameLower.Contains("search") || nameLower.Contains("url")
                            || name.Contains("地址") || name.Contains("搜索"))
                        {
                            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object p))
                            {
                                string val = ((ValuePattern)p).Current.Value;
                                if (!string.IsNullOrEmpty(val) && (val.StartsWith("http") || val.StartsWith("file")))
                                    return val;
                            }
                        }
                    }
                }
                // Firefox
                else if (processName.ToLowerInvariant() == "firefox" || processName.ToLowerInvariant() == "waterfox")
                {
                    var cond = new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Search or enter address"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input")
                    );
                    var addrBar = element.FindFirst(TreeScope.Descendants, cond);
                    if (addrBar != null && addrBar.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                    {
                        return ((ValuePattern)pattern).Current.Value;
                    }

                    // Fallback
                    var docCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
                    var doc = element.FindFirst(TreeScope.Descendants, docCond);
                    if (doc != null)
                    {
                        string val = doc.Current.Name;
                        if (!string.IsNullOrEmpty(val) && val.StartsWith("http"))
                            return val;
                    }
                }
                // Opera
                else if (processName.ToLowerInvariant() == "opera")
                {
                    var cond = new PropertyCondition(AutomationElement.NameProperty, "Address field");
                    var addrBar = element.FindFirst(TreeScope.Descendants, cond);
                    if (addrBar != null && addrBar.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                    {
                        return ((ValuePattern)pattern).Current.Value;
                    }
                }
            }
            catch
            {
                // UI Automation may fail on some windows
            }
            return null;
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
