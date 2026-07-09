using System;
using System.Text;
using System.Windows;
using SideDesk.Windows;

namespace SideDesk
{
    /// <summary>
    /// 锁定任务栏标题对话框（可复用）。
    /// 负责：录入锁定文字 / 匹配字符串 / 匹配模式，展示窗口详情，执行锁定或解除锁定。
    /// 使用方式：new LockTitleDialog(window) { Owner = ... }.ShowDialog()；
    /// 返回 true 表示已执行锁定或解除锁定（调用方可据此刷新列表）。
    /// </summary>
    public partial class LockTitleDialog : Window
    {
        private readonly TaskbarOverlayManager.WindowInfo _window;

        /// <summary>本次对话框最终应用的锁定文字（未锁定时为空）。</summary>
        public string ResultLockText { get; private set; } = string.Empty;

        /// <summary>true=执行了锁定；false=执行了解除锁定或取消。</summary>
        public bool IsLocked { get; private set; }

        public LockTitleDialog(TaskbarOverlayManager.WindowInfo window)
        {
            InitializeComponent();
            _window = window ?? throw new ArgumentNullException(nameof(window));
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
            Loaded += (s, e) => InitFromWindow();
        }

        /// <summary>
        /// 根据目标窗口初始化界面：标题信息、初始锁定文字、已存在的匹配规则、窗口详情。
        /// </summary>
        private void InitFromWindow()
        {
            Title = $"锁定标题: {_window.Title}";
            windowInfoText.Text = $"窗口：{_window.Title}";
            processInfoText.Text = $"进程：{_window.ProcessName}";

            // 初始锁定文字：已锁定文字 > 标题前 2 字符
            string initialText = string.Empty;
            try
            {
                var locked = TaskbarOverlayManager.GetLockedWindows();
                if (locked != null && locked.TryGetValue(_window.Handle, out string lockedText) && !string.IsNullOrEmpty(lockedText))
                {
                    initialText = lockedText;
                }
                else if (!string.IsNullOrEmpty(_window.Title))
                {
                    string t = _window.Title.Trim();
                    initialText = t.Length >= 2 ? t.Substring(0, 2) : t;
                }
            }
            catch { }
            lockTextBox.Text = initialText;

            // 预填已存在的匹配规则（匹配字符串 + 模式）
            string preMatchPattern = _window.Title ?? string.Empty;
            TitleMatchMode preMatchMode = TitleMatchMode.Contains;
            try
            {
                var details = TaskbarOverlayManager.GetWindowDetails(_window.Handle);
                if (details != null)
                {
                    var match = TitleLockRuleManager.FindMatch(details.ProcessName, details.Title, details.WorkingDirectory);
                    if (match != null && !string.IsNullOrEmpty(match.MatchPattern))
                    {
                        preMatchPattern = match.MatchPattern;
                        preMatchMode = match.MatchMode;
                    }
                    detailsBox.Text = BuildDetailsText(details);
                }
            }
            catch
            {
                detailsBox.Text = "(无法获取窗口详细信息)";
            }

            matchPatternBox.Text = preMatchPattern;
            if (preMatchMode == TitleMatchMode.Equals)
                equalsRadio.IsChecked = true;
            else
                containsRadio.IsChecked = true;

            lockTextBox.Focus();
            lockTextBox.SelectAll();
        }

        private static string BuildDetailsText(TaskbarOverlayManager.WindowDetails d)
        {
            if (d == null) return "(无信息)";
            var sb = new StringBuilder();
            sb.AppendLine($"窗口句柄 (HWND) : 0x{d.Handle.ToInt64():X}");
            sb.AppendLine($"窗口标题       : {d.Title}");
            sb.AppendLine($"窗口类名       : {d.ClassName}");
            sb.AppendLine($"窗口 Style     : 0x{d.StyleFlags:X8}");
            sb.AppendLine($"窗口 ExStyle   : 0x{d.ExStyleFlags:X8}");
            sb.AppendLine();
            sb.AppendLine($"进程 ID (PID)  : {d.ProcessId}");
            sb.AppendLine($"进程名         : {d.ProcessName}");
            sb.AppendLine($"可执行文件     : {d.ExecutablePath}");
            sb.AppendLine($"工作目录       : {d.WorkingDirectory}");
            sb.AppendLine($"运行用户       : {d.UserName}");
            sb.AppendLine($"启动时间       : {(d.StartTime.HasValue ? d.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")}");
            sb.AppendLine($"父进程 ID      : {d.ParentProcessId}");
            sb.AppendLine($"父进程名       : {d.ParentProcessName}");
            sb.AppendLine();
            sb.AppendLine("命令行参数：");
            sb.AppendLine(string.IsNullOrEmpty(d.CommandLine) ? "  (无法获取，可能需要管理员权限)" : "  " + d.CommandLine);
            return sb.ToString();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(detailsBox.Text ?? string.Empty);
                MessageBox.Show("信息已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TaskbarOverlayManager.UnlockWindowTitle(_window.Handle);
            }
            catch { }
            IsLocked = false;
            ResultLockText = string.Empty;
            DialogResult = true;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string lockText = lockTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(lockText))
            {
                MessageBox.Show("请输入锁定文字", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                lockTextBox.Focus();
                return;
            }

            string pattern = matchPatternBox.Text?.Trim() ?? string.Empty;
            TitleMatchMode mode = equalsRadio.IsChecked == true
                ? TitleMatchMode.Equals
                : TitleMatchMode.Contains;

            bool ok = TaskbarOverlayManager.LockWindowTitle(
                _window.Handle,
                lockText,
                string.IsNullOrEmpty(pattern) ? null : pattern,
                mode);

            if (!ok)
            {
                MessageBox.Show("锁定标题失败，可能权限不足。请以管理员身份运行。", "锁定失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsLocked = true;
            ResultLockText = lockText;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
