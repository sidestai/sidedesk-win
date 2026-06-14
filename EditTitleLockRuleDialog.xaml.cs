using System;
using System.Windows;
using SideDesk.Windows;

namespace SideDesk
{
    /// <summary>
    /// 编辑标题锁定规则对话框
    /// </summary>
    public partial class EditTitleLockRuleDialog : Window
    {
        private readonly TitleLockRule _originalRule;

        public EditTitleLockRuleDialog(TitleLockRule rule)
        {
            InitializeComponent();
            
            // 保存原始规则的引用
            _originalRule = rule;
            
            // 填充现有数据
            LoadRuleData(rule);
        }

        /// <summary>
        /// 加载规则数据到界面控件
        /// </summary>
        private void LoadRuleData(TitleLockRule rule)
        {
            if (rule == null) return;

            processNameBox.Text = rule.ProcessName ?? string.Empty;
            originalTitleBox.Text = rule.OriginalTitle ?? string.Empty;
            lockTextBox.Text = rule.LockText ?? string.Empty;
            matchPatternBox.Text = rule.MatchPattern ?? string.Empty;

            // 设置匹配模式
            matchModeCombo.SelectedIndex = rule.MatchMode == TitleMatchMode.Equals ? 0 : 1;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 验证输入
            string processName = processNameBox.Text?.Trim();
            if (string.IsNullOrEmpty(processName))
            {
                MessageBox.Show("请输入进程名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                processNameBox.Focus();
                return;
            }

            string originalTitle = originalTitleBox.Text?.Trim() ?? string.Empty;
            string lockText = lockTextBox.Text?.Trim() ?? string.Empty;
            string matchPattern = matchPatternBox.Text?.Trim() ?? string.Empty;

            // 获取匹配模式
            TitleMatchMode matchMode = matchModeCombo.SelectedIndex == 0 
                ? TitleMatchMode.Equals 
                : TitleMatchMode.Contains;

            // 更新原始规则对象
            _originalRule.ProcessName = processName;
            _originalRule.OriginalTitle = originalTitle;
            _originalRule.LockText = lockText;
            _originalRule.MatchPattern = matchPattern;
            _originalRule.MatchMode = matchMode;

            // 保存到管理器（Upsert会根据key更新）
            TitleLockRuleManager.Upsert(_originalRule);

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
