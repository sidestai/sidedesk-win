using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Windows;

namespace SideDesk
{
    /// <summary>
    /// 任务栏标题锁定 —— 选择窗口界面（可复用）。
    /// 左侧展示已锁定规则（可删除/刷新），右侧列出当前任务栏窗口，
    /// 选中后「下一步」进入 <see cref="LockTitleDialog"/> 完成锁定。
    /// 使用方式：new TitleLockPickerWindow { Owner = ... }.ShowDialog();
    /// </summary>
    public partial class TitleLockPickerWindow : Window
    {
        private List<TaskbarOverlayManager.WindowInfo> _windows = new();

        public TitleLockPickerWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
            Loaded += (s, e) =>
            {
                LoadWindowList();
                ReloadLocked();
            };
        }

        /// <summary>加载当前可用的任务栏窗口列表。</summary>
        private void LoadWindowList()
        {
            _windows = TaskbarOverlayManager.GetTaskbarWindows() ?? new List<TaskbarOverlayManager.WindowInfo>();
            windowListBox.Items.Clear();

            if (_windows.Count == 0)
            {
                windowListBox.Items.Add(new ListBoxItem
                {
                    Content = new Emoji.Wpf.TextBlock
                    {
                        Text = "（未找到可用的任务栏窗口）",
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontStyle = FontStyles.Italic
                    },
                    IsHitTestVisible = false
                });
                return;
            }

            foreach (var w in _windows)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                if (w.Icon != null)
                {
                    itemPanel.Children.Add(new Image
                    {
                        Source = w.Icon,
                        Width = 20,
                        Height = 20,
                        Margin = new Thickness(0, 0, 8, 0)
                    });
                }
                var textPanel = new StackPanel();
                textPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = w.Title,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 420
                });
                textPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = w.ProcessName,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11
                });
                itemPanel.Children.Add(textPanel);
                windowListBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = w });
            }
        }

        /// <summary>重新加载已锁定规则列表。</summary>
        private void ReloadLocked()
        {
            lockedListBox.Items.Clear();
            var rules = TitleLockRuleManager.GetAll();
            foreach (var r in rules)
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) };
                sp.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"[{r.LockText}]  {r.ProcessName}",
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                string detail = !string.IsNullOrEmpty(r.MatchPattern)
                    ? $"匹配（{(r.MatchMode == TitleMatchMode.Equals ? "等于" : "包含")}）：{r.MatchPattern}"
                    : $"原标题：{r.OriginalTitle}";
                sp.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = detail,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                lockedListBox.Items.Add(new ListBoxItem { Content = sp, Tag = r });
            }

            if (lockedListBox.Items.Count == 0)
            {
                lockedListBox.Items.Add(new ListBoxItem
                {
                    Content = new Emoji.Wpf.TextBlock
                    {
                        Text = "（暂无锁定规则）",
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontStyle = FontStyles.Italic
                    },
                    IsHitTestVisible = false
                });
            }
        }

        private void DeleteRuleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (lockedListBox.SelectedItem is ListBoxItem li && li.Tag is TitleLockRule rule)
            {
                if (MessageBox.Show($"确定删除规则：[{rule.LockText}] {rule.ProcessName}？",
                    "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    TitleLockRuleManager.RemoveExact(rule);
                    ReloadLocked();
                }
            }
            else
            {
                MessageBox.Show("请选中要删除的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void EditRuleBtn_Click(object sender, RoutedEventArgs e)
        {
            EditSelectedRule();
        }

        private void LockedListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            EditSelectedRule();
        }

        /// <summary>对左侧列表选中的规则打开编辑对话框，返回后刷新列表。</summary>
        private void EditSelectedRule()
        {
            if (lockedListBox.SelectedItem is not ListBoxItem li || li.Tag is not TitleLockRule rule)
            {
                MessageBox.Show("请选中要编辑的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new LockTitleDialog(rule) { Owner = this };
            dialog.ShowDialog();
            ReloadLocked();
        }

        private void RefreshLockedBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadWindowList();
            ReloadLocked();
        }

        private void WindowListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OpenEditorForSelected();
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenEditorForSelected();
        }

        /// <summary>对选中的窗口打开锁定对话框，返回后刷新已锁定列表。</summary>
        private void OpenEditorForSelected()
        {
            if (windowListBox.SelectedItem is not ListBoxItem li || li.Tag is not TaskbarOverlayManager.WindowInfo window)
            {
                MessageBox.Show("请选择一个窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new LockTitleDialog(window) { Owner = this };
            dialog.ShowDialog();
            ReloadLocked();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
