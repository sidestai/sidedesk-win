using System;
using System.Windows;

namespace SideDesk
{
    /// <summary>
    /// 手机扫码远程语音输入编排：手机扫码配对后按住说话，音频经后端 WS 中继到本程序流式转写，
    /// 手机松手结束识别并把累计文字粘贴到当前焦点位置（会话事件在 WS 线程触发，均切本窗口 UI 线程处理）。
    /// 多窗口去重：带归属认领（MobileVoiceService.TryClaimUi(owner)），仅 owner 窗口处理停止/中止，
    /// 非 owner 窗口不动标志、不停面板。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>初始化手机扫码远程语音订阅（窗口加载时调用，紧邻 <see cref="InitVoiceHotkey"/>）</summary>
        private void InitMobileVoice()
        {
            var svc = Services.MobileVoiceService.Instance;
            svc.SessionStarted += MobileVoice_OnSessionStarted;
            svc.SessionStopRequested += MobileVoice_OnSessionStop;
            svc.SessionAborted += MobileVoice_OnSessionAborted;

            Closed += (_, _) =>
            {
                svc.SessionStarted -= MobileVoice_OnSessionStarted;
                svc.SessionStopRequested -= MobileVoice_OnSessionStop;
                svc.SessionAborted -= MobileVoice_OnSessionAborted;
            };
        }

        /// <summary>手机配对成功：认领会话并用中继音频流启动远程识别（识别文本经面板 VoiceTextChanged 实时刷新浮动窗，复用热键路径订阅链路）</summary>
        private void MobileVoice_OnSessionStarted(string code)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var svc = Services.MobileVoiceService.Instance;
                    var panel = VoiceHotkeyPanel;

                    // 本窗口不具备启动条件（无面板/正在录音）：若尚无窗口认领，发 voice_stop 终结会话，
                    // 让后端销毁配对并通知手机（契约：exe 可发 voice_stop 终结会话）
                    if (panel == null || panel.IsVoiceRecording)
                    {
                        if (!svc.IsUiClaimed) await svc.FailSessionAsync(code);
                        return;
                    }

                    // 已有其它主窗口在录音则不启动（同热键路径去重模式）
                    foreach (Window w in Application.Current.Windows)
                    {
                        if (w is MainWindow other && other != this && other.VoiceHotkeyPanel?.IsVoiceRecording == true)
                        {
                            if (!svc.IsUiClaimed) await svc.FailSessionAsync(code);
                            return;
                        }
                    }

                    // 多主窗口：同一会话仅首个窗口认领处理；已有窗口认领时本窗口不做任何事（不发 voice_stop）
                    if (!svc.TryClaimUi(this)) return;

                    var stream = svc.CurrentAudioStream;
                    if (stream == null || !panel.StartVoiceFromRemote(stream))
                    {
                        // 认领后启动失败：发 voice_stop 终结会话（后端销毁配对并通知手机），并复位认领
                        await svc.FailSessionAsync(code);
                        svc.ReleaseUiClaim(this);
                        return;
                    }

                    // 远程聆听展示在 AI推荐 浮动窗（总开关关闭时跳过展示，不影响识别与粘贴）
                    if (Framework.AppSettings.Instance.FloatingContextEnabled)
                        ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"),
                            Framework.Lang.T("nativePanel.voiceRemoteListening"), "voice");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MobileVoice] session start error: {ex.Message}");
                }
            }));
        }

        /// <summary>手机松手：停止识别，把累计文字粘贴到当前焦点位置（仅 owner 窗口处理；识别收尾略有延迟，与热键路径同为同步取文本方式）</summary>
        private void MobileVoice_OnSessionStop()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var svc = Services.MobileVoiceService.Instance;
                    // 非 owner 窗口：不动标志、不停面板，直接忽略
                    if (!svc.IsUiClaimedBy(this)) return;

                    var panel = VoiceHotkeyPanel;
                    string text = panel?.StopVoiceFromRemote() ?? "";
                    svc.ReleaseUiClaim(this);
                    if (string.IsNullOrWhiteSpace(text)) return;

                    // 浮动窗保留最终文本，方便继续查看或点「问AI」
                    if (Framework.AppSettings.Instance.FloatingContextEnabled)
                        ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"), text, "voice");

                    PasteTextToForeground(text);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MobileVoice] session stop error: {ex.Message}");
                }
            }));
        }

        /// <summary>
        /// 手机断连/超时（含后端 WS 断连看门狗）：停止识别并恢复麦克风（仅 owner 窗口处理）。
        /// 取舍说明（与热键路径保持一致）：已识别出文本时同样粘贴，避免用户已说内容丢失；无文本则静默收尾。
        /// </summary>
        private void MobileVoice_OnSessionAborted()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var svc = Services.MobileVoiceService.Instance;
                    // 非 owner 窗口：不动标志、不停面板，直接忽略
                    if (!svc.IsUiClaimedBy(this)) return;

                    var panel = VoiceHotkeyPanel;
                    string text = panel?.StopVoiceFromRemote() ?? "";
                    svc.ReleaseUiClaim(this);
                    if (string.IsNullOrWhiteSpace(text)) return;

                    if (Framework.AppSettings.Instance.FloatingContextEnabled)
                        ShowFloatingContext(FloatingContextSource.Voice, Framework.Lang.T("settings.aiPicks"), text, "voice");

                    PasteTextToForeground(text);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MobileVoice] session abort error: {ex.Message}");
                }
            }));
        }
    }
}
