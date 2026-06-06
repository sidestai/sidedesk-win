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

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

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
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                KBDLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Check if date changed
                string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                if (currentDate != _todayKeyCountDate)
                {
                    SaveKeyCount();
                    _todayKeyCountDate = currentDate;
                    _todayKeyCount = 0;
                }

                // Count only non-ignored keys
                if (!_ignoredKeys.Contains(hookStruct.vkCode))
                {
                    _todayKeyCount++;
                    KeyCountChanged?.Invoke(_todayKeyCount);
                }
            }
            return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        private void LoadKeyCount()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string filePath = Path.Combine(appData, "SideDesk", "keycount.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (data != null && data.ContainsKey(today))
                    {
                        _todayKeyCount = data[today];
                        _todayKeyCountDate = today;
                    }
                }
            }
            catch { }
        }

        private void SaveKeyCount()
        {
            try
            {
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
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string filePath = Path.Combine(appData, "SideDesk", "mousedistance.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, long>>(json);
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (data != null && data.ContainsKey(today))
                    {
                        _todayMouseDistance = data[today];
                        _todayMouseDate = today;
                    }
                }
            }
            catch { }
        }

        private void SaveMouseDistance()
        {
            try
            {
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
