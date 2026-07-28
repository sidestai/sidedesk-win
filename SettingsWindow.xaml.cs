using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SideDesk.Models;
using SideDesk.Services;
using SideDesk.Windows;
using System.Text;
using Velopack;

namespace SideDesk
{
    public partial class SettingsWindow : Window
    {
        private readonly App _app;
        private bool _isLoading = true;
        private ProgramRegistryManager _registry = null!;
        private Guid _selectedDesktopId = Guid.Empty;

        /// <summary>根据当前编辑上下文获取指定显示器的停靠配置</summary>
        private MonitorDockSettings ResolveMonitorConfig(int monitorIndex)
        {
            if (_app.AppSettings.EnablePerVirtualDesktop && _selectedDesktopId != Guid.Empty)
                return _app.AppSettings.GetMonitorForDesktop(_selectedDesktopId, monitorIndex);
            return _app.AppSettings.GetMonitor(monitorIndex);
        }

        public SettingsWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            LoadCurrentSettings();
            _isLoading = false;

            // 订阅云同步状态变化，实时更新账户页提示文本
            Services.CloudSyncService.Instance.StatusChanged += OnCloudSyncStatusChanged;
            Closed += (s, e) =>
            {
                try { Services.CloudSyncService.Instance.StatusChanged -= OnCloudSyncStatusChanged; } catch { }
            };

            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
        }

        private void LoadCurrentSettings()
        {
            // 主题模式
            themeModeCombo.Items.Add("深色");
            themeModeCombo.Items.Add("浅色");
            themeModeCombo.Items.Add("跟随系统");

            switch (_app.AppSettings.ThemeMode?.ToLower())
            {
                case "light": themeModeCombo.SelectedIndex = 1; break;
                case "system": themeModeCombo.SelectedIndex = 2; break;
                default: themeModeCombo.SelectedIndex = 0; break;
            }

            // 背景透明度
            bgOpacityBox.Text = _app.AppSettings.BackgroundOpacity.ToString("F0");

            // 隐藏模块外框时同时隐藏标题
            hideModuleTitleSwitch.IsChecked = Framework.AppSettings.Instance.HideModuleTitle;

            // 虚拟桌面开关
            perVirtualDesktopSwitch.IsChecked = _app.AppSettings.EnablePerVirtualDesktop;
            if (_app.AppSettings.EnablePerVirtualDesktop && _app.VirtualDesktop != null && _app.VirtualDesktop.IsAvailable)
            {
                _selectedDesktopId = _app.GetCurrentDesktopId();
                RefreshDesktopSelector();
            }

            // 停靠配置：为每个显示器动态生成一张卡片
            BuildDockConfigCards();

            // 程序注册表
            _registry = _app.ProgramRegistry;
            RefreshProgramList();
            RefreshBrowserList();

            // 显示默认 Profile 目录
            profileBaseDirText.Text = string.IsNullOrEmpty(_registry.DefaultProfileBaseDir) 
                ? "未设置" 
                : _registry.DefaultProfileBaseDir;

            RefreshExtensionList();

            titleLockSwitch.IsChecked = _app.AppSettings.EnableTitleLock;
            windowSwitchHotkeySwitch.IsChecked = _app.AppSettings.EnableWindowSwitchHotkey;
            mcpServerSwitch.IsChecked = _app.AppSettings.EnableMcpServer;

            // 账户 / 云同步
            RefreshAccountUI();

            // 团队切换下拉框
            RefreshCorpSelector();

            InitUpdateUI();
        }

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (pageGeneral == null || pageAccount == null || pageCloudSpaces == null || pageTeams == null || pageAppearance == null || pageProgramManager == null || pageExtensions == null || pagePlugins == null || pageTools == null || pageModules == null || pageWindows == null || pageShortcuts == null || pageAbout == null) return;
                    
            // 隐藏所有页面
            pageGeneral.Visibility = Visibility.Collapsed;
            pageAccount.Visibility = Visibility.Collapsed;
            pageCloudSpaces.Visibility = Visibility.Collapsed;
            pageTeams.Visibility = Visibility.Collapsed;
            pageAppearance.Visibility = Visibility.Collapsed;
            pageProgramManager.Visibility = Visibility.Collapsed;
            pageExtensions.Visibility = Visibility.Collapsed;
            pagePlugins.Visibility = Visibility.Collapsed;
            pageTools.Visibility = Visibility.Collapsed;
            pageModules.Visibility = Visibility.Collapsed;
            pageWindows.Visibility = Visibility.Collapsed;
            pageShortcuts.Visibility = Visibility.Collapsed;
            pageAbout.Visibility = Visibility.Collapsed;
                    
            // 显示选中页面
            switch (navList.SelectedIndex)
            {
                case 0: pageGeneral.Visibility = Visibility.Visible; break;
                case 1: pageWindows.Visibility = Visibility.Visible; _ = RefreshWindowListAsync(); break;
                case 2: pageAccount.Visibility = Visibility.Visible; RefreshAccountUI(); break;
                case 3: pageTeams.Visibility = Visibility.Visible; _ = RefreshTeamListAsync(); break;
                case 4: pageCloudSpaces.Visibility = Visibility.Visible; RefreshCloudSpaceList(); break;
                case 5: pageAppearance.Visibility = Visibility.Visible; break;
                case 6: pageProgramManager.Visibility = Visibility.Visible; break;
                case 7: pageExtensions.Visibility = Visibility.Visible; break;
                case 8: pagePlugins.Visibility = Visibility.Visible; RefreshPluginList(); break;
                case 9: pageTools.Visibility = Visibility.Visible; break;
                case 10: pageModules.Visibility = Visibility.Visible; RefreshModulesPage(); break;
                case 11: pageShortcuts.Visibility = Visibility.Visible; break;
                case 12: pageAbout.Visibility = Visibility.Visible; break;
            }
        }

        #region 账户 / 云同步

        /// <summary>根据当前登录/同步状态刷新账户页 UI</summary>
        private void RefreshAccountUI()
        {
            if (accountStatusText == null) return; // 尚未加载完成
            var s = _app.AppSettings;
            bool loggedIn = !string.IsNullOrEmpty(s.AccountToken);

            if (loggedIn)
            {
                accountStatusText.Text = "已登录" + (string.IsNullOrEmpty(s.AccountName) ? "" : "：" + s.AccountName);
                accountSubText.Text = s.AccountLoginTime.HasValue
                    ? $"登录时间：{s.AccountLoginTime:yyyy-MM-dd HH:mm}"
                    : "已登录";
                accountActionBtn.Content = "退出登录";
            }
            else
            {
                accountStatusText.Text = "未登录";
                // 若启动时检测到 Token 过期，显示提示；否则显示默认引导文本
                accountSubText.Text = !string.IsNullOrEmpty(App.AccountTokenExpiredMessage)
                    ? App.AccountTokenExpiredMessage
                    : "登录后可开启云同步备份";
                accountActionBtn.Content = "登录";
            }

            // 云同步开关：仅登录后可用
            _isLoading = true;
            cloudSyncSwitch.IsChecked = s.EnableCloudSync && loggedIn;
            _isLoading = false;
            cloudSyncSwitch.IsEnabled = loggedIn;
            syncNowBtn.IsEnabled = loggedIn;

            if (s.LastCloudSyncTime.HasValue)
                cloudSyncStatusText.Text = $"上次同步：{s.LastCloudSyncTime:yyyy-MM-dd HH:mm}";
            else
                cloudSyncStatusText.Text = loggedIn ? "尚未同步" : "登录后可同步";

            RefreshBackupUI();
        }

        /// <summary>检测到 Token 已失效时，清除本地登录状态并刷新相关 UI</summary>
        private void InvalidateLocalToken(string message)
        {
            var s = _app.AppSettings;
            s.AccountToken = null;
            s.AccountName = null;
            s.AccountLoginTime = null;
            s.EnableCloudSync = false;
            s.SelectedCorpId = null;
            s.SelectedCorpName = null;
            s.Save();

            App.AccountTokenExpiredMessage = message;
            try { Services.CloudSyncService.Instance.Stop(); } catch { }

            RefreshAccountUI();
            RefreshCorpSelector();
        }

        /// <summary>刷新本地备份状态。</summary>
        private void RefreshBackupUI()
        {
            if (backupStatusText == null) return;
            var t = Services.CloudSyncService.GetLocalBackupTime();
            if (t.HasValue)
            {
                backupStatusText.Text = $"最近备份：{t:yyyy-MM-dd HH:mm}";
                restoreBtn.IsEnabled = true;
            }
            else
            {
                backupStatusText.Text = "尚无本地备份";
                restoreBtn.IsEnabled = false;
            }
        }

        /// <summary>创建本地备份（覆盖最后一个）</summary>
        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (Services.CloudSyncService.CreateLocalBackup())
                MessageBox.Show("已创建本地备份。", "备份", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("备份失败，请重试。", "备份", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshBackupUI();
        }

        /// <summary>从最后一次本地备份恢复</summary>
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (!Services.CloudSyncService.HasLocalBackup())
            {
                MessageBox.Show("暂无可恢复的本地备份。", "恢复", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = MessageBox.Show("恢复将用最近一次备份覆盖当前全部配置，需要重启应用后生效。确定继续吗？",
                "恢复备份", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            int n = Services.CloudSyncService.RestoreLocalBackup();
            if (n >= 0)
                MessageBox.Show($"已恢复 {n} 个配置文件，重启应用后生效。", "恢复", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("恢复失败，请重试。", "恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>导出全部配置为 JSON 文件</summary>
        private void ExportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出配置",
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = $"SideDesk-config-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, Services.CloudSyncService.BuildConfigBundleJson(indented: true));
                MessageBox.Show("配置已导出。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>登录 / 退出登录</summary>
        private void AccountAction_Click(object sender, RoutedEventArgs e)
        {
            var s = _app.AppSettings;
            if (!string.IsNullOrEmpty(s.AccountToken))
            {
                // 退出登录
                var r = MessageBox.Show("确定要退出登录吗？退出后云同步将停止。", "退出登录",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK) return;

                s.AccountToken = null;
                s.AccountName = null;
                s.AccountLoginTime = null;
                s.EnableCloudSync = false;
                s.SelectedCorpId = null;
                s.SelectedCorpName = null;
                s.Save();
                try { Services.CloudSyncService.Instance.Stop(); } catch { }
                RefreshAccountUI();
                RefreshCorpSelector();
                return;
            }

            // 登录：打开登录窗口，成功后由 LoginWindow.LastToken 读取
            var login = new LoginWindow { Owner = this };
            login.ShowDialog();
            if (!string.IsNullOrEmpty(LoginWindow.LastToken))
            {
                s.AccountToken = LoginWindow.LastToken;
                s.AccountName = LoginWindow.LastUserName;
                s.AccountLoginTime = DateTime.Now;
                // 登录成功后，若团队选择为空则默认切到"个人"(corpId='')
                if (string.IsNullOrWhiteSpace(s.SelectedCorpId))
                {
                    s.SelectedCorpId = null;
                    s.SelectedCorpName = null;
                }
                s.Save();
                App.AccountTokenExpiredMessage = null; // 清除过期提示
                RefreshAccountUI();
                RefreshCorpSelector();
            }
        }

        /// <summary>云同步开关切换</summary>
        private void CloudSyncSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var s = _app.AppSettings;
            bool on = cloudSyncSwitch.IsChecked == true;

            if (on && string.IsNullOrEmpty(s.AccountToken))
            {
                MessageBox.Show("请先登录后再开启云同步。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                _isLoading = true;
                cloudSyncSwitch.IsChecked = false;
                _isLoading = false;
                return;
            }

            s.EnableCloudSync = on;
            s.Save();
            try
            {
                if (on) Services.CloudSyncService.Instance.Start();
                else Services.CloudSyncService.Instance.Stop();
            }
            catch { }
            RefreshAccountUI();
        }

        /// <summary>立即同步一次</summary>
        private async void SyncNow_Click(object sender, RoutedEventArgs e)
        {
            syncNowBtn.IsEnabled = false;
            await Services.CloudSyncService.Instance.SyncNowAsync(silent: false);
            syncNowBtn.IsEnabled = !string.IsNullOrEmpty(_app.AppSettings.AccountToken);
            RefreshAccountUI();
        }

        /// <summary>云同步状态变化回调：更新提示文本</summary>
        private void OnCloudSyncStatusChanged(Services.CloudSyncState state, string msg)
        {
            try
            {
                // 用 BeginInvoke 异步投递（非阻塞）：云同步后续在线程池线程回调，
                // 若用 Invoke 会阻塞线程池线程等待 UI 线程，调试断点暂停 UI 线程时会卡死
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cloudSyncStatusText != null) cloudSyncStatusText.Text = msg;
                }));
            }
            catch { }
        }

        #endregion

        #region 云空间管理

        private void RefreshCloudSpaceList()
        {
            cloudSpaceListPanel.Children.Clear();
            var spaces = CloudSpaceManager.GetAll();

            if (spaces.Count == 0)
            {
                cloudSpaceListPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "（暂无云空间，可点击“恢复内置”或手动添加）",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall"),
                    Margin = new Thickness(4)
                });
                return;
            }

            foreach (var space in spaces)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 图标
                var icon = new Emoji.Wpf.TextBlock
                {
                    Text = space.Icon, FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 10, 0)
                };
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);

                // 名称 + 网址
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                nameRow.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = space.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                });
                if (space.IsBuiltin)
                {
                    nameRow.Children.Add(new TextBlock
                    {
                        Text = "内置", FontSize = 10,
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0), Opacity = 0.7
                    });
                }
                info.Children.Add(nameRow);
                info.Children.Add(new TextBlock
                {
                    Text = space.Url,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                // 操作按钮
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var openBtn = new Button
                {
                    Content = "打开", Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
                };
                if (space.IsBuiltin)
                {
                    // 内置云空间（元桌云盘）：打开专用文件管理窗口，不可编辑/删除
                    openBtn.Click += (s, e) => { new CloudDriveWindow().Show(); };
                    btnPanel.Children.Add(openBtn);
                }
                else
                {
                    var spaceUrl = space.Url;
                    openBtn.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(spaceUrl) { UseShellExecute = true }); } catch { } };
                    btnPanel.Children.Add(openBtn);

                    var editBtn = new Button
                    {
                        Content = "编辑", Style = (Style)FindResource("GhostButtonStyle"),
                        Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
                    };
                    var editTarget = space;
                    editBtn.Click += (s, e) => ShowCloudSpaceEditDialog(editTarget);
                    btnPanel.Children.Add(editBtn);

                    var delBtn = new Button
                    {
                        Content = "✕", Style = (Style)FindResource("GhostButtonStyle"),
                        Padding = new Thickness(6, 2, 6, 2), FontSize = 11
                    };
                    var delId = space.Id;
                    var delName = space.Name;
                    delBtn.Click += (s, e) =>
                    {
                        if (MessageBox.Show($"确定删除云空间“{delName}”？", "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                        {
                            CloudSpaceManager.Delete(delId);
                            RefreshCloudSpaceList();
                        }
                    };
                    btnPanel.Children.Add(delBtn);
                }

                Grid.SetColumn(btnPanel, 2);
                row.Children.Add(btnPanel);

                cloudSpaceListPanel.Children.Add(row);
            }
        }

        private void AddCloudSpace_Click(object sender, RoutedEventArgs e)
        {
            ShowCloudSpaceEditDialog(null);
        }

        private void RestoreBuiltinSpaces_Click(object sender, RoutedEventArgs e)
        {
            CloudSpaceManager.RestoreBuiltins();
            RefreshCloudSpaceList();
        }

        /// <summary>云空间编辑对话框（space=null 表示新建）</summary>
        private void ShowCloudSpaceEditDialog(CloudSpace? space)
        {
            bool isNew = space == null;
            var editing = space ?? new CloudSpace();

            var dialog = new Window
            {
                Title = isNew ? "添加云空间" : "编辑云空间",
                Width = 380, Height = 310,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushBG")
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock { Text = "图标（emoji）", Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var iconBox = new TextBox { Text = editing.Icon, Width = 60, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(iconBox);

            root.Children.Add(new TextBlock { Text = "名称", Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var nameBox = new TextBox { Text = editing.Name, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(nameBox);

            root.Children.Add(new TextBlock { Text = "网址", Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var urlBox = new TextBox { Text = editing.Url, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(urlBox);

            root.Children.Add(new TextBlock { Text = "备注（可选）", Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var noteBox = new TextBox { Text = editing.Note ?? "", Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(noteBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var okBtn = new Button { Content = "确定", Width = 70, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                string name = nameBox.Text.Trim();
                string url = urlBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show("请输入名称"); return; }
                if (string.IsNullOrEmpty(url)) { MessageBox.Show("请输入网址"); return; }
                if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;

                editing.Name = name;
                editing.Url = url;
                editing.Icon = string.IsNullOrWhiteSpace(iconBox.Text) ? "☁️" : iconBox.Text.Trim();
                editing.Note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim();

                if (isNew) CloudSpaceManager.Add(editing);
                else CloudSpaceManager.Update(editing);

                RefreshCloudSpaceList();
                dialog.Close();
            };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        #endregion

        #region 团队管理

        /// <summary>头像下载专用 HttpClient（共享复用）</summary>
        private static readonly System.Net.Http.HttpClient _avatarHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        /// <summary>团队切换下拉框加载中标志（防止填充 Items 时触发 SelectionChanged）</summary>
        private bool _corpLoading = false;

        /// <summary>刷新左侧面板顶部的团队切换下拉框（未登录显示"未登录"，登录后显示"个人"+团队列表）</summary>
        private async void RefreshCorpSelector()
        {
            if (corpSelectorCombo == null) return;
            _corpLoading = true;
            corpSelectorCombo.Items.Clear();

            var s = _app.AppSettings;
            bool loggedIn = !string.IsNullOrEmpty(s.AccountToken);

            if (!loggedIn)
            {
                corpSelectorCombo.Items.Add(new ComboBoxItem { Content = "未登录", IsEnabled = false });
                corpSelectorCombo.SelectedIndex = 0;
                corpSelectorCombo.IsEnabled = false;
                _corpLoading = false;
                return;
            }

            corpSelectorCombo.IsEnabled = true;

            // 个人选项（有账户名/手机号时显示账户名，否则显示"个人"）
            string personalLabel = string.IsNullOrEmpty(s.AccountName) ? "个人" : s.AccountName;
            corpSelectorCombo.Items.Add(new ComboBoxItem { Content = personalLabel, Tag = null });

            // 异步加载团队列表
            var teams = await TeamService.Instance.ListTeamsAsync();
            if (corpSelectorCombo == null) return; // 窗口可能已关闭

            // 检测 Token 失效：清除登录状态并重置下拉框
            if (teams == null && TeamService.Instance.LastAuthFailed)
            {
                InvalidateLocalToken("登录已过期，请重新登录");
                return; // InvalidateLocalToken 已调用 RefreshCorpSelector 重新填充
            }

            if (teams != null)
            {
                foreach (var team in teams)
                    corpSelectorCombo.Items.Add(new ComboBoxItem { Content = team.Name, Tag = team.Id });
            }

            // 恢复上次选中的团队；若已不存在则回退到"个人"
            int selIdx = 0;
            if (!string.IsNullOrEmpty(s.SelectedCorpId))
            {
                for (int i = 1; i < corpSelectorCombo.Items.Count; i++)
                {
                    if ((corpSelectorCombo.Items[i] as ComboBoxItem)?.Tag as string == s.SelectedCorpId)
                    { selIdx = i; break; }
                }
                if (selIdx == 0)
                {
                    // 之前选中的团队已不在列表中，清除记录
                    s.SelectedCorpId = null;
                    s.SelectedCorpName = null;
                    s.Save();
                }
            }
            corpSelectorCombo.SelectedIndex = selIdx;
            _corpLoading = false;
        }

        /// <summary>团队切换下拉框选中变化：更新全局 corpId 并持久化</summary>
        private void CorpSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_corpLoading) return;
            if (corpSelectorCombo.SelectedItem is not ComboBoxItem item) return;

            var s = _app.AppSettings;
            string? corpId = item.Tag as string;
            s.SelectedCorpId = corpId;
            s.SelectedCorpName = corpId == null ? null : item.Content?.ToString();
            s.Save();
        }

        /// <summary>刷新团队列表（异步加载，失败优雅降级）</summary>
        private async Task RefreshTeamListAsync()
        {
            if (teamListPanel == null) return;
            teamListPanel.Children.Clear();

            // 未登录提示
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                teamListPanel.Children.Add(BuildTeamHintText("请先登录后再查看团队"));
                return;
            }

            teamListPanel.Children.Add(BuildTeamHintText("正在加载..."));
            var teams = await TeamService.Instance.ListTeamsAsync();

            teamListPanel.Children.Clear();
            if (teams == null)
            {
                // 检测是否因 Token 过期/无效导致失败
                if (TeamService.Instance.LastAuthFailed)
                {
                    InvalidateLocalToken("登录已过期，请重新登录");
                    teamListPanel.Children.Add(BuildTeamHintText("登录已过期，请重新登录后再查看团队"));
                }
                else
                {
                    teamListPanel.Children.Add(BuildTeamHintText("加载失败，请重试"));
                }
                return;
            }
            if (teams.Count == 0)
            {
                teamListPanel.Children.Add(BuildTeamHintText("（暂无团队，可点击下方添加）"));
                return;
            }

            foreach (var team in teams)
                teamListPanel.Children.Add(BuildTeamRow(team));
        }

        /// <summary>构建团队提示文本（加载/空/失败）</summary>
        private TextBlock BuildTeamHintText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(4)
            };
        }

        /// <summary>构建单个团队行：第一行团队名+成员数，第二行成员头像堆叠+添加按钮</summary>
        private UIElement BuildTeamRow(Team team)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(4, 8, 4, 8)
            };

            // 第一行：团队名 + 成员数
            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = team.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = (double)FindResource("FontSizeNormal"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var members = team.Members ?? new List<TeamMember>();
            headerRow.Children.Add(new TextBlock
            {
                Text = $"{members.Count} 人",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
            row.Children.Add(headerRow);

            // 第二行：成员头像堆叠（最多 5 个，超出显示 +N）+ 添加成员按钮
            const int maxAvatars = 5;
            var avatarStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            int shown = Math.Min(members.Count, maxAvatars);
            for (int i = 0; i < shown; i++)
            {
                var avatar = BuildAvatarCircle(members[i].Name, members[i].Avatar, 26);
                if (i > 0) avatar.Margin = new Thickness(-8, 0, 0, 0); // 负边距堆叠
                avatarStack.Children.Add(avatar);
            }
            if (members.Count > maxAvatars)
            {
                var more = BuildAvatarCircle("+" + (members.Count - maxAvatars), null, 26, isMore: true);
                more.Margin = new Thickness(-8, 0, 0, 0);
                avatarStack.Children.Add(more);
            }

            // 「+」添加成员按钮
            var addBtn = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(shown > 0 || members.Count > maxAvatars ? -8 : 0, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "添加成员",
                Tag = team,
                Background = Brushes.Transparent,
                BorderBrush = (Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(1),
                Content = new TextBlock
                {
                    Text = "+",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Template = null // 使用默认模板
            };
            addBtn.Style = null;
            // 圆形外观
            addBtn.Template = CreateCircleButtonTemplate();
            addBtn.Click += AddMember_Click;
            avatarStack.Children.Add(addBtn);

            row.Children.Add(avatarStack);

            return row;
        }

        /// <summary>创建圆形按钮模板</summary>
        private static System.Windows.Controls.ControlTemplate CreateCircleButtonTemplate()
        {
            var template = new System.Windows.Controls.ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            borderFactory.SetValue(Border.WidthProperty, 26.0);
            borderFactory.SetValue(Border.HeightProperty, 26.0);

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;
            return template;
        }

        /// <summary>添加成员按钮点击</summary>
        private async void AddMember_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Team team)
            {
                // TODO: 弹出添加成员对话框，调用后端接口
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// 构建圆形头像：有头像 URL 时后台下载并替换为图片，否则显示名称首字占位圆。
        /// isMore 表示“+N”省略圆（灰色背景）。
        /// </summary>
        private Border BuildAvatarCircle(string name, string? avatarUrl, double size, bool isMore = false)
        {
            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderBrush = (Brush)FindResource("BrushInputBG"), // 与面板背景同色，堆叠时形成间隔
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 占位内容：首字（或 +N）
            var placeholder = new TextBlock
            {
                Text = isMore ? name : GetInitial(name),
                Foreground = Brushes.White,
                FontSize = size * 0.45,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            border.Background = isMore
                ? (Brush)FindResource("BrushTextMuted")
                : new SolidColorBrush(HashToColor(name));
            border.Child = placeholder;

            // 有头像 URL 时后台下载，成功后切回 UI 线程替换为图片
            if (!isMore && !string.IsNullOrEmpty(avatarUrl))
            {
                string url = avatarUrl;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[] bytes = await _avatarHttp.GetByteArrayAsync(url);
                        if (bytes.Length == 0) return;
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new System.IO.MemoryStream(bytes))
                        {
                            bi.BeginInit();
                            bi.StreamSource = ms;
                            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bi.EndInit();
                        }
                        if (bi.CanFreeze) bi.Freeze();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            border.Background = new ImageBrush(bi);
                            border.Child = null;
                        });
                    }
                    catch { /* 下载失败保留占位圆 */ }
                });
            }

            return border;
        }

        /// <summary>取名称首字（用于头像占位，兼容 emoji 代理对）</summary>
        private static string GetInitial(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            string s = name.Trim();
            return char.IsSurrogatePair(s, 0) ? s.Substring(0, 2) : s.Substring(0, 1);
        }

        /// <summary>根据名称哈希生成稳定的头像背景色</summary>
        private static Color HashToColor(string name)
        {
            Color[] palette =
            {
                Color.FromRgb(0x5B, 0x8F, 0xF5), // 蓝
                Color.FromRgb(0x52, 0xC4, 0x7B), // 绿
                Color.FromRgb(0xF5, 0x8B, 0x5B), // 橙
                Color.FromRgb(0x9B, 0x6B, 0xF0), // 紫
                Color.FromRgb(0xF0, 0x6B, 0x9B), // 粉
                Color.FromRgb(0x3B, 0xC4, 0xC4), // 青
            };
            int hash = 0;
            foreach (char c in name) hash = hash * 31 + c;
            return palette[Math.Abs(hash) % palette.Length];
        }

        /// <summary>添加团队：弹出输入框，调用创建接口</summary>
        private async void AddTeam_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RenameDialog("新建团队", "");
            if (dlg.ShowDialog() != true) return;
            string name = dlg.NewName;
            if (string.IsNullOrWhiteSpace(name)) return;

            var team = await TeamService.Instance.CreateTeamAsync(name);
            if (team == null)
            {
                MessageBox.Show("创建团队失败，请重试", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await RefreshTeamListAsync();
            RefreshCorpSelector();
        }

        #endregion

        private void ThemeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            string mode = themeModeCombo.SelectedIndex switch
            {
                1 => "Light",
                2 => "System",
                _ => "Dark"
            };

            _app.AppSettings.ThemeMode = mode;
            _app.AppSettings.Save();
            App.ApplyTheme(mode);
        }

        private void HideModuleTitleSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            Framework.AppSettings.Instance.HideModuleTitle = hideModuleTitleSwitch.IsChecked ?? false;
            Framework.AppSettings.Instance.Save();
            // 即时重新渲染所有主窗口的原生面板
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                    mw.RefreshNativePanels();
            }
        }

        // ===== 虚拟桌面配置 =====

        private void PerVirtualDesktopSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool on = perVirtualDesktopSwitch.IsChecked == true;

            if (on && (_app.VirtualDesktop == null || !_app.VirtualDesktop.IsAvailable))
            {
                MessageBox.Show("当前系统不支持虚拟桌面，或仅有 1 个桌面。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _isLoading = true;
                perVirtualDesktopSwitch.IsChecked = false;
                _isLoading = false;
                return;
            }

            _app.AppSettings.EnablePerVirtualDesktop = on;
            _app.AppSettings.Save();

            if (on)
            {
                _selectedDesktopId = _app.GetCurrentDesktopId();
                RefreshDesktopSelector();
            }
            else
            {
                _selectedDesktopId = Guid.Empty;
                desktopSelectorPanel.Visibility = Visibility.Collapsed;
            }

            BuildDockConfigCards();
            _app.ApplyLayout(save: true);
        }

        private void RefreshDesktopSelector()
        {
            desktopSelectorCombo.Items.Clear();
            if (_app.VirtualDesktop == null || !_app.VirtualDesktop.IsAvailable) return;

            var desktopIds = _app.VirtualDesktop.GetAllDesktopIds();
            for (int i = 0; i < desktopIds.Count; i++)
            {
                var id = desktopIds[i];
                // 尝试从 profile 获取自定义名称，否则使用默认名称
                var profile = _app.AppSettings.GetDesktopProfile(id, createIfMissing: false);
                string name = !string.IsNullOrEmpty(profile.DesktopName) ? profile.DesktopName : $"桌面 {i + 1}";
                desktopSelectorCombo.Items.Add(name);
            }

            desktopSelectorPanel.Visibility = desktopIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            // 选中当前桌面
            if (_selectedDesktopId != Guid.Empty)
            {
                int idx = desktopIds.IndexOf(_selectedDesktopId);
                if (idx >= 0) desktopSelectorCombo.SelectedIndex = idx;
            }
        }

        private void DesktopSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (_app.VirtualDesktop == null) return;

            var desktopIds = _app.VirtualDesktop.GetAllDesktopIds();
            int idx = desktopSelectorCombo.SelectedIndex;
            if (idx >= 0 && idx < desktopIds.Count)
            {
                _selectedDesktopId = desktopIds[idx];
                BuildDockConfigCards();
            }
        }

        // ===== 停靠配置卡片动态构建（每显示器一张） =====
        private void BuildDockConfigCards()
        {
            dockConfigContainer.Children.Clear();
            var monitors = MonitorEnumerator.GetMonitorsPrimaryFirst();
            if (monitors.Count == 0)
                monitors.Add(new MonitorDisplayInfo { IsPrimary = true, PixelWidth = 0, PixelHeight = 0 });

            bool showHeader = monitors.Count > 1;
            for (int i = 0; i < monitors.Count; i++)
            {
                dockConfigContainer.Children.Add(BuildMonitorCard(i, monitors[i], showHeader));
            }
        }

        private FrameworkElement BuildMonitorCard(int monitorIndex, MonitorDisplayInfo mon, bool showHeader)
        {
            var cfg = ResolveMonitorConfig(monitorIndex);
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            if (showHeader)
            {
                string primary = mon.IsPrimary ? " · 主" : "";
                string res = mon.PixelWidth > 0 ? $" · {mon.PixelWidth}×{mon.PixelHeight}" : "";
                outer.Children.Add(new TextBlock
                {
                    Text = $"显示器 {monitorIndex + 1}{primary}{res}",
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 10)
                });
            }

            // 停靠位置下拉框
            var dockCombo = new ComboBox
            {
                Width = 120,
                Style = (Style)FindResource("GlobalComboBoxStyle")
            };
            dockCombo.Items.Add("无停靠");
            dockCombo.Items.Add("左侧");
            dockCombo.Items.Add("右侧");
            dockCombo.Items.Add("两侧");
            dockCombo.SelectedIndex = cfg.Layout switch
            {
                LayoutMode.Left => 1,
                LayoutMode.Right => 2,
                LayoutMode.Both => 3,
                _ => 0
            };
            outer.Children.Add(BuildInfoRow("停靠位置", "面板停靠在该显示器的哪一侧", dockCombo, new Thickness(0, 0, 0, 16)));

            // 左右侧子区块
            var leftBlock = BuildSideBlock("左侧面板", monitorIndex, DockEdge.Left);
            var rightBlock = BuildSideBlock("右侧面板", monitorIndex, DockEdge.Right);
            outer.Children.Add(leftBlock);
            outer.Children.Add(rightBlock);

            void UpdateVisibility()
            {
                var layout = ResolveMonitorConfig(monitorIndex).Layout;
                leftBlock.Visibility = (layout == LayoutMode.Left || layout == LayoutMode.Both)
                    ? Visibility.Visible : Visibility.Collapsed;
                rightBlock.Visibility = (layout == LayoutMode.Right || layout == LayoutMode.Both)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateVisibility();

            dockCombo.SelectionChanged += (s, e) =>
            {
                if (_isLoading) return;
                var newLayout = dockCombo.SelectedIndex switch
                {
                    1 => LayoutMode.Left,
                    2 => LayoutMode.Right,
                    3 => LayoutMode.Both,
                    _ => LayoutMode.None
                };
                var mcfg = ResolveMonitorConfig(monitorIndex);
                if (newLayout != mcfg.Layout)
                {
                    mcfg.Layout = newLayout;
                    _app.AppSettings.Save();
                    _app.ApplyLayout(save: true);
                    UpdateVisibility();
                }
            };

            return outer;
        }

        private Border BuildSideBlock(string title, int monitorIndex, DockEdge side)
        {
            var cfg = ResolveMonitorConfig(monitorIndex);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 侧边栏宽度
            var widthBox = new TextBox
            {
                Width = 70,
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                TextAlignment = TextAlignment.Center,
                Text = (side == DockEdge.Left ? cfg.LeftPanelWidth : cfg.RightPanelWidth).ToString()
            };
            void ApplyWidth()
            {
                if (_isLoading) return;
                var c = ResolveMonitorConfig(monitorIndex);
                int cur = side == DockEdge.Left ? c.LeftPanelWidth : c.RightPanelWidth;
                if (!int.TryParse(widthBox.Text, out int w) || w < 50 || w > 500)
                {
                    widthBox.Text = cur.ToString();
                    return;
                }
                if (w != cur)
                {
                    if (side == DockEdge.Left) c.LeftPanelWidth = w; else c.RightPanelWidth = w;
                    _app.AppSettings.Save();
                    _app.ApplyPanelWidths();
                }
            }
            widthBox.LostFocus += (s, e) => ApplyWidth();
            widthBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyWidth(); };
            panel.Children.Add(BuildInputRow("侧边栏宽度", "侧边栏停靠宽度（50-500）", widthBox, "px", new Thickness(0, 0, 0, 12)));

            // 开启浮动
            var hoverCheck = new CheckBox
            {
                Style = (Style)FindResource("RaycastSwitchStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = side == DockEdge.Left ? cfg.LeftHoverEnabled : cfg.RightHoverEnabled
            };

            // 浮动宽度
            var hoverWidthBox = new TextBox
            {
                Width = 70,
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                TextAlignment = TextAlignment.Center,
                Text = (side == DockEdge.Left ? cfg.LeftHoverWidth : cfg.RightHoverWidth).ToString()
            };
            void ApplyHoverWidth()
            {
                if (_isLoading) return;
                var c = ResolveMonitorConfig(monitorIndex);
                int cur = side == DockEdge.Left ? c.LeftHoverWidth : c.RightHoverWidth;
                if (!int.TryParse(hoverWidthBox.Text, out int w) || w < 100 || w > 500)
                {
                    hoverWidthBox.Text = cur.ToString();
                    return;
                }
                if (w != cur)
                {
                    if (side == DockEdge.Left) c.LeftHoverWidth = w; else c.RightHoverWidth = w;
                    _app.AppSettings.Save();
                    _app.ReloadHoverSettings();
                }
            }
            hoverWidthBox.LostFocus += (s, e) => ApplyHoverWidth();
            hoverWidthBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyHoverWidth(); };
            var hoverWidthRow = BuildInputRow("浮动宽度", "悬停展开宽度（100-500）", hoverWidthBox, "px", new Thickness(0));
            hoverWidthRow.Visibility = hoverCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            void HoverChanged()
            {
                if (_isLoading) return;
                var c = ResolveMonitorConfig(monitorIndex);
                bool en = hoverCheck.IsChecked == true;
                if (side == DockEdge.Left) c.LeftHoverEnabled = en; else c.RightHoverEnabled = en;
                _app.AppSettings.Save();
                hoverWidthRow.Visibility = en ? Visibility.Visible : Visibility.Collapsed;
                _app.ReloadHoverSettings();
            }
            hoverCheck.Checked += (s, e) => HoverChanged();
            hoverCheck.Unchecked += (s, e) => HoverChanged();
            panel.Children.Add(BuildInfoRow("开启浮动", "开启浮动（鼠标悬停展开）", hoverCheck, new Thickness(0, 0, 0, 12)));
            panel.Children.Add(hoverWidthRow);

            return new Border
            {
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16),
                Child = panel
            };
        }

        // 构建“标题 + 右侧控件”行
        private Grid BuildInfoRow(string title, string subtitle, FrameworkElement control, Thickness margin)
        {
            var grid = new Grid { Margin = margin };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = (double)FindResource("FontSizeNormal")
            });
            labels.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetColumn(labels, 0);
            grid.Children.Add(labels);

            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        // 构建“标题 + 输入框 + 单位”行
        private Grid BuildInputRow(string title, string subtitle, FrameworkElement input, string unit, Thickness margin)
        {
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(input);
            wrap.Children.Add(new TextBlock
            {
                Text = unit,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
            return BuildInfoRow(title, subtitle, wrap, margin);
        }

        private void BgOpacity_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyBgOpacity();
        }

        private void BgOpacity_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyBgOpacity();
            }
        }

        private void ApplyBgOpacity()
        {
            if (_isLoading) return;
            if (!double.TryParse(bgOpacityBox.Text, out double opacity) || opacity < 0 || opacity > 100)
            {
                bgOpacityBox.Text = _app.AppSettings.BackgroundOpacity.ToString("F0");
                return;
            }

            if (opacity != _app.AppSettings.BackgroundOpacity)
            {
                _app.AppSettings.BackgroundOpacity = opacity;
                _app.AppSettings.Save();
                ApplyPanelBackgroundOpacity();
            }
        }

        private void ApplyPanelBackgroundOpacity()
        {
            double opacity = _app.AppSettings.BackgroundOpacity / 100.0;
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                {
                    mw.Background = new SolidColorBrush(Color.FromArgb(
                        (byte)(opacity * 255), 0, 0, 0));
                }
            }
        }

        #region 程序管理

        private void RefreshProgramList()
        {
            programListPanel.Children.Clear();
            foreach (var prog in _registry.Programs)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                var nameBlock = new Emoji.Wpf.TextBlock
                {
                    Text = prog.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                };
                var pathBlock = new Emoji.Wpf.TextBlock
                {
                    Text = prog.ExePath,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                info.Children.Add(nameBlock);
                info.Children.Add(pathBlock);
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var progId = prog.Id;
                delBtn.Click += (s, e) => { RemoveProgram(progId); };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                programListPanel.Children.Add(row);
            }

            if (_registry.Programs.Count == 0)
            {
                programListPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无已注册程序",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
            }
        }

        private void AddProgram_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择程序"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                _registry.Programs.Add(new ProgramEntry { Name = name, ExePath = dlg.FileName });
                _registry.Save();
                RefreshProgramList();
            }
        }

        private void RemoveProgram(string id)
        {
            _registry.Programs.RemoveAll(p => p.Id == id);
            _registry.Save();
            RefreshProgramList();
        }

        #endregion

        #region 浏览器管理

        private void RefreshBrowserList()
        {
            browserListPanel.Children.Clear();
            foreach (var browser in _registry.Browsers)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var cardContent = new StackPanel();

                // 浏览器头部（名称 + 路径 + 删除按钮）
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var browserInfo = new StackPanel();
                browserInfo.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = browser.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal")
                });
                browserInfo.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = browser.ExePath,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(browserInfo, 0);
                header.Children.Add(browserInfo);

                var delBrowserBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top
                };
                var browserId = browser.Id;
                delBrowserBtn.Click += (s, e) => { RemoveBrowser(browserId); };
                Grid.SetColumn(delBrowserBtn, 1);
                header.Children.Add(delBrowserBtn);

                cardContent.Children.Add(header);

                // UserData 列表
                if (browser.Profiles.Count > 0)
                {
                    var profilesTitle = new Emoji.Wpf.TextBlock
                    {
                        Text = "UserData 配置",
                        Foreground = (Brush)FindResource("BrushTextSec"),
                        FontSize = (double)FindResource("FontSizeSmall"),
                        Margin = new Thickness(0, 8, 0, 4)
                    };
                    cardContent.Children.Add(profilesTitle);

                    foreach (var profile in browser.Profiles)
                    {
                        var profileRow = new Grid { Margin = new Thickness(12, 0, 0, 4) };
                        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var profileInfo = new StackPanel();
                        profileInfo.Children.Add(new Emoji.Wpf.TextBlock
                        {
                            Text = profile.Name,
                            Foreground = (Brush)FindResource("BrushTextMain"),
                            FontSize = (double)FindResource("FontSizeSmall")
                        });
                        profileInfo.Children.Add(new Emoji.Wpf.TextBlock
                        {
                            Text = profile.Directory,
                            Foreground = (Brush)FindResource("BrushTextMuted"),
                            FontSize = (double)FindResource("FontSizeTiny"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        Grid.SetColumn(profileInfo, 0);
                        profileRow.Children.Add(profileInfo);

                        var delProfileBtn = new Button
                        {
                            Content = "✕",
                            Style = (Style)FindResource("GhostButtonStyle"),
                            Padding = new Thickness(4, 1, 4, 1),
                            FontSize = 10
                        };
                        var pBrowserId = browser.Id;
                        var pProfileId = profile.Id;
                        delProfileBtn.Click += (s, e) => { RemoveProfile(pBrowserId, pProfileId); };
                        Grid.SetColumn(delProfileBtn, 1);
                        profileRow.Children.Add(delProfileBtn);

                        cardContent.Children.Add(profileRow);
                    }
                }

                // 添加 UserData 按钮
                var addProfileBtn = new Button
                {
                    Content = "+ UserData",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var addBrowserId = browser.Id;
                addProfileBtn.Click += (s, e) => { AddProfile(addBrowserId); };
                cardContent.Children.Add(addProfileBtn);

                card.Child = cardContent;
                browserListPanel.Children.Add(card);
            }
        }

        private void AddBrowser_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "浏览器 (*.exe)|*.exe",
                Title = "选择浏览器主程序"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                _registry.Browsers.Add(new BrowserConfig { Name = name, ExePath = dlg.FileName });
                _registry.Save();
                RefreshBrowserList();
            }
        }

        private void RemoveBrowser(string id)
        {
            _registry.Browsers.RemoveAll(b => b.Id == id);
            _registry.Save();
            RefreshBrowserList();
        }

        private void AddProfile(string browserId)
        {
            var browser = _registry.GetBrowserById(browserId);
            if (browser == null) return;

            var dlg = new OpenFolderDialog
            {
                Title = "选择 UserData 目录"
            };
            if (dlg.ShowDialog() == true)
            {
                var name = System.IO.Path.GetFileName(dlg.FolderName);
                if (string.IsNullOrEmpty(name)) name = "Profile";
                browser.Profiles.Add(new UserDataProfile { Name = name, Directory = dlg.FolderName });
                _registry.Save();
                RefreshBrowserList();
            }
        }

        private void RemoveProfile(string browserId, string profileId)
        {
            var browser = _registry.GetBrowserById(browserId);
            browser?.Profiles.RemoveAll(p => p.Id == profileId);
            _registry.Save();
            RefreshBrowserList();
        }

        #endregion

        private void SelectProfileBaseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择默认 Profile 目录"
            };
            if (dlg.ShowDialog() == true)
            {
                _registry.DefaultProfileBaseDir = dlg.FolderName;
                _registry.Save();
                profileBaseDirText.Text = dlg.FolderName;
            }
        }

        #region 扩展管理

        private void RefreshExtensionList()
        {
            extensionListPanel.Children.Clear();
            var app = (App)Application.Current;

            if (app.Extensions.Extensions.Count == 0)
            {
                extensionListPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无扩展",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
                return;
            }

            foreach (var ext in app.Extensions.Extensions)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BrushInputBG"),
                    BorderBrush = (Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = ext.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                });

                string typeLabel = ext.Type switch
                {
                    "open" => "打开网址/程序",
                    "paste" => "粘贴文本",
                    "powershell" => "PowerShell 脚本",
                    "csharp" => "C# 脚本",
                    _ => ext.Type
                };
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = typeLabel + (string.IsNullOrEmpty(ext.Description) ? "" : " — " + ext.Description),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var extId = ext.Id;
                delBtn.Click += (s, e) =>
                {
                    var app2 = (App)Application.Current;
                    app2.Extensions.Remove(extId);
                    RefreshExtensionList();
                };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                card.Child = row;
                extensionListPanel.Children.Add(card);
            }
        }

        private void NewExtension_Click(object sender, RoutedEventArgs e)
        {
            var win = new ExtensionWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                RefreshExtensionList();
            }
        }

        #endregion

        #region 插件管理

        /// <summary>刷新插件列表（扫描插件目录，展示所有插件及启用状态）</summary>
        private void RefreshPluginList()
        {
            pluginListPanel.Children.Clear();

            var configs = Services.Plugins.PluginLoaderService.Instance.ScanAllPluginConfigs();
            var disabled = _app.AppSettings.DisabledPlugins;

            if (configs.Count == 0)
            {
                pluginListPanel.Children.Add(new TextBlock
                {
                    Text = "插件目录下没有插件",
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            foreach (var cfg in configs)
            {
                var isEnabled = !disabled.Contains(cfg.Id);

                var card = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("BrushInputBG"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var dock = new DockPanel();

                // 右侧开关
                var toggle = new CheckBox
                {
                    IsChecked = isEnabled,
                    Style = (Style)FindResource("RaycastSwitchStyle"),
                    Tag = cfg.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                toggle.Checked += PluginToggle_Changed;
                toggle.Unchecked += PluginToggle_Changed;
                DockPanel.SetDock(toggle, Dock.Right);
                dock.Children.Add(toggle);

                // 左侧信息
                var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"{cfg.Icon} {cfg.Name}",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMain")
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"v{cfg.Version}  |  {cfg.Id}  |  {(string.IsNullOrEmpty(cfg.Author) ? "未知作者" : cfg.Author)}",
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
                if (!string.IsNullOrEmpty(cfg.Description))
                {
                    info.Children.Add(new TextBlock
                    {
                        Text = cfg.Description,
                        FontSize = 11,
                        Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                dock.Children.Add(info);

                card.Child = dock;
                pluginListPanel.Children.Add(card);
            }
        }

        /// <summary>插件启用/禁用开关变化</summary>
        private void PluginToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox toggle)
                return;
            var pluginId = toggle.Tag as string;
            if (string.IsNullOrEmpty(pluginId)) return;

            var disabled = _app.AppSettings.DisabledPlugins;
            if (toggle.IsChecked == true)
                disabled.Remove(pluginId);
            else
                disabled.Add(pluginId);

            _app.AppSettings.Save();

            // 重新加载插件（卸载旧的 + 加载新的）
            Services.Plugins.PluginLoaderService.Instance.UnloadAll();
            Services.Plugins.PluginLoaderService.Instance.LoadAllPlugins(disabled);
        }

        #endregion

        #region 工具 - 任务栏标题锁定

        private void TitleLockSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.EnableTitleLock = titleLockSwitch.IsChecked == true;
            _app.AppSettings.Save();

            // 接通后台重扫定时器的启停：开启则启动重扫（自动捕捉重开的目标窗口），关闭则停止。
            try
            {
                if (_app.AppSettings.EnableTitleLock)
                    TaskbarOverlayManager.EnsureRescanStarted();
                else
                    TaskbarOverlayManager.StopRescan();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TitleLock] 切换启停失败: {ex.Message}");
            }
        }

        private void TaskbarOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new TitleLockPickerWindow { Owner = this };
            picker.ShowDialog();
        }

        private void WindowSwitchHotkeySwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.EnableWindowSwitchHotkey = windowSwitchHotkeySwitch.IsChecked == true;
            _app.AppSettings.Save();

            // 实时启停全局键盘钩子
            try
            {
                if (_app.AppSettings.EnableWindowSwitchHotkey)
                    Services.GlobalHotkeyService.Instance.Start();
                else
                    Services.GlobalHotkeyService.Instance.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] 切换启停失败: {ex.Message}");
            }
        }

        private void McpServerSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.EnableMcpServer = mcpServerSwitch.IsChecked == true;
            _app.AppSettings.Save();

            try
            {
                if (_app.AppSettings.EnableMcpServer)
                    Services.McpServerService.Instance.Start();
                else
                    Services.McpServerService.Instance.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] MCP Server 启停异常: {ex.Message}");
            }
        }

        #endregion

        #region 模块 - 数据模版管理

        /// <summary>刷新模块页面（连接方式开关 + 模版列表）</summary>
        private void RefreshModulesPage()
        {
            // 初始化连接方式开关状态
            dataConnectionModeSwitch.IsChecked = _app.AppSettings.DataConnectionMode == "extension";
            RefreshTemplateList();
        }

        /// <summary>数据模块连接方式切换</summary>
        private void DataConnectionMode_Changed(object sender, RoutedEventArgs e)
        {
            if (dataConnectionModeSwitch == null) return;
            var mode = dataConnectionModeSwitch.IsChecked == true ? "extension" : "cdp";
            _app.AppSettings.DataConnectionMode = mode;
            _app.AppSettings.Save();

            if (mode == "extension")
            {
                // 启动 WS 服务
                try
                {
                    if (!Services.WebSocketServerService.Instance.IsRunning)
                        Services.WebSocketServerService.Instance.Start();
                }
                catch { /* ignore */ }
            }
            else
            {
                // 停止 WS 服务
                try
                {
                    Services.WebSocketServerService.Instance.Stop();
                }
                catch { /* ignore */ }
            }
        }

        private void RefreshTemplateList()
        {
            templateListPanel.Children.Clear();
            var manager = DataTemplateManager.Load();

            if (manager.Templates.Count == 0)
            {
                templateListPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = "暂无数据模版",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
                return;
            }

            foreach (var tpl in manager.Templates)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                var nameBlock = new Emoji.Wpf.TextBlock
                {
                    Text = tpl.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                };
                info.Children.Add(nameBlock);

                var descText = string.IsNullOrEmpty(tpl.Category) ? tpl.Description : $"[{tpl.Category}] {tpl.Description}";
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = descText,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"目标: {tpl.TargetUrl}  |  选择器: {tpl.Selector}  |  刷新: {tpl.RefreshIntervalSeconds}s",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var tplId = tpl.Id;
                delBtn.Click += (s, e) => { RemoveTemplate(tplId); };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                templateListPanel.Children.Add(row);
            }
        }

        private void ViewWsClients_Click(object sender, RoutedEventArgs e)
        {
            var win = new WsClientsWindow();
            win.Owner = this;
            win.Show();
        }

        private void AddTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "新建数据模版",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog);

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "新建数据模版",
                FontSize = (double)FindResource("FontSizeSubtitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 名称
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "模版名称", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            // 描述
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "描述", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var descBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(descBox);

            // 分类
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "分类", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var categoryBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(categoryBox);

            // 目标网址
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "目标网址", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var urlBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(urlBox);

            // 浏览器选择
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "浏览器", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var browserCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            browserCombo.Items.Add("默认浏览器");
            foreach (var b in _registry.Browsers)
                browserCombo.Items.Add(b.Name);
            browserCombo.SelectedIndex = 0;
            panel.Children.Add(browserCombo);

            // 容器(Profile)选择
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "容器 (Profile)", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var profileCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            profileCombo.Items.Add("默认");
            profileCombo.SelectedIndex = 0;
            panel.Children.Add(profileCombo);

            // 浏览器切换时更新Profile列表
            browserCombo.SelectionChanged += (s, ev) =>
            {
                profileCombo.Items.Clear();
                profileCombo.Items.Add("默认");
                if (browserCombo.SelectedIndex > 0)
                {
                    var selectedBrowser = _registry.Browsers[browserCombo.SelectedIndex - 1];
                    foreach (var p in selectedBrowser.Profiles)
                        profileCombo.Items.Add(p.Name);
                }
                profileCombo.SelectedIndex = 0;
            };

            // 选择器
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "选择器 (CSS Selector 或 XPath)", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var selectorGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selectorBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            Grid.SetColumn(selectorBox, 0);
            selectorGrid.Children.Add(selectorBox);
            var pickSelectorBtn = new Button
            {
                Content = "选择",
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pickSelectorBtn, 1);
            selectorGrid.Children.Add(pickSelectorBtn);
            panel.Children.Add(selectorGrid);

            // 选择按钮点击逻辑
            pickSelectorBtn.Click += async (s, ev) =>
            {
                // 校验URL
                if (string.IsNullOrWhiteSpace(urlBox.Text))
                {
                    MessageBox.Show("请先输入目标网址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 确定浏览器路径
                string browserPath = "";
                string profileDir = "";
                if (browserCombo.SelectedIndex > 0)
                {
                    var selectedBrowser = _registry.Browsers[browserCombo.SelectedIndex - 1];
                    browserPath = selectedBrowser.ExePath;
                    if (profileCombo.SelectedIndex > 0)
                        profileDir = selectedBrowser.Profiles[profileCombo.SelectedIndex - 1].Directory;
                }
                else
                {
                    // 默认浏览器：尝试找 chrome/edge
                    var defaultPaths = new[]
                    {
                        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
                    };
                    browserPath = defaultPaths.FirstOrDefault(System.IO.File.Exists) ?? "";
                    if (string.IsNullOrEmpty(browserPath))
                    {
                        MessageBox.Show("未找到可用浏览器，请在浏览器下拉框中选择一个已注册的浏览器", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // 禁用按钮
                pickSelectorBtn.IsEnabled = false;
                pickSelectorBtn.Content = "选择中...";

                try
                {
                    var (port, process) = await Services.CdpBrowserService.Instance.LaunchVisibleBrowserAsync(browserPath, profileDir, urlBox.Text.Trim());
                    if (process == null)
                    {
                        MessageBox.Show("启动浏览器失败\n\n可能原因：该浏览器已在运行中，请先关闭浏览器后重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    try
                    {
                        var selector = await Services.CdpBrowserService.Instance.PickElementSelectorAsync(port, urlBox.Text.Trim());
                        if (!string.IsNullOrEmpty(selector))
                        {
                            selectorBox.Text = selector;
                        }
                        else
                        {
                            MessageBox.Show("未选择到元素（超时或取消）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    finally
                    {
                        // 关闭浏览器
                        try
                        {
                            if (!process.HasExited)
                                process.Kill();
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"选择器获取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    pickSelectorBtn.IsEnabled = true;
                    pickSelectorBtn.Content = "选择";
                }
            };

            // 刷新间隔
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = "刷新间隔（秒）", Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var intervalBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = "60", Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(intervalBox);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("StandardButtonStyle"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button { Content = "保存", Style = (Style)FindResource("PrimaryButtonStyle"), Padding = new Thickness(16, 8, 16, 8) };
            okBtn.Click += (s, ev) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text) || string.IsNullOrWhiteSpace(selectorBox.Text))
                {
                    MessageBox.Show("请填写名称、目标网址和选择器", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int interval = 60;
                int.TryParse(intervalBox.Text, out interval);
                if (interval < 10) interval = 10;

                var template = new Models.DataTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = nameBox.Text.Trim(),
                    Description = descBox.Text.Trim(),
                    Category = categoryBox.Text.Trim(),
                    TargetUrl = urlBox.Text.Trim(),
                    Selector = selectorBox.Text.Trim(),
                    RefreshIntervalSeconds = interval,
                    DefaultBrowserId = browserCombo.SelectedIndex > 0 ? _registry.Browsers[browserCombo.SelectedIndex - 1].Id : null,
                    DefaultProfileId = (browserCombo.SelectedIndex > 0 && profileCombo.SelectedIndex > 0)
                        ? _registry.Browsers[browserCombo.SelectedIndex - 1].Profiles[profileCombo.SelectedIndex - 1].Id
                        : null
                };

                var manager = DataTemplateManager.Load();
                manager.SaveUserTemplate(template);
                dialog.Close();
                RefreshTemplateList();
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            panel.Children.Add(btnPanel);

            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private void RemoveTemplate(string templateId)
        {
            if (MessageBox.Show("确定删除该模版？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            // 从 user-templates.json 中删除
            var userFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json");
            if (System.IO.File.Exists(userFile))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(userFile);
                    var list = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<Models.DataTemplate>>(json) ?? new();
                    list.RemoveAll(t => t.Id == templateId);
                    System.IO.File.WriteAllText(userFile, Newtonsoft.Json.JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
                }
                catch { }
            }
            RefreshTemplateList();
        }

        #endregion

        #region 自动更新

        private UpdateInfo? _newVersionUpdateInfo;
        private bool _isCheckingUpdate;
        private bool _isDownloadingUpdate;
        private bool _updateEventsSubscribed;

        private void InitUpdateUI()
        {
            // 设置开关状态
            autoUpdateSwitch.IsChecked = _app.AppSettings.EnableAutoUpdate;

            // 显示当前版本
            try
            {
                var mgr = new UpdateManager(""); // dummy，仅获取版本
                if (mgr.IsInstalled)
                    versionText.Text = $"版本 v{mgr.CurrentVersion}";
            }
            catch { }

            // 订阅更新事件
            SubscribeUpdateEvents();

            // 如果已有更新就绪，直接显示
            if (VelopackUpdateService.Instance.IsUpdateReady)
            {
                applyUpdatePanel.Visibility = Visibility.Visible;
                updateStatusText.Text = "更新包已就绪，重启即可生效！";
                checkUpdateBtn.IsEnabled = false;
            }
        }

        private void SubscribeUpdateEvents()
        {
            if (_updateEventsSubscribed) return;
            _updateEventsSubscribed = true;

            VelopackUpdateService.Instance.UpdateStatusChanged += status =>
            {
                Dispatcher.Invoke(() => updateStatusText.Text = status);
            };

            VelopackUpdateService.Instance.DownloadProgressChanged += progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    downloadProgressBar.Value = progress;
                    progressPercentText.Text = $"{progress}%";
                    if (progress >= 100)
                    {
                        downloadProgressPanel.Visibility = Visibility.Collapsed;
                        applyUpdatePanel.Visibility = Visibility.Visible;
                        _isDownloadingUpdate = false;
                    }
                });
            };

            VelopackUpdateService.Instance.UpdateReadyChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    applyUpdatePanel.Visibility = Visibility.Visible;
                    checkUpdateBtn.IsEnabled = false;
                });
            };
        }

        private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
        {
            _app.AppSettings.EnableAutoUpdate = autoUpdateSwitch.IsChecked == true;
            _app.AppSettings.Save();
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate || _isDownloadingUpdate) return;

            _isCheckingUpdate = true;
            checkUpdateBtn.IsEnabled = false;
            applyUpdatePanel.Visibility = Visibility.Collapsed;
            downloadProgressPanel.Visibility = Visibility.Collapsed;

            try
            {
                var updateInfo = await VelopackUpdateService.Instance.CheckForUpdatesAsync();
                if (updateInfo != null)
                {
                    _newVersionUpdateInfo = updateInfo;
                    // 自动开始下载
                    _isDownloadingUpdate = true;
                    downloadProgressPanel.Visibility = Visibility.Visible;
                    downloadProgressBar.Value = 0;
                    progressPercentText.Text = "0%";

                    var success = await VelopackUpdateService.Instance.DownloadUpdatesAsync(updateInfo);
                    if (!success)
                    {
                        downloadProgressPanel.Visibility = Visibility.Collapsed;
                        checkUpdateBtn.IsEnabled = true;
                    }
                }
                else
                {
                    checkUpdateBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                updateStatusText.Text = $"检查失败: {ex.Message}";
                checkUpdateBtn.IsEnabled = true;
            }
            finally
            {
                _isCheckingUpdate = false;
                if (!_isDownloadingUpdate)
                    checkUpdateBtn.IsEnabled = true;
            }
        }

        private void ApplyUpdateAndRestart_Click(object sender, RoutedEventArgs e)
        {
            var updateInfo = VelopackUpdateService.Instance.ReadyUpdateInfo ?? _newVersionUpdateInfo;
            if (updateInfo == null)
            {
                MessageBox.Show("更新信息不存在，请重新检查更新。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                VelopackUpdateService.Instance.ApplyAndRestart(updateInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用更新并重启失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 小屏窗口管理

        /// <summary>加载并显示所有窗口（本地 + 服务器团队窗口）</summary>
        private async Task RefreshWindowListAsync()
        {
            if (windowListPanel == null) return;
            windowListPanel.Children.Clear();

            var app = (App)Application.Current;
            var mgr = app.WindowConfigs;

            // 本地窗口
            if (mgr.Windows.Count > 0)
            {
                windowListPanel.Children.Add(BuildSectionHeader("我的窗口"));
                windowListPanel.Children.Add(BuildListHeaderRow());
                foreach (var win in mgr.Windows)
                {
                    windowListPanel.Children.Add(BuildWindowRow(win.Name, win.OwnerType, win.ShareMode, win.ServerId, win.Id, isVisible: true));
                }
            }

            // 从服务器拉取团队窗口（仅已登录时）
            var token = app.AppSettings.AccountToken;
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get, "http://localhost:3007/api/sidebar/bar/visible");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var resp = await http.SendAsync(req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200 && root["data"] is Newtonsoft.Json.Linq.JArray bars)
                    {
                        var teamBars = bars.Where(b => b["ownerType"]?.ToString() == "team").ToList();
                        if (teamBars.Count > 0)
                        {
                            windowListPanel.Children.Add(BuildSectionHeader("团队窗口"));
                            windowListPanel.Children.Add(BuildListHeaderRow());
                            foreach (var bar in teamBars)
                            {
                                string title = bar["title"]?.ToString() ?? "未命名";
                                bool canEdit = bar["canEdit"]?.ToObject<bool>() ?? false;
                                bool canDelete = bar["canDelete"]?.ToObject<bool>() ?? false;
                                string perm = canEdit ? "可编辑" : "只读";
                                windowListPanel.Children.Add(BuildWindowRow(title, "team", null, bar["barServerId"]?.ToString(), null, perm, true));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Settings] 拉取窗口列表失败: {ex.Message}");
                }
            }

            if (windowListPanel.Children.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = "暂无窗口，请在主界面创建小屏窗口",
                    Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                    FontSize = 13,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                windowListPanel.Children.Add(hint);
            }
        }

        private UIElement BuildSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 16, 0, 8)
            };
        }

        /// <summary>列表表头行</summary>
        private UIElement BuildListHeaderRow()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var mutedBrush = (System.Windows.Media.Brush)FindResource("BrushTextMuted");
            void AddHeader(string text, int col)
            {
                var tb = new TextBlock { Text = text, FontSize = 11, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(tb, col);
                grid.Children.Add(tb);
            }
            AddHeader("显示", 0);
            AddHeader("名称", 1);
            AddHeader("类型", 2);
            AddHeader("", 3);
            return grid;
        }

        /// <summary>构建窗口列表行：[✓] 名称  类型  ⋯</summary>
        private UIElement BuildWindowRow(string name, string ownerType, string? shareMode, string? serverId, string? localId, string? permText = null, bool isVisible = true)
        {
            var border = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrushInputBG"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            // Col 0: 显示 checkbox
            var chk = new CheckBox
            {
                IsChecked = isVisible,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = serverId ?? localId
            };
            chk.Checked += WindowVisible_Changed;
            chk.Unchecked += WindowVisible_Changed;
            Grid.SetColumn(chk, 0);
            grid.Children.Add(chk);

            // Col 1: 名称
            var nameText = new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // Col 2: 类型标签
            string tag = ownerType == "team" ? "团队" : "个人";
            if (shareMode == "copy") tag = "复制";
            else if (shareMode == "reference") tag = "引用";
            if (permText != null) tag = permText;

            var tagText = new TextBlock
            {
                Text = tag,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tagText, 2);
            grid.Children.Add(tagText);

            // Col 3: ⋯ 按钮
            var moreBtn = new Button
            {
                Content = "⋯",
                FontSize = 14,
                Width = 28,
                Height = 24,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = new WindowRowTag { ServerId = serverId, LocalId = localId, OwnerType = ownerType }
            };
            moreBtn.Click += WindowMore_Click;
            Grid.SetColumn(moreBtn, 3);
            grid.Children.Add(moreBtn);

            border.Child = grid;
            return border;
        }

        /// <summary>⋯ 按钮点击 → 弹出 ContextMenu</summary>
        private void WindowMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not WindowRowTag tag) return;

            var menu = new ContextMenu
            {
                Style = (Style)FindResource("DarkContextMenuStyle")
            };

            var editItem = new MenuItem { Header = "编辑" };
            editItem.Click += (_, _) => WindowEdit_Click(tag);
            menu.Items.Add(editItem);

            if (!string.IsNullOrEmpty(tag.ServerId))
            {
                var shareItem = new MenuItem { Header = "分享" };
                shareItem.Click += (_, _) => ShareWindow_Click(new Button { Tag = tag.ServerId }, new RoutedEventArgs());
                menu.Items.Add(shareItem);
            }

            var deleteItem = new MenuItem { Header = "删除", Foreground = System.Windows.Media.Brushes.IndianRed };
            deleteItem.Click += (_, _) => WindowDelete_Click(tag);
            menu.Items.Add(deleteItem);

            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void WindowEdit_Click(WindowRowTag tag)
        {
            // TODO: 打开窗口编辑（重命名等）
            MessageBox.Show("编辑功能开发中", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void WindowDelete_Click(WindowRowTag tag)
        {
            var result = MessageBox.Show("确定要删除该窗口吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var app = (App)Application.Current;
            if (!string.IsNullOrEmpty(tag.LocalId))
            {
                app.WindowConfigs.Remove(tag.LocalId);
                // 如果有 ServerId 则同步删除服务器端
                if (!string.IsNullOrEmpty(tag.ServerId))
                {
                    _ = Services.CloudSyncService.Instance.DeleteBarOnServerAsync(tag.ServerId);
                }
            }
            _ = RefreshWindowListAsync();
        }

        private void WindowVisible_Changed(object sender, RoutedEventArgs e)
        {
            // TODO: 同步可见性到服务器 (PUT /sidebar/layout)
        }

        /// <summary>窗口行 Tag 数据</summary>
        private class WindowRowTag
        {
            public string? ServerId { get; set; }
            public string? LocalId { get; set; }
            public string? OwnerType { get; set; }
        }

        /// <summary>分享窗口按钮点击</summary>
        private async void ShareWindow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string serverId) return;

            // 简单对话框选择分享模式
            var dialog = new System.Windows.Window
            {
                Title = "创建分享",
                Width = 320, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "选择分享模式：", FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

            var copyRadio = new RadioButton { Content = "复制（对方获得独立副本）", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
            var refRadio = new RadioButton { Content = "引用（对方看到你的实时数据）", Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(copyRadio);
            panel.Children.Add(refRadio);

            var confirmBtn = new Button
            {
                Content = "创建分享链接",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(confirmBtn);
            dialog.Content = panel;

            confirmBtn.Click += async (_, _) =>
            {
                string mode = copyRadio.IsChecked == true ? "copy" : "reference";
                try
                {
                    var app = (App)Application.Current;
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { barServerId = serverId, shareMode = mode });
                    using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "http://localhost:3007/api/sidebar/share/create") { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                    using var resp = await http.SendAsync(req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200)
                    {
                        string code = root["data"]?["shareCode"]?.ToString() ?? "";
                        dialog.Close();
                        MessageBox.Show($"分享创建成功！\n\n短码：{code}\n链接：http://localhost:3007/s/{code}\n\n将短码或链接发给对方即可导入。", "分享成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(root["msg"]?.ToString() ?? "创建分享失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"创建分享失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>导入窗口按钮点击</summary>
        private async void ImportWindow_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Window
            {
                Title = "导入窗口",
                Width = 340, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "输入分享短码或链接：", FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

            var inputBox = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(inputBox);

            var confirmBtn = new Button
            {
                Content = "导入",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(confirmBtn);
            dialog.Content = panel;

            confirmBtn.Click += async (_, _) =>
            {
                string input = inputBox.Text.Trim();
                if (string.IsNullOrEmpty(input)) return;

                // 从 URL 提取短码
                string code = input;
                if (input.Contains("/s/"))
                {
                    var parts = input.Split("/s/");
                    if (parts.Length > 1) code = parts[1].Trim().TrimEnd('/');
                }

                try
                {
                    var app = (App)Application.Current;
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { shareCode = code });
                    using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "http://localhost:3007/api/sidebar/share/import") { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                    using var resp = await http.SendAsync(req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200)
                    {
                        string? barServerId = root["data"]?["barServerId"]?.ToString();
                        string? shareMode = root["data"]?["shareMode"]?.ToString();
                        string? sourceBarId = root["data"]?["sourceBarId"]?.ToString();

                        // 添加到本地窗口配置
                        var newWin = new Models.WindowConfig
                        {
                            Name = "导入窗口",
                            ServerId = barServerId,
                            ShareMode = shareMode,
                            SourceBarId = sourceBarId,
                            OwnerType = "personal"
                        };
                        app.WindowConfigs.Add(newWin);

                        dialog.Close();
                        MessageBox.Show($"导入成功！窗口已添加到你的小屏列表。\n模式：{(shareMode == "copy" ? "复制" : "引用")}", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        _ = RefreshWindowListAsync();
                    }
                    else
                    {
                        MessageBox.Show(root["msg"]?.ToString() ?? "导入失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>我的分享列表</summary>
        private async void MyShares_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            var token = app.AppSettings.AccountToken;
            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show("请先登录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, "http://localhost:3007/api/sidebar/share/list/mine");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var resp = await http.SendAsync(req);
                string json = await resp.Content.ReadAsStringAsync();
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                if (root["code"]?.ToObject<int>() == 200 && root["data"] is Newtonsoft.Json.Linq.JArray shares)
                {
                    if (shares.Count == 0)
                    {
                        MessageBox.Show("暂无分享记录", "我的分享", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var lines = shares.Select(s =>
                    {
                        string title = s["barTitle"]?.ToString() ?? "未命名";
                        string mode = s["shareMode"]?.ToString() == "copy" ? "复制" : "引用";
                        string code = s["shareCode"]?.ToString() ?? "";
                        var maxUses = s["maxUses"]?.ToObject<int>() ?? 0;
                        string usesText = maxUses == 0 ? "∞" : maxUses.ToString();
                        string used = s["usedCount"]?.ToString() ?? "0";
                        string views = s["viewCount"]?.ToString() ?? "0";
                        return $"• {title}  [{mode}]  短码: {code}  使用: {used}/{usesText}  查看: {views}";
                    }).ToList();

                    MessageBox.Show(string.Join("\n", lines), "我的分享", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(root["msg"]?.ToString() ?? "获取分享列表失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"获取分享列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
