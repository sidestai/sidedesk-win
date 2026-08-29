using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace SideDesk.Services
{
    /// <summary>
    /// 全局快捷键服务：低级键盘钩子实现两类全局热键。
    /// 1) Ctrl+1~9 切换窗口标签（命中会吞掉按键，可能与浏览器等应用冲突，默认关闭）；
    /// 2) 语音输入热键（默认启动/停止均为 Ctrl+B，同一键切换开始/结束，组合键可在设置中修改）。
    /// </summary>
    public sealed class GlobalHotkeyService
    {
        private static readonly Lazy<GlobalHotkeyService> _instance = new(() => new GlobalHotkeyService());
        public static GlobalHotkeyService Instance => _instance.Value;

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _proc;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>钩子是否已安装</summary>
        public bool IsRunning => _hookId != IntPtr.Zero;

        /// <summary>启动语音识别热键被按下（已按设置过滤，命中即被吞掉）</summary>
        public event Action? VoiceStartRequested;

        /// <summary>停止语音识别热键被按下</summary>
        public event Action? VoiceStopRequested;

        private GlobalHotkeyService() { }

        /// <summary>安装全局键盘钩子（幂等）</summary>
        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            _proc = HookCallback;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;
            _hookId = NativeMethods.SetWindowsHookEx(
                WH_KEYBOARD_LL, _proc,
                NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
        }

        /// <summary>卸载全局键盘钩子（幂等）</summary>
        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        /// <summary>按设置中各热键开关刷新钩子启停（任一功能开启即安装钩子）</summary>
        public static void RefreshState()
        {
            try
            {
                var settings = (Application.Current as App)?.AppSettings;
                if (settings == null) return;
                if (settings.EnableWindowSwitchHotkey || settings.EnableVoiceInputHotkey)
                    Instance.Start();
                else
                    Instance.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GlobalHotkey] failed to refresh enabled state: {ex.Message}");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var settings = (Application.Current as App)?.AppSettings;

                // 语音输入热键：停止组合更具体，优先匹配；修饰键必须完全一致（避免 Ctrl+Alt+B 同时命中 Ctrl+B）。
                // 启动/停止组合相同时视为切换键：先通知停止再通知启动，由订阅方按录音状态决定行为。
                if (settings is { EnableVoiceInputHotkey: true })
                {
                    bool ctrlDown = (NativeMethods.GetKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool altDown = (NativeMethods.GetKeyState(VK_MENU) & 0x8000) != 0;
                    bool shiftDown = (NativeMethods.GetKeyState(VK_SHIFT) & 0x8000) != 0;

                    bool sameCombo = string.Equals(settings.VoiceStartHotkey, settings.VoiceStopHotkey, StringComparison.OrdinalIgnoreCase);
                    bool stopHit = TryMatchHotkey(settings.VoiceStopHotkey, data.vkCode, ctrlDown, altDown, shiftDown);
                    bool startHit = stopHit && sameCombo
                        || TryMatchHotkey(settings.VoiceStartHotkey, data.vkCode, ctrlDown, altDown, shiftDown);

                    if (stopHit || startHit)
                    {
                        if (stopHit) FireOnUI(() => VoiceStopRequested?.Invoke());
                        if (startHit) FireOnUI(() => VoiceStartRequested?.Invoke());
                        return (IntPtr)1;
                    }
                }

                if (settings is { EnableWindowSwitchHotkey: true })
                {
                    bool ctrlDown = (NativeMethods.GetKeyState(VK_CONTROL) & 0x8000) != 0;
                    if (ctrlDown)
                    {
                        int index = -1;
                        if (data.vkCode >= 0x31 && data.vkCode <= 0x39) index = (int)(data.vkCode - 0x31);       // 主键盘 1~9
                        else if (data.vkCode >= 0x61 && data.vkCode <= 0x69) index = (int)(data.vkCode - 0x61);  // 小键盘 1~9

                        if (index >= 0)
                        {
                            int idx = index;
                            FireOnUI(() => (Application.Current as App)?.SwitchWindowTabByHotkey(idx));
                            return (IntPtr)1; // 吞掉按键，阻止传递给前台应用
                        }
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>事件统一投递到 UI 线程</summary>
        private static void FireOnUI(Action action)
        {
            Application.Current?.Dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// 匹配形如 "Ctrl+Alt+B" 的热键字符串：修饰键状态必须与组合完全一致，主键虚拟键码一致。
        /// 主键支持字母（A~Z）、数字（0~9）与 F1~F12。
        /// </summary>
        private static bool TryMatchHotkey(string? combo, uint vkCode, bool ctrlDown, bool altDown, bool shiftDown)
        {
            if (string.IsNullOrWhiteSpace(combo)) return false;
            bool needCtrl = false, needAlt = false, needShift = false;
            string? main = null;

            foreach (string part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string p = part.Trim().ToLowerInvariant();
                switch (p)
                {
                    case "ctrl": case "control": needCtrl = true; break;
                    case "alt": needAlt = true; break;
                    case "shift": needShift = true; break;
                    default: main = part.Trim(); break;
                }
            }

            if (main == null) return false;
            if (needCtrl != ctrlDown || needAlt != altDown || needShift != shiftDown) return false;

            uint targetVk;
            if (main.Length == 1)
            {
                char c = char.ToUpperInvariant(main[0]);
                if (c >= 'A' && c <= 'Z') targetVk = (uint)c;
                else if (c >= '0' && c <= '9') targetVk = (uint)c;
                else return false;
            }
            else if (main.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(main.AsSpan(1), out int fn) && fn >= 1 && fn <= 12)
            {
                targetVk = 0x70u + (uint)(fn - 1); // F1~F12
            }
            else return false;

            return vkCode == targetVk;
        }
    }
}
