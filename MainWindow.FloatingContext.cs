using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>
    /// 浮动上下文窗口集成：订阅输入框焦点与剪贴板事件驱动显隐，
    /// 处理内容区垂直拖拽、双击精简模式切换与位置持久化。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>当前上下文来源（用于隐藏策略判定；Voice 为语音热键会话，录音期间不自动隐藏）</summary>
        private enum FloatingContextSource { None, InputBox, Clipboard, Voice }

        private FloatingContextSource _fcSource = FloatingContextSource.None;
        private bool _fcInited;
        private bool _fcSourcesStarted;
        private bool _fcDragging;
        private double _fcDragStartY;
        private double _fcDragStartTop;
        /// <summary>当前显示内容的签名</summary>
        private string _fcCurrentSignature = "";
        /// <summary>输入框失焦后的延迟隐藏计时器（10 秒）</summary>
        private DispatcherTimer? _fcAutoHideTimer;
        /// <summary>自上次显示内容以来用户是否交互过（点击/拖拽/双击），交互后本轮失焦不自动隐藏</summary>
        private bool _fcUserInteracted;
        /// <summary>失焦后延迟隐藏的等待时长</summary>
        private static readonly TimeSpan FcAutoHideDelay = TimeSpan.FromSeconds(10);
        /// <summary>内容显示的最大长度</summary>
        private const int FcMaxContentLength = 4000;
        /// <summary>“问AI”使用的内置对话窗实例（复用，支持向已打开窗口追加提交）</summary>
        private AIChatDialog? _fcAiDialog;

        /// <summary>初始化浮动上下文窗口（窗口加载时调用，幂等）</summary>
        private void InitFloatingContext()
        {
            if (_fcInited) return;
            _fcInited = true;

            floatingContext.CloseRequested += FloatingContext_CloseRequested;
            floatingContext.AskAiRequested += FloatingContext_AskAiRequested;
            floatingContext.MouseLeftButtonDown += FloatingContext_MouseDown;
            floatingContext.MouseMove += FloatingContext_MouseMove;
            floatingContext.MouseLeftButtonUp += FloatingContext_MouseUp;
            floatingContext.MouseDoubleClick += FloatingContext_DoubleClick;
            floatingContextLayer.SizeChanged += (_, _) => ClampFloatingContextPosition();
            Closed += (_, _) => FloatingContext_OnWindowClosed();

            floatingContext.SetCompact(Framework.AppSettings.Instance.FloatingContextCompact);

            if (Framework.AppSettings.Instance.FloatingContextEnabled)
                StartFloatingContextSources();
        }

        /// <summary>设置页开关回调：启用/停用浮动上下文窗口</summary>
        public void ApplyFloatingContextEnabled(bool enabled)
        {
            if (!_fcInited) return;
            if (enabled) StartFloatingContextSources();
            else StopFloatingContextSources(hidePanel: true);
        }

        /// <summary>“问AI”：弹出 AI 助手内置对话窗并把当前内容作为用户消息提交</summary>
        private void FloatingContext_AskAiRequested(string content)
        {
            FloatingContext_UserInteracted();
            if (string.IsNullOrWhiteSpace(content)) return;

            try
            {
                if (_fcAiDialog == null)
                    _fcAiDialog = new AIChatDialog();

                if (_fcAiDialog.IsOpen)
                {
                    _ = _fcAiDialog.SubmitText(content);
                    return;
                }

                // 内置模式支持程序化提交，固定走本地对话窗（位置与侧边栏 AI 助手一致：主窗口右侧）
                _fcAiDialog.Show(this,
                    Left + Width, Top, 400, Height, content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FloatingContext] 问AI 失败: {ex.Message}");
            }
        }

        /// <summary>启动上下文来源（剪贴板监听 + 输入框焦点检测），幂等</summary>
        private void StartFloatingContextSources()
        {
            if (_fcSourcesStarted) return;
            _fcSourcesStarted = true;

            var clipboard = ClipboardService.Instance;
            if (!clipboard.IsRunning)
                clipboard.Start(new WindowInteropHelper(this).Handle);
            clipboard.ClipboardChanged += FloatingContext_OnClipboardChanged;

            FocusWatchService.Instance.FocusChanged += FloatingContext_OnFocusChanged;
            FocusWatchService.Instance.Start();
        }

        /// <summary>停用上下文来源并可选隐藏面板，幂等</summary>
        private void StopFloatingContextSources(bool hidePanel)
        {
            if (!_fcSourcesStarted) return;
            _fcSourcesStarted = false;

            ClipboardService.Instance.ClipboardChanged -= FloatingContext_OnClipboardChanged;
            FocusWatchService.Instance.FocusChanged -= FloatingContext_OnFocusChanged;
            FocusWatchService.Instance.Stop();

            if (hidePanel)
            {
                floatingContext.HidePanel();
                _fcSource = FloatingContextSource.None;
                _fcCurrentSignature = "";
            }
            StopFcAutoHideTimer();
        }

        /// <summary>窗口关闭时注销事件订阅；无其他主窗口使用时停止焦点检测</summary>
        private void FloatingContext_OnWindowClosed()
        {
            StopFcAutoHideTimer();
            if (!_fcSourcesStarted) return;
            _fcSourcesStarted = false;

            ClipboardService.Instance.ClipboardChanged -= FloatingContext_OnClipboardChanged;
            FocusWatchService.Instance.FocusChanged -= FloatingContext_OnFocusChanged;

            bool hasOtherConsumer = false;
            foreach (Window w in Application.Current.Windows)
            {
                if (w is MainWindow other && other != this && other._fcSourcesStarted)
                {
                    hasOtherConsumer = true;
                    break;
                }
            }
            if (!hasOtherConsumer) FocusWatchService.Instance.Stop();
        }

        // ═══════════ 上下文事件 ═══════════

        /// <summary>输入框焦点变化：获焦显示内容；失焦延迟 10 秒隐藏（用户交互过则本轮不隐藏），剪贴板内容保留</summary>
        private void FloatingContext_OnFocusChanged(FocusContext? ctx)
        {
            if (ctx == null)
            {
                if (_fcSource != FloatingContextSource.InputBox) return;
                if (_fcUserInteracted) return; // 用户点击/拖拽过，说明在用，本轮不自动隐藏
                if (_fcSource == FloatingContextSource.Voice) return; // 语音热键会话期间不自动隐藏
                // 延迟 10 秒再隐藏：期间若重新获焦或用户交互则取消
                if (_fcAutoHideTimer == null)
                {
                    _fcAutoHideTimer = new DispatcherTimer { Interval = FcAutoHideDelay };
                    _fcAutoHideTimer.Tick += (_, _) =>
                    {
                        StopFcAutoHideTimer();
                        if (_fcSource == FloatingContextSource.InputBox)
                        {
                            floatingContext.HidePanel();
                            _fcSource = FloatingContextSource.None;
                            _fcCurrentSignature = "";
                        }
                    };
                }
                _fcAutoHideTimer.Stop();
                _fcAutoHideTimer.Start();
                return;
            }

            StopFcAutoHideTimer();

            // 标题固定为 AI推荐，不附带来源窗口名
            string title = Framework.Lang.T("settings.aiPicks");
            // 文字读取失败时退而显示窗口标题
            string content = string.IsNullOrWhiteSpace(ctx.Text) ? ctx.WindowTitle : ctx.Text;
            ShowFloatingContext(FloatingContextSource.InputBox, title, content,
                $"input:{ctx.WindowTitle}:{ctx.Text}");
        }

        /// <summary>剪贴板变化：按最新一条记录的类型显示对应内容</summary>
        private void FloatingContext_OnClipboardChanged()
        {
            var history = ClipboardService.Instance.ClipboardHistory;
            if (history.Count == 0) return;
            var entry = history[0];
            string signature = $"clip:{entry.Time.Ticks}:{entry.ContentType}";

            switch (entry.ContentType)
            {
                case ClipboardContentType.File:
                    string files = entry.FilePaths != null && entry.FilePaths.Count > 0
                        ? string.Join("\n", entry.FilePaths)
                        : entry.Text;
                    ShowFloatingContext(FloatingContextSource.Clipboard,
                        Framework.Lang.T("settings.aiPicks") + " · " + Framework.Lang.T("nativePanel.copiedFile"),
                        files, signature);
                    break;
                case ClipboardContentType.Image:
                    ShowFloatingContext(FloatingContextSource.Clipboard,
                        Framework.Lang.T("settings.aiPicks") + " · " + Framework.Lang.T("nativePanel.copiedImage"),
                        entry.Text, signature);
                    break;
                case ClipboardContentType.Text:
                case ClipboardContentType.Html:
                case ClipboardContentType.Video:
                    ShowFloatingContext(FloatingContextSource.Clipboard,
                        Framework.Lang.T("settings.aiPicks") + " · " + Framework.Lang.T("nativePanel.copiedText"),
                        entry.Text, signature);
                    break;
                default:
                    return;
            }
        }

        /// <summary>显示上下文内容：多主窗口时仅在离鼠标最近的主窗口显示（新内容替换旧内容；关闭只是隐藏，下次触发仍会重新出现）</summary>
        private void ShowFloatingContext(FloatingContextSource source, string title, string content, string signature)
        {
            // 语音热键录音期间（任一主窗口在录），不被输入框/剪贴板事件抢占，保证实时转写内容稳定显示
            if (source != FloatingContextSource.Voice && AnyWindowIsVoiceRecording()) return;

            // 多屏/左右侧边栏：仅在离鼠标最近的主窗口显示浮动框；本窗口非宿主时隐藏自身并转发给宿主窗口显示（语音等单窗口事件也能落到正确屏幕）
            MainWindow? host = PickFloatingContextHost();
            if (host != null && host != this)
            {
                HideFloatingContextIfVisible();
                host.ShowFloatingContextLocal(source, title, content, signature);
                return;
            }

            ShowFloatingContextLocal(source, title, content, signature);
        }

        /// <summary>本窗口实际展示浮动框内容（宿主判定通过后才执行）</summary>
        private void ShowFloatingContextLocal(FloatingContextSource source, string title, string content, string signature)
        {
            if (string.IsNullOrEmpty(content))
                content = Framework.Lang.T("nativePanel.empty");
            if (content.Length > FcMaxContentLength)
                content = content.Substring(0, FcMaxContentLength) + "...";

            _fcCurrentSignature = signature;
            _fcSource = source;
            _fcUserInteracted = false; // 新内容到达：重置交互标记，后续失焦按正常策略处理
            StopFcAutoHideTimer();

            bool wasHidden = floatingContext.Visibility != Visibility.Visible;
            floatingContext.Show(title, content);

            if (wasHidden)
            {
                // 布局完成后按持久化的相对位置恢复
                Dispatcher.BeginInvoke(new Action(RestoreFloatingContextPosition));
            }
            else
            {
                ClampFloatingContextPosition();
            }
        }

        /// <summary>关闭按钮：仅隐藏面板，下次触发（复制/输入框获焦）时仍会重新出现</summary>
        private void FloatingContext_CloseRequested()
        {
            StopFcAutoHideTimer();
            _fcCurrentSignature = "";
            _fcSource = FloatingContextSource.None;
            floatingContext.HidePanel();
        }

        /// <summary>主窗口按下 ESC：若浮动框正在显示则隐藏（同关闭按钮）；返回是否消费了该按键</summary>
        public bool TryHideFloatingContextByEscape()
        {
            if (floatingContext.Visibility != Visibility.Visible) return false;
            FloatingContext_CloseRequested();
            return true;
        }

        /// <summary>停止失焦延迟隐藏计时器</summary>
        private void StopFcAutoHideTimer() => _fcAutoHideTimer?.Stop();

        /// <summary>宿主切换时隐藏本窗口浮动框（等同关闭按钮语义）</summary>
        private void HideFloatingContextIfVisible()
        {
            if (floatingContext.Visibility != Visibility.Visible) return;
            FloatingContext_CloseRequested();
        }

        /// <summary>任一主窗口是否正在语音录音</summary>
        private static bool AnyWindowIsVoiceRecording()
        {
            foreach (Window w in Application.Current.Windows)
                if (w is MainWindow mw && mw.VoiceHotkeyPanel?.IsVoiceRecording == true) return true;
            return false;
        }

        /// <summary>选取离当前鼠标位置最近的主窗口作为浮动框宿主（同一屏左右侧栏取更近一侧；跨屏取鼠标所在屏的侧栏）</summary>
        private static MainWindow? PickFloatingContextHost()
        {
            if (!NativeMethods.GetCursorPos(out var cursor)) return null;

            MainWindow? best = null;
            long bestDist = long.MaxValue;
            foreach (Window w in Application.Current.Windows)
            {
                if (w is not MainWindow mw || mw.Visibility != Visibility.Visible) continue;
                IntPtr hwnd = new WindowInteropHelper(mw).Handle;
                if (hwnd == IntPtr.Zero) continue;
                if (!NativeMethods.GetWindowRect(hwnd, out var r)) continue;

                // 鼠标在窗口内时距离为 0，否则取到窗口矩形的平方距离（与 HoverTimer 一致的物理像素比较，无 DPI 换算问题）
                long dx = cursor.X < r.Left ? r.Left - cursor.X : (cursor.X > r.Right ? cursor.X - r.Right : 0);
                long dy = cursor.Y < r.Top ? r.Top - cursor.Y : (cursor.Y > r.Bottom ? cursor.Y - r.Bottom : 0);
                long dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = mw; }
            }
            return best;
        }

        /// <summary>用户交互（点击/拖拽/双击）：取消待定的自动隐藏，本轮失焦不再自动消失</summary>
        private void FloatingContext_UserInteracted()
        {
            _fcUserInteracted = true;
            StopFcAutoHideTimer(); // 交互时若倒计时进行中，一并取消
        }

        // ═══════════ 位置：恢复与钳制 ═══════════

        /// <summary>按持久化的相对偏移恢复面板垂直位置</summary>
        private void RestoreFloatingContextPosition()
        {
            if (floatingContext.Visibility != Visibility.Visible) return;
            // 先强制布局，确保 ActualHeight 已测量，位置换算才准确
            floatingContext.UpdateLayout();
            double offset = Math.Clamp(Framework.AppSettings.Instance.FloatingContextOffset, 0, 1);
            double maxTop = Math.Max(0, floatingContextLayer.ActualHeight - floatingContext.ActualHeight);
            Canvas.SetTop(floatingContext, offset * maxTop);
        }

        /// <summary>将面板位置钳制到内容区内（布局变化时调用）</summary>
        private void ClampFloatingContextPosition()
        {
            if (floatingContext.Visibility != Visibility.Visible) return;
            double top = Canvas.GetTop(floatingContext);
            if (double.IsNaN(top))
            {
                RestoreFloatingContextPosition();
                return;
            }
            double maxTop = Math.Max(0, floatingContextLayer.ActualHeight - floatingContext.ActualHeight);
            Canvas.SetTop(floatingContext, Math.Clamp(top, 0, maxTop));
        }

        // ═══════════ 交互：垂直拖拽 / 双击精简 ═══════════

        private void FloatingContext_MouseDown(object sender, MouseButtonEventArgs e)
        {
            FloatingContext_UserInteracted();
            if (e.ClickCount > 1) return; // 第二击交给双击处理
            if (e.OriginalSource is Button) return; // 点击关闭按钮等交互控件时不启动拖拽
            _fcDragging = true;
            _fcDragStartY = e.GetPosition(floatingContextLayer).Y;
            _fcDragStartTop = Canvas.GetTop(floatingContext);
            if (double.IsNaN(_fcDragStartTop)) _fcDragStartTop = 0;
            floatingContext.CaptureMouse();
        }

        private void FloatingContext_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_fcDragging || e.LeftButton != MouseButtonState.Pressed) return;
            double y = e.GetPosition(floatingContextLayer).Y;
            double maxTop = Math.Max(0, floatingContextLayer.ActualHeight - floatingContext.ActualHeight);
            Canvas.SetTop(floatingContext, Math.Clamp(_fcDragStartTop + (y - _fcDragStartY), 0, maxTop));
        }

        private void FloatingContext_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_fcDragging) return;
            _fcDragging = false;
            floatingContext.ReleaseMouseCapture();

            // 持久化相对位置（0~1）
            double maxTop = Math.Max(1, floatingContextLayer.ActualHeight - floatingContext.ActualHeight);
            double offset = Math.Clamp(Canvas.GetTop(floatingContext) / maxTop, 0, 1);
            Framework.AppSettings.Instance.FloatingContextOffset = offset;
            Framework.AppSettings.Instance.Save();
        }

        private void FloatingContext_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            FloatingContext_UserInteracted();
            if (e.OriginalSource is Button) return; // 双击关闭按钮不切换模式
            bool compact = !floatingContext.IsCompact;
            floatingContext.SetCompact(compact);
            Framework.AppSettings.Instance.FloatingContextCompact = compact;
            Framework.AppSettings.Instance.Save();

            // 高度变化后重新钳制位置
            Dispatcher.BeginInvoke(new Action(ClampFloatingContextPosition));
            e.Handled = true;
        }
    }
}
