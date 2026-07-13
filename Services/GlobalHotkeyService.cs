using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace SideDesk.Services
{
    /// <summary>
    /// 全局快捷键服务：低级键盘钩子监听 Ctrl+1~9，在任意应用中全局切换当前窗口标签。
    /// 注意：命中时会吞掉该按键，可能与浏览器/编辑器等应用的 Ctrl+数字切标签冲突，故默认关闭，由用户在设置中开启。
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

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ctrlDown = (NativeMethods.GetKeyState(VK_CONTROL) & 0x8000) != 0;
                if (ctrlDown)
                {
                    int index = -1;
                    if (data.vkCode >= 0x31 && data.vkCode <= 0x39) index = (int)(data.vkCode - 0x31);       // 主键盘 1~9
                    else if (data.vkCode >= 0x61 && data.vkCode <= 0x69) index = (int)(data.vkCode - 0x61);  // 小键盘 1~9

                    if (index >= 0)
                    {
                        int idx = index;
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            (Application.Current as App)?.SwitchWindowTabByHotkey(idx);
                        }));
                        return (IntPtr)1; // 吞掉按键，阻止传递给前台应用
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
    }
}
