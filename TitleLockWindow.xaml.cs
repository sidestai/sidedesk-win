using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SideDesk.Windows;

namespace SideDesk
{
    /// <summary>
    /// 窗口标题锁定器管理界面
    /// </summary>
    public partial class TitleLockWindow : Window
    {
        public TitleLockWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadRules();
        }

        /// <summary>
        /// 加载所有规则并显示在ListView中
        /// </summary>
        private void LoadRules()
        {
            var rules = TitleLockRuleManager.GetAll();
            rulesListView.ItemsSource = rules;
        }

        /// <summary>
        /// 关闭窗口
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// 添加新规则
        /// </summary>
        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddTitleLockRuleDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                LoadRules(); // 重新加载列表
            }
        }

        /// <summary>
        /// 编辑规则
        /// </summary>
        private void EditRule_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var rule = button?.DataContext as TitleLockRule;
            if (rule == null) return;

            var dialog = new EditTitleLockRuleDialog(rule) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                LoadRules(); // 重新加载列表
            }
        }

        /// <summary>
        /// 删除规则
        /// </summary>
        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var rule = button?.DataContext as TitleLockRule;
            if (rule == null) return;

            string displayName = !string.IsNullOrEmpty(rule.MatchPattern) 
                ? $"{rule.ProcessName} ({rule.MatchPattern})" 
                : $"{rule.ProcessName} - {rule.OriginalTitle}";

            if (MessageBox.Show($"确定删除规则 \"{displayName}\"?", "确认删除", 
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                TitleLockRuleManager.RemoveExact(rule);
                LoadRules(); // 重新加载列表
            }
        }
    }
}
