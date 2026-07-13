using System;
using System.Windows;
using SideDesk.Models;

namespace SideDesk;

public partial class TodoDialog : Window
{
    /// <summary>提醒选项对应的提前分钟数（索引与 ComboBox 一致）</summary>
    private static readonly int[] RemindMinutes = { 0, 5, 10, 15, 30, 60 };

    /// <summary>对话框结果（确定时填充）</summary>
    public TodoItem Result { get; private set; } = new TodoItem();

    public TodoDialog(TodoItem? existing = null)
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

        // 提醒下拉项
        remindCombo.Items.Add("准时提醒");
        remindCombo.Items.Add("提前 5 分钟");
        remindCombo.Items.Add("提前 10 分钟");
        remindCombo.Items.Add("提前 15 分钟");
        remindCombo.Items.Add("提前 30 分钟");
        remindCombo.Items.Add("提前 60 分钟");
        remindCombo.SelectedIndex = 2; // 默认提前 10 分钟

        if (existing != null)
        {
            Title = "编辑待办";
            Result = existing;
            titleBox.Text = existing.Text;
            descBox.Text = existing.Description;
            if (existing.DueTime.HasValue)
            {
                enableTimeCheck.IsChecked = true;
                datePicker.SelectedDate = existing.DueTime.Value.Date;
                timeBox.Text = existing.DueTime.Value.ToString("HH:mm");
                int idx = Array.IndexOf(RemindMinutes, existing.RemindBeforeMinutes);
                remindCombo.SelectedIndex = idx >= 0 ? idx : 2;
            }
        }

        if (!enableTimeCheck.IsChecked.HasValue || enableTimeCheck.IsChecked == false)
        {
            // 默认日期时间：一小时后（勾选后可直接使用）
            var def = DateTime.Now.AddHours(1);
            if (datePicker.SelectedDate == null) datePicker.SelectedDate = def.Date;
            if (string.IsNullOrEmpty(timeBox.Text)) timeBox.Text = def.ToString("HH:mm");
        }

        titleBox.Focus();
        titleBox.SelectAll();
    }

    private void EnableTime_Changed(object sender, RoutedEventArgs e)
    {
        if (timePanel != null)
            timePanel.IsEnabled = enableTimeCheck.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var title = titleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("请输入待办标题", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result.Text = title;
        Result.Description = descBox.Text.Trim();

        if (enableTimeCheck.IsChecked == true && datePicker.SelectedDate.HasValue)
        {
            var due = datePicker.SelectedDate.Value.Date;
            if (TimeSpan.TryParse(timeBox.Text.Trim(), out var t))
                due = due.Add(t);
            Result.DueTime = due;
            Result.RemindBeforeMinutes = RemindMinutes[Math.Max(0, remindCombo.SelectedIndex)];
            Result.Reminded = false; // 重新设置时间后允许再次提醒
        }
        else
        {
            Result.DueTime = null;
            Result.RemindBeforeMinutes = -1;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
