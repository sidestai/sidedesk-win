using System;
using System.Windows;
using SideDesk.Windows;

namespace SideDesk
{
    /// <summary>
    /// 添加标题锁定规则对话框
    /// </summary>
    public partial class AddTitleLockRuleDialog : Window
    {
        public AddTitleLockRuleDialog()
        {
            InitializeComponent();
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

            // 创建规则
            var rule = new TitleLockRule
            {
                ProcessName = processName,
                OriginalTitle = originalTitle,
                LockText = lockText,
                MatchPattern = matchPattern,
                MatchMode = matchMode,
                ExecutableDir = string.Empty
            };

            // 保存到管理器
            TitleLockRuleManager.Upsert(rule);

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
