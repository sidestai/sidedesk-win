using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>小屏管理独立窗口：表格形式列出所有小屏，可控制是否在顶部标签显示、可删除。</summary>
    public partial class ScreenManagerWindow : Window
    {
        private readonly MainWindow _owner;

        public ScreenManagerWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Owner = owner;
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
            PopulateList();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>填充小屏管理表格（表头：序号 | 名称 | 类型 | 显示 | 操作），删除/切换后重新调用以刷新。</summary>
        private void PopulateList()
        {
            listPanel.Children.Clear();
            var configManager = _owner.ConfigManager;
            if (configManager == null) return;

            var windows = configManager.Windows;
            if (windows.Count == 0)
            {
                listPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "（暂无小屏）",
                    FontSize = 13,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 8, 0, 8)
                });
                return;
            }

            // ===== 表头 =====
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            string[] headers = { "序号", "名称", "类型", "显示", "操作" };
            for (int c = 0; c < headers.Length; c++)
            {
                var hText = new TextBlock
                {
                    Text = headers[c],
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = c == 3 ? HorizontalAlignment.Center : HorizontalAlignment.Left
                };
                Grid.SetColumn(hText, c);
                header.Children.Add(hText);
            }
            listPanel.Children.Add(header);

            // 分隔线
            listPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("BrushBorder"),
                Height = 1,
                Margin = new Thickness(0, 0, 0, 4)
            });

            // ===== 数据行 =====
            for (int i = 0; i < windows.Count; i++)
            {
                var config = windows[i];
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

                // 序号列
                var idxText = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 13,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(idxText, 0);
                row.Children.Add(idxText);

                // 名称列
                var label = new Emoji.Wpf.TextBlock
                {
                    Text = config.Name,
                    FontSize = 13,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                // 类型列（原生/网页）
                var typeText = new TextBlock
                {
                    Text = config.Type == WindowType.WebView ? "网页" : "原生",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(typeText, 2);
                row.Children.Add(typeText);

                // 显示列（CheckBox）
                var showCb = new CheckBox
                {
                    IsChecked = config.ShowInTabs,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                Grid.SetColumn(showCb, 3);
                showCb.Checked += (s, ev) => ToggleShowInTabs(config, true);
                showCb.Unchecked += (s, ev) => ToggleShowInTabs(config, false);
                row.Children.Add(showCb);

                // 操作列（删除按钮）
                var delBtn = new Button
                {
                    Content = "删除",
                    MinWidth = 52,
                    MinHeight = 24,
                    FontSize = 12,
                    Style = (Style)FindResource("StandardButtonStyle"),
                    Cursor = Cursors.Hand
                };
                Grid.SetColumn(delBtn, 4);
                delBtn.Click += (s, ev) => DeleteScreen(config);
                row.Children.Add(delBtn);

                listPanel.Children.Add(row);
            }
        }

        /// <summary>切换小屏在顶部标签栏的显示状态，同步两侧 dock 的 WindowIds 并刷新界面。</summary>
        private void ToggleShowInTabs(WindowConfig config, bool show)
        {
            var configManager = _owner.ConfigManager;
            var dockState = _owner.DockState;
            if (configManager == null || dockState == null) return;

            config.ShowInTabs = show;

            if (show)
            {
                // 按 Windows 列表中的顺序插回两侧 dock 的 WindowIds
                int orderIndex = configManager.Windows.IndexOf(config);
                InsertIntoDock(configManager, configManager.LeftDock, config.Id, orderIndex);
                InsertIntoDock(configManager, configManager.RightDock, config.Id, orderIndex);
            }
            else
            {
                configManager.LeftDock.WindowIds.Remove(config.Id);
                configManager.RightDock.WindowIds.Remove(config.Id);
            }

            // 修正当前 dock 活动索引
            if (dockState.ActiveIndex >= dockState.WindowIds.Count)
                dockState.ActiveIndex = Math.Max(0, dockState.WindowIds.Count - 1);

            configManager.Save();
            _owner.RefreshWindowTabs();

            // 隐藏的是当前小屏时自动切到第一个可见小屏；全部不可见则显示空白
            if (dockState.WindowIds.Count > 0)
                _owner.SwitchToWindowByIndex(dockState.ActiveIndex);
            else
                _owner.sidebarPanel.ClearContent();
        }

        /// <summary>按全局顺序将窗口 Id 插入 dock 的 WindowIds（保持与 Windows 列表一致的相对顺序）。</summary>
        private static void InsertIntoDock(WindowConfigManager configManager, DockWindowState dock, string windowId, int orderIndex)
        {
            if (dock.WindowIds.Contains(windowId)) return;

            // 找到应插入的位置：在 orderIndex 之前的窗口中，找最后一个已存在于 dock 中的
            int insertAt = 0;
            for (int i = 0; i < orderIndex && i < configManager.Windows.Count; i++)
            {
                var prevId = configManager.Windows[i].Id;
                int pos = dock.WindowIds.IndexOf(prevId);
                if (pos >= 0) insertAt = pos + 1;
            }
            dock.WindowIds.Insert(Math.Min(insertAt, dock.WindowIds.Count), windowId);
        }

        /// <summary>删除指定小屏及其下方所有模块，并刷新管理列表与主界面。</summary>
        private void DeleteScreen(WindowConfig config)
        {
            var configManager = _owner.ConfigManager;
            var dockState = _owner.DockState;
            if (configManager == null || dockState == null) return;

            int moduleCount = config.Panels?.Count ?? 0;
            string msg = moduleCount > 0
                ? $"确定删除小屏「{config.Name}」吗？\n其下方的 {moduleCount} 个模块也将一并删除，此操作不可撤销。"
                : $"确定删除小屏「{config.Name}」吗？此操作不可撤销。";
            if (!_owner.ShowConfirmDialog("删除小屏", msg, "删除")) return;

            // 在本地删除前先捕获 serverId，用于后续通知服务器删除
            string? serverId = config.ServerId;

            // 从所有窗口及两侧 dock 中移除（内部已处理 Windows + LeftDock + RightDock + Save）
            configManager.Remove(config.Id);

            // 修正当前 dock 的活动索引并刷新界面
            if (dockState.ActiveIndex >= dockState.WindowIds.Count)
                dockState.ActiveIndex = Math.Max(0, dockState.WindowIds.Count - 1);
            _owner.RefreshWindowTabs();
            if (dockState.WindowIds.Count > 0)
                _owner.SwitchToWindowByIndex(dockState.ActiveIndex);

            // 已同步过（有 serverId）则通知服务器软删除该 bar（连带其下所有 items）；不阻塞界面
            if (!string.IsNullOrEmpty(serverId))
                _ = CloudSyncService.Instance.DeleteBarOnServerAsync(serverId);

            PopulateList();
        }
    }
}
