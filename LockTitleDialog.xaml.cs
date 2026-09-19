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
        private readonly TitleLockRule _editRule; // 非 null 表示编辑模式

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

        /// <summary>编辑模式：基于已有规则打开对话框进行修改。</summary>
        public LockTitleDialog(TitleLockRule rule)
        {
            InitializeComponent();
            _editRule = rule ?? throw new ArgumentNullException(nameof(rule));
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
            Loaded += (s, e) => InitFromRule();
        }

        /// <summary>
        /// 编辑模式初始化：从已有规则预填界面。
        /// </summary>
        private void InitFromRule()
        {
            Title = Framework.Lang.TF("nativePanel.editRuleNamed", _editRule.LockText, _editRule.ProcessName);
            windowInfoText.Text = Framework.Lang.TF("window.processLabel", _editRule.ProcessName);
            processInfoText.Text = !string.IsNullOrEmpty(_editRule.OriginalTitle)
                ? Framework.Lang.TF("window.originalTitle", _editRule.OriginalTitle)
                : Framework.Lang.TF("window.directoryLabel", _editRule.ExecutableDir);

            lockTextBox.Text = _editRule.LockText ?? string.Empty;
            matchPatternBox.Text = _editRule.MatchPattern ?? string.Empty;
            if (_editRule.MatchMode == TitleMatchMode.Equals)
                equalsRadio.IsChecked = true;
            else
                containsRadio.IsChecked = true;

            // 编辑模式下隐藏“解除锁定”按钮（无活动窗口可解锁）
            unlockButton.Visibility = Visibility.Collapsed;

            // 详细信息区显示规则摘要
            var sb = new StringBuilder();
            sb.AppendLine(Framework.Lang.TF("window.detailProcessName", _editRule.ProcessName));
            sb.AppendLine(Framework.Lang.TF("window.originalTitle", _editRule.OriginalTitle));
            sb.AppendLine(Framework.Lang.TF("window.detailExeDir", _editRule.ExecutableDir));
            sb.AppendLine(Framework.Lang.TF("window.detailLockText", _editRule.LockText));
            sb.AppendLine(Framework.Lang.TF("window.detailMatchPattern", _editRule.MatchPattern));
            sb.AppendLine(Framework.Lang.TF("window.detailMatchMode", _editRule.MatchMode == TitleMatchMode.Equals ? Framework.Lang.T("common.exactlyTheSame") : Framework.Lang.T("common.contains")));
            sb.AppendLine(Framework.Lang.TF("window.detailCreatedAt", _editRule.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine(Framework.Lang.TF("window.detailUpdatedAt", _editRule.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            detailsBox.Text = sb.ToString();

            lockTextBox.Focus();
            lockTextBox.SelectAll();
        }

        /// <summary>
        /// 根据目标窗口初始化界面：标题信息、初始锁定文字、已存在的匹配规则、窗口详情。
        /// </summary>
        private void InitFromWindow()
        {
            Title = Framework.Lang.TF("nativePanel.lockTitleStatus", _window.Title);
            windowInfoText.Text = Framework.Lang.TF("window.windowLabel", _window.Title);
            processInfoText.Text = Framework.Lang.TF("window.processLabel", _window.ProcessName);

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
                detailsBox.Text = Framework.Lang.T("window.detailCannotRetrieve");
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
            if (d == null) return Framework.Lang.T("window.noInfo");
            var sb = new StringBuilder();
            sb.AppendLine(Framework.Lang.TF("window.detailHandle", $"0x{d.Handle.ToInt64():X}"));
            sb.AppendLine(Framework.Lang.TF("window.detailWindowTitle", d.Title));
            sb.AppendLine(Framework.Lang.TF("window.detailClassName", d.ClassName));
            sb.AppendLine(Framework.Lang.TF("window.detailStyle", $"0x{d.StyleFlags:X8}"));
            sb.AppendLine(Framework.Lang.TF("window.detailExStyle", $"0x{d.ExStyleFlags:X8}"));
            sb.AppendLine();
            sb.AppendLine(Framework.Lang.TF("window.detailPid", d.ProcessId));
            sb.AppendLine(Framework.Lang.TF("window.detailProcessName", d.ProcessName));
            sb.AppendLine(Framework.Lang.TF("window.detailExecutable", d.ExecutablePath));
            sb.AppendLine(Framework.Lang.TF("window.detailWorkingDir", d.WorkingDirectory));
            sb.AppendLine(Framework.Lang.TF("window.detailUser", d.UserName));
            sb.AppendLine(Framework.Lang.TF("window.detailStartTime", d.StartTime.HasValue ? d.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : ""));
            sb.AppendLine(Framework.Lang.TF("window.detailParentPid", d.ParentProcessId));
            sb.AppendLine(Framework.Lang.TF("window.detailParentName", d.ParentProcessName));
            sb.AppendLine();
            sb.AppendLine(Framework.Lang.T("window.detailCommandLine"));
            sb.AppendLine(string.IsNullOrEmpty(d.CommandLine) ? Framework.Lang.T("window.detailCannotRetrieveAdmin") : "  " + d.CommandLine);
            return sb.ToString();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(detailsBox.Text ?? string.Empty);
                MessageBox.Show(Framework.Lang.T("common.infoCopied"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(Framework.Lang.T("misc.pleaseEnterTheLock"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Warning);
                lockTextBox.Focus();
                return;
            }

            string pattern = matchPatternBox.Text?.Trim() ?? string.Empty;
            TitleMatchMode mode = equalsRadio.IsChecked == true
                ? TitleMatchMode.Equals
                : TitleMatchMode.Contains;

            // ===== 编辑模式：更新已有规则 =====
            if (_editRule != null)
            {
                var newRule = new TitleLockRule
                {
                    ProcessName = _editRule.ProcessName,
                    OriginalTitle = _editRule.OriginalTitle,
                    ExecutableDir = _editRule.ExecutableDir,
                    LockText = lockText,
                    MatchPattern = string.IsNullOrEmpty(pattern) ? null : pattern,
                    MatchMode = mode,
                    CreatedAt = _editRule.CreatedAt
                };
                // 键可能变化（MatchPattern 被修改），先删旧规则再写入新规则
                TitleLockRuleManager.RemoveExact(_editRule);
                TitleLockRuleManager.Upsert(newRule);
                // 编辑模式只写存储不会碰活动窗口：立即跑一轮匹配应用，
                // 让修改当场生效（后台重扫定时器未启动时否则永远不生效）。
                try { TaskbarOverlayManager.ApplyRulesNow(); }
                catch { }
                IsLocked = true;
                ResultLockText = lockText;
                DialogResult = true;
                Close();
                return;
            }

            // ===== 新建模式：锁定活动窗口 =====
            bool ok = TaskbarOverlayManager.LockWindowTitle(
                _window.Handle,
                lockText,
                string.IsNullOrEmpty(pattern) ? null : pattern,
                mode);

            if (!ok)
            {
                MessageBox.Show(Framework.Lang.T("nativePanel.lockTitleFailedAdmin"), Framework.Lang.T("nativePanel.lockFailed"),
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
