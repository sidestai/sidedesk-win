using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace SideDesk.Services
{
    /// <summary>外部窗口输入框焦点上下文</summary>
    public class FocusContext
    {
        /// <summary>前台窗口标题</summary>
        public string WindowTitle { get; set; } = "";

        /// <summary>输入框中的文字（尽力读取，可能为空）</summary>
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// 外部输入框焦点监测服务：定时检测前台窗口是否有获得焦点的输入框。
    /// 原生控件（Edit/RichEdit/Scintilla 等）经 GetGUIThreadInfo 快速识别并跨进程读取文本；
    /// 浏览器 / Electron 等自绘界面回落 UIA（FocusedElement + ValuePattern/TextPattern）。
    /// 焦点或内容变化时触发 <see cref="FocusChanged"/>（焦点丢失时传 null）。
    /// 必须在 UI 线程上 Start（UIA 需要 STA 线程）。
    /// </summary>
    public sealed class FocusWatchService
    {
        /// <summary>读取输入框文字的最大长度</summary>
        private const int MaxTextLength = 4000;

        private static readonly Lazy<FocusWatchService> _instance = new(() => new FocusWatchService());
        public static FocusWatchService Instance => _instance.Value;

        private FocusWatchService() { }

        private DispatcherTimer? _timer;
        /// <summary>上次触发状态的签名（null = 无输入框上下文），用于去重避免轮询抖动</summary>
        private string? _lastSignature;
        private readonly uint _selfProcessId = (uint)Environment.ProcessId;

        /// <summary>监测是否运行中</summary>
        public bool IsRunning => _timer != null;

        /// <summary>输入框焦点变化事件（焦点丢失时为 null）</summary>
        public event Action<FocusContext?>? FocusChanged;

        /// <summary>启动监测（幂等，须在 UI 线程调用）</summary>
        public void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
        }

        /// <summary>停止监测（幂等）</summary>
        public void Stop()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer = null;
            _lastSignature = null;
        }

        /// <summary>单次轮询：检测前台输入框并按签名去重后触发事件</summary>
        private void Poll()
        {
            FocusContext? ctx = null;
            string? signature = null;

            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
                    // 前台是 SideDesk 自身窗口时不产生上下文，避免自触发循环
                    if (pid != _selfProcessId)
                        ctx = DetectInputBox(fg, out signature);
                }
            }
            catch { /* 检测失败静默降级 */ }

            if (signature == _lastSignature) return;
            _lastSignature = signature;

            try { FocusChanged?.Invoke(ctx); }
            catch { }
        }

        /// <summary>检测前台窗口的焦点输入框；命中时返回上下文并输出签名，否则返回 null</summary>
        private FocusContext? DetectInputBox(IntPtr fg, out string? signature)
        {
            signature = null;

            // 1. 原生控件快速路径
            try
            {
                var info = new NativeMethods.GUITHREADINFO
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
                };
                if (NativeMethods.GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
                {
                    var cls = new StringBuilder(64);
                    NativeMethods.GetClassName(info.hwndFocus, cls, cls.Capacity);
                    if (IsNativeEditClass(cls.ToString()))
                    {
                        string text = ReadControlText(info.hwndFocus);
                        signature = $"native:{info.hwndFocus}|{text}";
                        return new FocusContext { WindowTitle = GetWindowTitle(fg), Text = text };
                    }
                }
            }
            catch { }

            // 2. UIA 兜底（覆盖 Chrome / Edge / Electron 等自绘输入框）
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    var controlType = focused.Current.ControlType;
                    if (controlType == ControlType.Edit || controlType == ControlType.Document)
                    {
                        string text = ReadUiaText(focused);
                        string id;
                        try
                        {
                            id = focused.GetCurrentPropertyValue(AutomationElement.RuntimeIdProperty) is int[] runtimeId
                                ? string.Join(",", runtimeId)
                                : focused.GetHashCode().ToString();
                        }
                        catch { id = focused.GetHashCode().ToString(); }

                        signature = $"uia:{id}|{text}";
                        return new FocusContext { WindowTitle = GetWindowTitle(fg), Text = text };
                    }
                }
            }
            catch { /* UIA 元素随时可能失效（ElementNotAvailable），忽略 */ }

            return null;
        }

        /// <summary>原生编辑类控件类名判定</summary>
        private static bool IsNativeEditClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            return cls == "Edit" || cls == "TEdit" || cls == "Scintilla"
                || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("RICHEDIT", StringComparison.Ordinal)
                || cls.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>跨进程读取控件文本（WM_GETTEXT，带超时保护避免目标窗口无响应挂起）</summary>
        private static string ReadControlText(IntPtr hwnd)
        {
            try
            {
                NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG, 500, out IntPtr lenPtr);
                int len = Math.Min(lenPtr.ToInt32(), MaxTextLength);
                if (len <= 0) return "";

                var sb = new StringBuilder(len + 1);
                NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETTEXT, new IntPtr(len + 1), sb,
                    NativeMethods.SMTO_ABORTIFHUNG, 500, out _);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>经 UIA 读取元素文字：优先 ValuePattern，回落 TextPattern</summary>
        private static string ReadUiaText(AutomationElement element)
        {
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valueObj)
                    && valueObj is ValuePattern valuePattern)
                {
                    string v = valuePattern.Current.Value ?? "";
                    if (v.Length > 0) return Truncate(v);
                }
            }
            catch { }

            try
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textObj)
                    && textObj is TextPattern textPattern)
                {
                    string v = textPattern.DocumentRange.GetText(MaxTextLength) ?? "";
                    if (v.Length > 0) return v;
                }
            }
            catch { }

            return "";
        }

        /// <summary>获取窗口标题</summary>
        private static string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string Truncate(string text)
            => text.Length <= MaxTextLength ? text : text.Substring(0, MaxTextLength);
    }
}
