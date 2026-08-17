using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace SideDesk.Services
{
    /// <summary>
    /// 低级键盘钩子服务：全局按键计数，支持持久化
    /// </summary>
    public class KeyboardHookService
    {
        private IntPtr _keyboardHookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
        private int _todayKeyCount = 0;
        private string _todayKeyCountDate = DateTime.Now.ToString("yyyy-MM-dd");
        private string _logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "Logs", "keyboard.log");

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Keys to ignore (function keys, modifiers, etc.)
        private static readonly HashSet<uint> _ignoredKeys = new()
        {
            0x08, // Backspace
            0x09, // Tab
            0x0D, // Enter
            0x10, // Shift
            0x11, // Ctrl
            0x12, // Alt
            0x13, // Pause
            0x14, // Caps Lock
            0x1B, // Escape
            0x20, // Space
            0x21, // Page Up
            0x22, // Page Down
            0x23, // End
            0x24, // Home
            0x25, // Left
            0x26, // Up
            0x27, // Right
            0x28, // Down
            0x2D, // Insert
            0x2E, // Delete
            0x5B, // Left Windows
            0x5C, // Right Windows
            0x5D, // Applications
            0x6F, // Num Divide
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, // F1-F6
            0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, // F7-F12
            0x90, // Num Lock
            0x91, // Scroll Lock
            0xA0, 0xA1, // Left/Right Shift
            0xA2, 0xA3, // Left/Right Ctrl
            0xA4, 0xA5, // Left/Right Alt
        };

        /// <summary>今日按键总数</summary>
        public int TodayKeyCount => _todayKeyCount;

        /// <summary>格式化计数显示（千及以上用 k/M 紧凑表示，节省宽度）</summary>
        public static string FormatCount(long count)
        {
            if (count < 1000)
                return count.ToString();
            else if (count < 1000000)
                return $"{count / 1000.0:F1}k";
            else
                return $"{count / 1000000.0:F2}M";
        }

        /// <summary>按键计数变化事件</summary>
        public event Action<int>? KeyCountChanged;

        /// <summary>启动键盘钩子</summary>
        public void Start()
        {
            LoadKeyCount();
            InstallHook();
        }

        /// <summary>停止键盘钩子并保存数据</summary>
        public void Stop()
        {
            UninstallHook();
            SaveKeyCount();
        }

        private void InstallHook()
        {
            if (_keyboardHookId != IntPtr.Zero) return;

            _keyboardProc = KeyboardHookCallback;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;
            _keyboardHookId = NativeMethods.SetWindowsHookEx(
                WH_KEYBOARD_LL, _keyboardProc,
                NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
        }

        private void UninstallHook()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
                bool isSysKey = wParam == (IntPtr)WM_SYSKEYDOWN || wParam == (IntPtr)WM_SYSKEYUP;
                
                if (isKeyDown || isKeyUp)
                {
                    string keyName = GetKeyName(hookStruct.vkCode);
                    string action = isKeyDown ? "DOWN" : "UP";
                    string sysMark = isSysKey ? "[SYS]" : "";
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    string logLine = $"[{timestamp}] {sysMark}{keyName} {action} (vkCode=0x{hookStruct.vkCode:X2})";
                    
                    // 写入日志
                    WriteLog(logLine);
                    
                    // 特别记录ALT+TAB
                    if (hookStruct.vkCode == 0x09 && (NativeMethods.GetKeyState(0x12) & 0x8000) != 0)
                    {
                        string altTabLog = $"[{timestamp}] *** ALT+TAB DETECTED ***";
                        WriteLog(altTabLog);
                        Debug.WriteLine(altTabLog);
                    }
                    
                    // 特别记录可能导致退出的组合键
                    if (hookStruct.vkCode == 0x73) // F4
                    {
                        if ((NativeMethods.GetKeyState(0x12) & 0x8000) != 0) // Alt+F4
                        {
                            string altF4Log = Framework.Lang.TF("[{0}] *** ALT+F4 DETECTED (可能关闭窗口) ***", timestamp);
                            WriteLog(altF4Log);
                            Debug.WriteLine(altF4Log);
                        }
                    }

                    // Check if date changed
                    string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                    if (currentDate != _todayKeyCountDate)
                    {
                        SaveKeyCount();
                        _todayKeyCountDate = currentDate;
                        _todayKeyCount = 0;
                    }

                    // Count only non-ignored keys
                    if (isKeyDown && !_ignoredKeys.Contains(hookStruct.vkCode))
                    {
                        _todayKeyCount++;
                        KeyCountChanged?.Invoke(_todayKeyCount);
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }
        
        /// <summary>
        /// 获取按键名称
        /// </summary>
        private string GetKeyName(uint vkCode)
        {
            // 数字 0-9
            if (vkCode >= 0x30 && vkCode <= 0x39)
                return $"{(char)('0' + (vkCode - 0x30))}";
            
            // 字母 A-Z
            if (vkCode >= 0x41 && vkCode <= 0x5A)
                return $"{(char)('A' + (vkCode - 0x41))}";
            
            // 数字键 0-9
            if (vkCode >= 0x60 && vkCode <= 0x69)
                return $"NUMPAD_{(vkCode - 0x60)}";
            
            // F1-F24
            if (vkCode >= 0x70 && vkCode <= 0x87)
                return $"F{(vkCode - 0x70 + 1)}";
            
            return vkCode switch
            {
                0x08 => "BACKSPACE",
                0x09 => "TAB",
                0x0D => "ENTER",
                0x10 => "SHIFT",
                0x11 => "CTRL",
                0x12 => "ALT",
                0x13 => "PAUSE",
                0x14 => "CAPS_LOCK",
                0x1B => "ESC",
                0x20 => "SPACE",
                0x21 => "PAGE_UP",
                0x22 => "PAGE_DOWN",
                0x23 => "END",
                0x24 => "HOME",
                0x25 => "LEFT",
                0x26 => "UP",
                0x27 => "RIGHT",
                0x28 => "DOWN",
                0x2D => "INSERT",
                0x2E => "DELETE",
                0x5B => "WIN_LEFT",
                0x5C => "WIN_RIGHT",
                0x5D => "APPS",
                0x6A => "NUMPAD_MUL",
                0x6B => "NUMPAD_ADD",
                0x6C => "NUMPAD_SEP",
                0x6D => "NUMPAD_SUB",
                0x6E => "NUMPAD_DEC",
                0x6F => "NUMPAD_DIV",
                0x90 => "NUM_LOCK",
                0x91 => "SCROLL_LOCK",
                0xA0 => "SHIFT_LEFT",
                0xA1 => "SHIFT_RIGHT",
                0xA2 => "CTRL_LEFT",
                0xA3 => "CTRL_RIGHT",
                0xA4 => "ALT_LEFT",
                0xA5 => "ALT_RIGHT",
                _ => $"VK_0x{vkCode:X2}"
            };
        }
        
        /// <summary>
        /// 写入日志文件
        /// </summary>
        private void WriteLog(string message)
        {
            try
            {
                string logDir = Path.GetDirectoryName(_logFilePath)!;
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                
                // 每天一个日志文件
                string dailyLogFile = _logFilePath.Replace(".log", $"-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(dailyLogFile, message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyboardHook] 写入日志失败: {ex.Message}");
            }
        }

        private void LoadKeyCount()
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                // 主存储：SQLite；首次时从旧 keycount.json 一次性迁入全量历史
                if (LocalDbService.DbExists)
                {
                    ImportLegacyKeyCountJsonOnce();
                    var v = LocalDbService.LoadKeyCount(today);
                    if (v.HasValue)
                    {
                        _todayKeyCount = v.Value;
                        _todayKeyCountDate = today;
                    }
                    return;
                }
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string filePath = Path.Combine(appData, "SideDesk", "keycount.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                    if (data != null && data.ContainsKey(today))
                    {
                        _todayKeyCount = data[today];
                        _todayKeyCountDate = today;
                    }
                }
            }
            catch { }
        }

        /// <summary>旧 keycount.json 一次性迁入 SQLite（导入后改名 .bak-migrated 保留）。</summary>
        private static void ImportLegacyKeyCountJsonOnce()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
                string filePath = Path.Combine(folder, "keycount.json");
                if (!File.Exists(filePath)) return;
                var data = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(filePath));
                if (data != null && data.Count > 0)
                    LocalDbService.ImportKeyCounts(data);
                try { File.Move(filePath, filePath + ".bak-migrated", overwrite: true); } catch { }
            }
            catch { }
        }

        private void SaveKeyCount()
        {
            try
            {
                if (LocalDbService.DbExists)
                {
                    ImportLegacyKeyCountJsonOnce();
                    try { LocalDbService.SaveKeyCount(_todayKeyCountDate, _todayKeyCount); return; }
                    catch { }
                }
                // db 不可用/未建立：退回旧 JSON 存储
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(appData, "SideDesk");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string filePath = Path.Combine(folder, "keycount.json");

                Dictionary<string, int> data = new();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var existing = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                    if (existing != null) data = existing;
                }

                data[_todayKeyCountDate] = _todayKeyCount;
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch { }
        }
    }

    /// <summary>
    /// 低级鼠标钩子服务：全局鼠标移动距离计数，支持持久化
    /// </summary>
    public class MouseHookService
    {
        private IntPtr _mouseHookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _mouseProc; // same signature
        private long _todayMouseDistance = 0; // in pixels
        private string _todayMouseDate = DateTime.Now.ToString("yyyy-MM-dd");
        private int _lastMouseX = 0;
        private int _lastMouseY = 0;
        private bool _mouseFirstMove = true;

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int x;
            public int y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>今日鼠标移动距离（像素）</summary>
        public long TodayMouseDistance => _todayMouseDistance;

        /// <summary>鼠标距离变化事件</summary>
        public event Action<long>? MouseDistanceChanged;

        /// <summary>启动鼠标钩子</summary>
        public void Start()
        {
            LoadMouseDistance();
            InstallHook();
        }

        /// <summary>停止鼠标钩子并保存数据</summary>
        public void Stop()
        {
            UninstallHook();
            SaveMouseDistance();
        }

        private void InstallHook()
        {
            if (_mouseHookId != IntPtr.Zero) return;

            _mouseProc = MouseHookCallback;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;
            _mouseHookId = NativeMethods.SetWindowsHookEx(
                WH_MOUSE_LL, _mouseProc,
                NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
        }

        private void UninstallHook()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
            {
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Check if date changed
                string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                if (currentDate != _todayMouseDate)
                {
                    SaveMouseDistance();
                    _todayMouseDate = currentDate;
                    _todayMouseDistance = 0;
                    _mouseFirstMove = true;
                }

                if (_mouseFirstMove)
                {
                    _lastMouseX = hookStruct.x;
                    _lastMouseY = hookStruct.y;
                    _mouseFirstMove = false;
                }
                else
                {
                    int dx = hookStruct.x - _lastMouseX;
                    int dy = hookStruct.y - _lastMouseY;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    _todayMouseDistance += (long)distance;
                    _lastMouseX = hookStruct.x;
                    _lastMouseY = hookStruct.y;

                    MouseDistanceChanged?.Invoke(_todayMouseDistance);
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private void LoadMouseDistance()
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                // 主存储：SQLite；首次时从旧 mousedistance.json 一次性迁入全量历史
                if (LocalDbService.DbExists)
                {
                    ImportLegacyMouseDistanceJsonOnce();
                    var v = LocalDbService.LoadMouseDistance(today);
                    if (v.HasValue)
                    {
                        _todayMouseDistance = v.Value;
                        _todayMouseDate = today;
                    }
                    return;
                }
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string filePath = Path.Combine(appData, "SideDesk", "mousedistance.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, long>>(json);
                    if (data != null && data.ContainsKey(today))
                    {
                        _todayMouseDistance = data[today];
                        _todayMouseDate = today;
                    }
                }
            }
            catch { }
        }

        /// <summary>旧 mousedistance.json 一次性迁入 SQLite（导入后改名 .bak-migrated 保留）。</summary>
        private static void ImportLegacyMouseDistanceJsonOnce()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
                string filePath = Path.Combine(folder, "mousedistance.json");
                if (!File.Exists(filePath)) return;
                var data = JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(filePath));
                if (data != null && data.Count > 0)
                    LocalDbService.ImportMouseDistances(data);
                try { File.Move(filePath, filePath + ".bak-migrated", overwrite: true); } catch { }
            }
            catch { }
        }

        private void SaveMouseDistance()
        {
            try
            {
                if (LocalDbService.DbExists)
                {
                    ImportLegacyMouseDistanceJsonOnce();
                    try { LocalDbService.SaveMouseDistance(_todayMouseDate, _todayMouseDistance); return; }
                    catch { }
                }
                // db 不可用/未建立：退回旧 JSON 存储
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(appData, "SideDesk");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string filePath = Path.Combine(folder, "mousedistance.json");

                Dictionary<string, long> data = new();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var existing = JsonConvert.DeserializeObject<Dictionary<string, long>>(json);
                    if (existing != null) data = existing;
                }

                data[_todayMouseDate] = _todayMouseDistance;
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch { }
        }

        /// <summary>格式化距离显示</summary>
        public static string FormatDistance(long pixels)
        {
            if (pixels < 1000)
                return $"{pixels}px";
            else if (pixels < 1000000)
                return $"{pixels / 1000.0:F1}k";
            else
                return $"{pixels / 1000000.0:F2}M";
        }
    }
}
