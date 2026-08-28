using System;
using System.Windows;

namespace SideDesk
{
    /// <summary>
    /// 全局语音输入热键编排：启动键开始语音识别（底部语音按钮状态同步变化），
    /// 识别内容实时显示在 AI推荐 浮动窗；停止键结束识别并把累计文字粘贴到当前焦点位置。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>初始化语音热键订阅（窗口加载时调用，幂等由 _fcInited 同级时机保证只调一次）</summary>
        private void InitVoiceHotkey()
        {
            Services.GlobalHotkeyService.Instance.VoiceStartRequested += VoiceHotkey_OnStart;
            Services.GlobalHotkeyService.Instance.VoiceStopRequested += VoiceHotkey_OnStop;

            // 常驻订阅语音面板事件：识别文本实时刷新浮动窗（热键/按钮长按均适用）；按钮松开后显示并复制
            var panel = VoiceHotkeyPanel;
            if (panel != null)
            {
                panel.VoiceTextChanged += VoiceHotkey_OnTextChanged;
                panel.VoiceButtonSessionCompleted += VoiceButton_OnCompleted;
            }

            Closed += (_, _) =>
            {
                Services.GlobalHotkeyService.Instance.VoiceStartRequested -= VoiceHotkey_OnStart;
                Services.GlobalHotkeyService.Instance.VoiceStopRequested -= VoiceHotkey_OnStop;
                var p = VoiceHotkeyPanel;
                if (p != null)
                {
                    p.VoiceTextChanged -= VoiceHotkey_OnTextChanged;
                    p.VoiceButtonSessionCompleted -= VoiceButton_OnCompleted;
                }
            };
        }

        /// <summary>本窗口语音面板（本地模式面板内的语音输入工具）</summary>
        private Controls.NativeModePanel? VoiceHotkeyPanel => sidebarPanel?.nativePanel;

        /// <summary>启动热键：开始语音识别；识别内容实时刷新到 AI推荐 浮动窗</summary>
        private void VoiceHotkey_OnStart()
        {
            try
            {
                var panel = VoiceHotkeyPanel;
                if (panel == null) return;
                // 已有主窗口在录音则不重复启动
                if (panel.IsVoiceRecording) return;
                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MainWindow other && other != this && other.VoiceHotkeyPanel?.IsVoiceRecording == true)
                        return;
                }

                if (!panel.StartVoiceFromHotkey()) return;

                // 识别内容展示在 AI推荐 浮动窗（浮动窗总开关关闭时跳过展示，不影响识别与粘贴）
                if (Framework.AppSettings.Instance.FloatingContextEnabled)
                    ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"),
                        Framework.Lang.T("nativePanel.listening"), "voice");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceHotkey] 启动异常: {ex.Message}");
            }
        }

        /// <summary>识别文本实时更新：刷新浮动窗内容</summary>
        private void VoiceHotkey_OnTextChanged(string text)
        {
            try
            {
                if (!Framework.AppSettings.Instance.FloatingContextEnabled) return;
                string content = string.IsNullOrWhiteSpace(text) ? Framework.Lang.T("nativePanel.listening") : text;
                ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"), content, "voice");
            }
            catch { }
        }

        /// <summary>停止热键：结束识别，把累计文字粘贴到当前焦点位置</summary>
        private void VoiceHotkey_OnStop()
        {
            try
            {
                var panel = VoiceHotkeyPanel;
                if (panel == null) return;
                string text = panel.StopVoiceFromHotkey();
                if (string.IsNullOrWhiteSpace(text)) return;

                // 浮动窗保留最终文本，方便继续查看或点「问AI」
                if (Framework.AppSettings.Instance.FloatingContextEnabled)
                    ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"), text, "voice");

                PasteTextToForeground(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceHotkey] 停止异常: {ex.Message}");
            }
        }

        /// <summary>按钮按住说话结束（松开）：累计文本显示到 AI推荐 浮动窗，并复制到剪贴板</summary>
        private void VoiceButton_OnCompleted(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                if (Framework.AppSettings.Instance.FloatingContextEnabled)
                    ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"), text, "voice");

                try { Clipboard.SetText(text); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceButton] 结束异常: {ex.Message}");
            }
        }

        /// <summary>把文本粘贴到当前前台窗口的焦点位置（记录前台窗口 → 剪贴板 → 模拟 Ctrl+V → 恢复原剪贴板文本）</summary>
        private static void PasteTextToForeground(string text)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            string? prevClip = null;
            try { if (Clipboard.ContainsText()) prevClip = Clipboard.GetText(); } catch { }

            Clipboard.SetText(text);
            try
            {
                // 确保粘贴目标是触发前的前台窗口（期间 SideDesk 自身窗口不应抢占焦点）
                if (fg != IntPtr.Zero) NativeMethods.SetForegroundWindow(fg);
                SendCtrlV();
            }
            finally
            {
                // 延迟恢复剪贴板，给前台应用完成粘贴留出时间
                _ = System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (prevClip != null) Clipboard.SetText(prevClip);
                        }
                        catch { }
                    }));
                });
            }
        }

        /// <summary>模拟按下并释放 Ctrl+V</summary>
        private static void SendCtrlV()
        {
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL_KEY, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V_KEY, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V_KEY, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL_KEY, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
