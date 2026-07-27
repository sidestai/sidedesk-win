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

            InitUpdateUI();
        }

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (pageGeneral == null || pageAccount == null || pageCloudSpaces == null || pageTeams == null || pageAppearance == null || pageProgramManager == null || pageExtensions == null || pageTools == null || pageModules == null || pageShortcuts == null || pageAbout == null) return;
                    
            // 隐藏所有页面
            pageGeneral.Visibility = Visibility.Collapsed;
            pageAccount.Visibility = Visibility.Collapsed;
            pageCloudSpaces.Visibility = Visibility.Collapsed;
            pageTeams.Visibility = Visibility.Collapsed;
            pageAppearance.Visibility = Visibility.Collapsed;
            pageProgramManager.Visibility = Visibility.Collapsed;
            pageExtensions.Visibility = Visibility.Collapsed;
            pageTools.Visibility = Visibility.Collapsed;
            pageModules.Visibility = Visibility.Collapsed;
            pageShortcuts.Visibility = Visibility.Collapsed;
            pageAbout.Visibility = Visibility.Collapsed;
                    
            // 显示选中页面
            switch (navList.SelectedIndex)
            {
                case 0: pageGeneral.Visibility = Visibility.Visible; break;
                case 1: pageAccount.Visibility = Visibility.Visible; RefreshAccountUI(); break;
                case 2: pageTeams.Visibility = Visibility.Visible; _ = RefreshTeamListAsync(); break;
                case 3: pageCloudSpaces.Visibility = Visibility.Visible; RefreshCloudSpaceList(); break;
                case 4: pageAppearance.Visibility = Visibility.Visible; break;
                case 5: pageProgramManager.Visibility = Visibility.Visible; break;
                case 6: pageExtensions.Visibility = Visibility.Visible; break;
                case 7: pageTools.Visibility = Visibility.Visible; break;
                case 8: pageModules.Visibility = Visibility.Visible; RefreshTemplateList(); break;
                case 9: pageShortcuts.Visibility = Visibility.Visible; break;
                case 10: pageAbout.Visibility = Visibility.Visible; break;
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
                accountSubText.Text = "登录后可开启云同步备份";
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
                s.Save();
                try { Services.CloudSyncService.Instance.Stop(); } catch { }
                RefreshAccountUI();
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
                s.Save();
                RefreshAccountUI();
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
                teamListPanel.Children.Add(BuildTeamHintText("加载失败，请重试"));
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

        /// <summary>构建单个团队行：团队名 + 成员头像堆叠 + 成员数</summary>
        private UIElement BuildTeamRow(Team team)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 6, 4, 6)
            };

            // 团队名
            row.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = team.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = (double)FindResource("FontSizeNormal"),
                VerticalAlignment = VerticalAlignment.Center
            });

            // 成员头像堆叠（最多 5 个，超出显示 +N）
            var members = team.Members ?? new List<TeamMember>();
            const int maxAvatars = 5;
            var avatarStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
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
            row.Children.Add(avatarStack);

            // 成员数
            row.Children.Add(new TextBlock
            {
                Text = $"{members.Count} 人",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });

            return row;
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
    }
}
