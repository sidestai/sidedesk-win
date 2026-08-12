using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
            themeModeCombo.Items.Add(Framework.Lang.T("深色"));
            themeModeCombo.Items.Add(Framework.Lang.T("浅色"));
            themeModeCombo.Items.Add(Framework.Lang.T("跟随系统"));

            switch (_app.AppSettings.ThemeMode?.ToLower())
            {
                case "light": themeModeCombo.SelectedIndex = 1; break;
                case "system": themeModeCombo.SelectedIndex = 2; break;
                default: themeModeCombo.SelectedIndex = 0; break;
            }

            // 配色主题（JSON 自定义主题）
            customThemeCombo.Items.Add(Framework.Lang.T("默认"));
            var customThemes = Services.ThemeManager.LoadThemes();
            foreach (var t in customThemes) customThemeCombo.Items.Add(t.Name);
            int customIdx = 0;
            var savedTheme = Framework.AppSettings.Instance.CurrentTheme;
            if (!string.IsNullOrWhiteSpace(savedTheme))
            {
                for (int i = 0; i < customThemes.Count; i++)
                {
                    if (string.Equals(customThemes[i].Name, savedTheme, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(customThemes[i].FileName, savedTheme, StringComparison.OrdinalIgnoreCase))
                    { customIdx = i + 1; break; }
                }
            }
            customThemeCombo.SelectedIndex = customIdx;

            // 背景透明度
            bgOpacityBox.Text = _app.AppSettings.BackgroundOpacity.ToString("F0");

            // 隐藏模块外框时同时隐藏标题
            hideModuleTitleSwitch.IsChecked = Framework.AppSettings.Instance.HideModuleTitle;

            // 显示通知条
            showNotificationStripSwitch.IsChecked = _app.AppSettings.ShowNotificationStrip;

            // 分享/团队组件边框颜色
            shareBorderHexBox.Text = Framework.AppSettings.Instance.ShareBorderColor;
            teamBorderHexBox.Text = Framework.AppSettings.Instance.TeamBorderColor;
            BuildBorderSwatches(shareBorderSwatches, shareBorderHexBox, isShare: true);
            BuildBorderSwatches(teamBorderSwatches, teamBorderHexBox, isShare: false);

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
                ? Framework.Lang.T("未设置") 
                : _registry.DefaultProfileBaseDir;

            titleLockSwitch.IsChecked = _app.AppSettings.EnableTitleLock;
            windowSwitchHotkeySwitch.IsChecked = _app.AppSettings.EnableWindowSwitchHotkey;
            mcpServerSwitch.IsChecked = _app.AppSettings.EnableMcpServer;
            var aiMode = _app.AppSettings.AIChatMode ?? "client";
            foreach (var item in aiChatModeCombo.Items)
            {
                if (item is ComboBoxItem ci && ci.Tag?.ToString() == aiMode)
                {
                    aiChatModeCombo.SelectedItem = ci;
                    break;
                }
            }
            if (aiChatModeCombo.SelectedIndex < 0)
                aiChatModeCombo.SelectedIndex = 0;
            string langPref = string.IsNullOrEmpty(_app.AppSettings.Language) ? "auto" : _app.AppSettings.Language;
            foreach (ComboBoxItem item in languageCombo.Items)
            {
                if (item.Tag is string t && t == langPref) { languageCombo.SelectedItem = item; break; }
            }
            if (languageCombo.SelectedIndex < 0) languageCombo.SelectedIndex = 0;
            RefreshMcpConfigStatus();
            httpServerSwitch.IsChecked = _app.AppSettings.EnableHttpServer;
            httpPortBox.Text = _app.AppSettings.HttpServerPort.ToString();
            UpdateHttpServerStatus();
            usageStatsSwitch.IsChecked = Framework.AppSettings.Instance.ShowUsageStats;
            autoStartSwitch.IsChecked = Framework.AppSettings.Instance.RunAtStartup;

            // 账户 / 云同步
            RefreshAccountUI();

            // 团队切换下拉框
            RefreshCorpSelector();

            InitUpdateUI();
        }

        private void Nav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (pageGeneral == null || pageAccount == null || pageCloudSpaces == null || pageTeams == null || pageFriends == null || pageAppearance == null || pageProgramManager == null || pagePlugins == null || pageTools == null || pageModules == null || pageMyData == null || pageWindows == null || pageShortcuts == null || pageAbout == null || pageDeveloper == null) return;
                    
            // 隐藏所有页面
            pageGeneral.Visibility = Visibility.Collapsed;
            pageAccount.Visibility = Visibility.Collapsed;
            pageCloudSpaces.Visibility = Visibility.Collapsed;
            pageTeams.Visibility = Visibility.Collapsed;
            pageFriends.Visibility = Visibility.Collapsed;
            pageAppearance.Visibility = Visibility.Collapsed;
            pageProgramManager.Visibility = Visibility.Collapsed;
            pagePlugins.Visibility = Visibility.Collapsed;
            pageTools.Visibility = Visibility.Collapsed;
            pageModules.Visibility = Visibility.Collapsed;
            pageMyData.Visibility = Visibility.Collapsed;
            pageWindows.Visibility = Visibility.Collapsed;
            pageShortcuts.Visibility = Visibility.Collapsed;
            pageAbout.Visibility = Visibility.Collapsed;
            pageDeveloper.Visibility = Visibility.Collapsed;
                    
            // 显示选中页面
            switch (navList.SelectedIndex)
            {
                case 0: pageGeneral.Visibility = Visibility.Visible; break;
                case 1: pageWindows.Visibility = Visibility.Visible; _ = RefreshWindowListAsync(); break;
                case 2: pageAccount.Visibility = Visibility.Visible; RefreshAccountUI(); break;
                case 3: pageTeams.Visibility = Visibility.Visible; _ = RefreshTeamListAsync(); break;
                case 4: pageFriends.Visibility = Visibility.Visible; _ = RefreshFriendsPageAsync(); break;
                case 5: pageCloudSpaces.Visibility = Visibility.Visible; RefreshCloudSpaceList(); break;
                case 6: pageAppearance.Visibility = Visibility.Visible; break;
                case 7: pageProgramManager.Visibility = Visibility.Visible; break;
                case 8: pagePlugins.Visibility = Visibility.Visible; RefreshPluginList(); break;
                case 9: pageTools.Visibility = Visibility.Visible; break;
                case 10: pageModules.Visibility = Visibility.Visible; RefreshModulesPage(); break;
                case 11: pageMyData.Visibility = Visibility.Visible; RefreshMyDataPage(); break;
                case 12: pageShortcuts.Visibility = Visibility.Visible; break;
                case 13: pageAbout.Visibility = Visibility.Visible; break;
                case 14: pageDeveloper.Visibility = Visibility.Visible; break;
            }
        }

        /// <summary>切换到好友页（供外部导航调用，如好友模块圆加号）</summary>
        public void SwitchToFriendsPage()
        {
            if (navList.SelectedIndex == 4)
                Nav_Changed(this, null!); // 已在好友页：手动触发以确保显示并刷新
            else
                navList.SelectedIndex = 4; // 触发 Nav_Changed 显示好友页
        }

        /// <summary>收到后端 friend_request 推送时刷新好友请求列表（覆盖自己加自己、多设备登录等场景，避免停留在本页时看不到新请求）</summary>
        public void OnFriendRequestReceived()
        {
            if (!IsLoaded) return;
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken)) return;
            _ = RefreshFriendRequestsAsync();
        }
        
        /// <summary>收到后端 friend_online/friend_offline 推送时刷新好友页（在线状态点与列表展示）</summary>
        public void OnFriendStatusChanged()
        {
            if (!IsLoaded) return;
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken)) return;
            _ = RefreshFriendsPageAsync();
        }

        /// <summary>收到后端团队相关 WS 推送时刷新团队页（membershipChanged=true 表示成员关系变化，同步刷新团队切换下拉）</summary>
        public void OnTeamEventReceived(bool membershipChanged = false)
        {
            if (!IsLoaded) return;
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken)) return;
            _ = RefreshTeamListAsync();
            if (membershipChanged) RefreshCorpSelector();
        }

        /// <summary>切换到账户页（供外部导航调用，如掉线通知点击）</summary>
        public void SwitchToAccountPage()
        {
            if (navList.SelectedIndex == 2)
                Nav_Changed(this, null!); // 已在账户页：手动触发以确保显示并刷新
            else
                navList.SelectedIndex = 2; // 触发 Nav_Changed 显示账户页
        }

        /// <summary>外部状态变化（如离线标记切换）时刷新账户页 UI</summary>
        public void RefreshAccountPage() => RefreshAccountUI();

        /// <summary>按页面名称导航（供通知动作 open_settings 调用）。
        /// 支持名称：general/windows/account/teams/friends/cloudspaces/appearance/programs/plugins/tools/modules/mydata/shortcuts/about</summary>
        public void NavigateToPage(string page)
        {
            int index = page.Trim().ToLowerInvariant() switch
            {
                "general" => 0,
                "windows" => 1,
                "account" => 2,
                "teams" => 3,
                "friends" => 4,
                "cloudspaces" or "cloud" => 5,
                "appearance" or "theme" => 6,
                "programs" or "programmanager" => 7,
                "plugins" => 8,
                "tools" => 9,
                "modules" => 10,
                "mydata" => 11,
                "shortcuts" => 12,
                "about" => 13,
                "developer" or "dev" => 14,
                _ => -1
            };
            if (index < 0) return;

            if (navList.SelectedIndex == index)
                Nav_Changed(this, null!);
            else
                navList.SelectedIndex = index;
        }

        #region 开发者测试面板

        private int _f12Count;
        private bool _devTestRevealed;

        /// <summary>在开发者页面连按 10 次 F12 显示隐藏测试面板</summary>
        private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F12) return;
            if (pageDeveloper == null || pageDeveloper.Visibility != Visibility.Visible) return;
            if (_devTestRevealed) return;

            _f12Count++;
            if (_f12Count >= 10)
            {
                _devTestRevealed = true;
                RevealDevTestPanel();
            }
        }

        private void RevealDevTestPanel()
        {
            BuildNotificationTestPanel();
            devTestHost.Visibility = Visibility.Visible;
        }

        /// <summary>构建通知系统测试面板：每种通知类型一个发送按钮</summary>
        private void BuildNotificationTestPanel()
        {
            devTestHost.Children.Clear();

            devTestHost.Children.Add(MakeSectionHeader(Framework.Lang.T("🔔 通知系统测试"), "点击按钮即可模拟 WebSocket 推送对应通知，用于验证显示效果"));

            // 显示模式
            devTestHost.Children.Add(MakeTestSection(Framework.Lang.T("显示模式"), new (string, Action)[]
            {
                (Framework.Lang.T("横幅通知"), () => Send(n => { n.Title = Framework.Lang.T("横幅通知"); n.Body = Framework.Lang.T("这是一条普通横幅，8 秒后自动消失"); })),
                (Framework.Lang.T("弹出对话框 (Alert)"), () => Send(n => {
                    n.Title = Framework.Lang.T("重要提醒"); n.Body = Framework.Lang.T("这是一条需要用户确认的弹出通知");
                    n.DisplayMode = NotificationDisplayMode.Alert;
                    n.Buttons = new List<NotificationAction> { Act("open_url", "查看详情", ("url", "https://www.baidu.com")), Act("dismiss", "关闭") };
                })),
                (Framework.Lang.T("静默通知 (Silent)"), () => Send(n => {
                    n.Title = Framework.Lang.T("静默通知"); n.Body = Framework.Lang.T("不会弹出，仅进入通知中心（点右上角⋯→通知中心查看）");
                    n.DisplayMode = NotificationDisplayMode.Silent;
                })),
            }));

            // 时效与优先级
            devTestHost.Children.Add(MakeTestSection(Framework.Lang.T("时效与优先级"), new (string, Action)[]
            {
                (Framework.Lang.T("常驻通知 (timeout=0)"), () => Send(n => { n.Title = Framework.Lang.T("常驻通知"); n.Body = Framework.Lang.T("不会自动消失，需手动关闭"); n.Timeout = 0; })),
                (Framework.Lang.T("高优先级 + 声音"), () => Send(n => { n.Title = Framework.Lang.T("紧急通知"); n.Body = Framework.Lang.T("优先级 Critical，播放提示音"); n.Priority = NotificationPriority.Critical; n.Sound = true; })),
                (Framework.Lang.T("低优先级"), () => Send(n => { n.Title = Framework.Lang.T("低优先级通知"); n.Body = Framework.Lang.T("多个通知并发时会排在后面"); n.Priority = NotificationPriority.Low; })),
                (Framework.Lang.T("连发 5 条 (测限流)"), SendBurst),
            }));

            // 外观
            devTestHost.Children.Add(MakeTestSection(Framework.Lang.T("外观"), new (string, Action)[]
            {
                (Framework.Lang.T("自定义颜色"), () => Send(n => { n.Title = Framework.Lang.T("自定义配色"); n.Body = Framework.Lang.T("背景与文字颜色均可自定义"); n.BackgroundColor = "#1a1a2e"; n.TextColor = "#e94560"; })),
                (Framework.Lang.T("大图通知"), () => Send(n => { n.Title = Framework.Lang.T("大图通知"); n.Body = Framework.Lang.T("顶部展示一张图片"); n.LargeImage = "https://picsum.photos/400/160"; })),
                (Framework.Lang.T("进度通知 (模拟下载)"), SendProgress),
            }));

            // Action 按钮
            devTestHost.Children.Add(MakeTestSection(Framework.Lang.T("Action 按钮"), new (string, Action)[]
            {
                ("open_url", () => Send(n => { n.Title = Framework.Lang.T("打开网页"); n.Body = Framework.Lang.T("点击下方按钮打开百度"); n.Buttons = new List<NotificationAction> { Act("open_url", "打开百度", ("url", "https://www.baidu.com")) }; })),
                ("open_webview", () => Send(n => { n.Title = Framework.Lang.T("内嵌网页窗口"); n.Body = Framework.Lang.T("点击按钮在独立 WebView2 窗口打开百度"); n.Buttons = new List<NotificationAction> { Act("open_webview", "打开网页窗口", ("url", "https://www.baidu.com"), ("title", "网页")) }; })),
                ("open_settings", () => Send(n => { n.Title = Framework.Lang.T("打开设置"); n.Body = Framework.Lang.T("点击按钮跳转到外观设置页"); n.Buttons = new List<NotificationAction> { Act("open_settings", "去设置", ("tab", "appearance")) }; })),
                ("open_file", () => Send(n => { n.Title = Framework.Lang.T("打开文件"); n.Body = Framework.Lang.T("点击按钮打开记事本"); n.Buttons = new List<NotificationAction> { Act("open_file", "打开记事本", ("filePath", "notepad.exe")) }; })),
                ("show_file_location", () => Send(n => { n.Title = Framework.Lang.T("定位文件"); n.Body = Framework.Lang.T("在资源管理器中显示本程序位置"); n.Buttons = new List<NotificationAction> { Act("show_file_location", "定位", ("filePath", AppDomain.CurrentDomain.BaseDirectory + "SideDesk.exe")) }; })),
                ("show_messagebox", () => Send(n => { n.Title = Framework.Lang.T("消息框"); n.Body = Framework.Lang.T("点击按钮弹出自定义按钮的消息框");
                    var subBtns = new Newtonsoft.Json.Linq.JArray
                    {
                        new Newtonsoft.Json.Linq.JObject { ["label"] = Framework.Lang.T("好的"), ["type"] = "dismiss" },
                        new Newtonsoft.Json.Linq.JObject { ["label"] = Framework.Lang.T("取消"), ["type"] = "dismiss" }
                    };
                    n.Buttons = new List<NotificationAction> { Act("show_messagebox", "弹出消息框", ("message", "这是通知触发的消息框"), ("buttons", subBtns)) }; })),
                ("run_command", () => Send(n => { n.Title = Framework.Lang.T("执行命令"); n.Body = Framework.Lang.T("后台运行一条命令"); n.Buttons = new List<NotificationAction> { Act("run_command", "运行", ("command", "echo hello > %TEMP%\\notif_test.txt"), ("runInBackground", true)) }; })),
                ("input_reply", () => Send(n => { n.Title = Framework.Lang.T("快速回复"); n.Body = Framework.Lang.T("点击按钮输入回复内容"); n.Buttons = new List<NotificationAction> { Act("input_reply", "回复", ("placeholder", "输入回复..."), ("replyActionId", "test_reply")) }; })),
                ("snooze", () => Send(n => { n.Title = Framework.Lang.T("稍后提醒"); n.Body = Framework.Lang.T("点击按钮 1 分钟后再次提醒"); n.Buttons = new List<NotificationAction> { Act("snooze", "1 分钟后提醒", ("snoozeInterval", 1)) }; })),
                ("dismiss", () => Send(n => { n.Title = Framework.Lang.T("可关闭"); n.Body = Framework.Lang.T("点击按钮关闭本通知"); n.Buttons = new List<NotificationAction> { Act("dismiss", "知道了") }; })),
            }));

            // 点击主体 (click_main)
            devTestHost.Children.Add(MakeTestSection(Framework.Lang.T("点击主体 (click_main)"), new (string, Action)[]
            {
                (Framework.Lang.T("点击通知打开网页"), () => Send(n => { n.Title = Framework.Lang.T("点我打开百度"); n.Body = Framework.Lang.T("点击通知主体即可跳转"); n.ClickMain = Act("open_url", null, ("url", "https://www.baidu.com")); })),
            }));
        }

        /// <summary>发送一条测试通知（默认 Banner 模式）</summary>
        private static void Send(Action<NotificationPayload> configure)
        {
            var n = new NotificationPayload();
            configure(n);
            NotificationService.Instance.Enqueue(n);
        }

        /// <summary>构造一个通知动作</summary>
        private static NotificationAction Act(string type, string? label, params (string key, object value)[] payload)
        {
            var action = new NotificationAction { Type = type, Label = label };
            if (payload.Length > 0)
            {
                action.Payload = new Dictionary<string, object>();
                foreach (var (k, v) in payload) action.Payload[k] = v;
            }
            return action;
        }

        /// <summary>连发 5 条通知，验证队列限流（最多同时 3 条）</summary>
        private static void SendBurst()
        {
            for (int i = 1; i <= 5; i++)
            {
                var n = new NotificationPayload { Title = Framework.Lang.TF("批量通知 #{0}", i), Body = Framework.Lang.TF("第 {0} 条，验证限流排队", i) };
                NotificationService.Instance.Enqueue(n);
            }
        }

        /// <summary>发送一条进度通知并模拟进度推进</summary>
        private void SendProgress()
        {
            var groupId = "test-progress-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Send(n =>
            {
                n.Title = Framework.Lang.T("正在下载文件...");
                n.Body = "0%";
                n.GroupId = groupId;
                n.Progress = 0;
                n.Timeout = 0;
            });

            int progress = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) =>
            {
                progress += 10;
                if (progress > 100)
                {
                    timer.Stop();
                    return;
                }
                NotificationService.Instance.UpdateProgress(groupId, progress, progress >= 100 ? Framework.Lang.T("下载完成 ✅") : $"{progress}%");
                if (progress >= 100) timer.Stop();
            };
            timer.Start();
        }

        /// <summary>测试面板分区标题</summary>
        private StackPanel MakeSectionHeader(string title, string subtitle)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain")
            });
            sp.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = (double)FindResource("FontSizeSmall"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            return sp;
        }

        /// <summary>测试分区卡片：标题 + 按钮网格</summary>
        private Border MakeTestSection(string title, (string label, Action action)[] buttons)
        {
            var wrap = new WrapPanel();
            foreach (var (label, action) in buttons)
            {
                var btn = new Button
                {
                    Content = label,
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 8)
                };
                var act = action;
                btn.Click += (s, e) => act();
                wrap.Children.Add(btn);
            }

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            stack.Children.Add(wrap);

            return new Border
            {
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12),
                Child = stack
            };
        }

        #endregion

        #region 账户 / 云同步

        /// <summary>根据当前登录/同步状态刷新账户页 UI（离线时视为未登录）</summary>
        private void RefreshAccountUI()
        {
            if (accountStatusText == null) return; // 尚未加载完成
            var s = _app.AppSettings;
            bool loggedIn = !string.IsNullOrEmpty(s.AccountToken) && !App.AccountOffline;

            if (loggedIn)
            {
                accountStatusText.Text = Framework.Lang.T("已登录") + (string.IsNullOrEmpty(s.AccountName) ? "" : "：" + s.AccountName);
                accountSubText.Text = s.AccountLoginTime.HasValue
                    ? Framework.Lang.TF("登录时间：{0:yyyy-MM-dd HH:mm}", s.AccountLoginTime)
                    : Framework.Lang.T("已登录");
                accountActionBtn.Content = Framework.Lang.T("退出登录");
            }
            else
            {
                accountStatusText.Text = Framework.Lang.T("未登录");
                // 离线（auth 请求失败）→ 离线提示；Token 过期→过期提示；否则默认引导文本
                if (App.AccountOffline)
                    accountSubText.Text = Framework.Lang.T("离线：无法连接服务器，当前视为未登录");
                else if (!string.IsNullOrEmpty(App.AccountTokenExpiredMessage))
                    accountSubText.Text = App.AccountTokenExpiredMessage;
                else
                    accountSubText.Text = Framework.Lang.T("登录后可开启云同步备份");
                accountActionBtn.Content = Framework.Lang.T("登录");
            }

            // 云同步开关：仅登录后可用
            _isLoading = true;
            cloudSyncSwitch.IsChecked = s.EnableCloudSync && loggedIn;
            _isLoading = false;
            cloudSyncSwitch.IsEnabled = loggedIn;
            syncNowBtn.IsEnabled = loggedIn;
            encryptSyncSwitch.IsChecked = s.SyncEncryptEnabled && loggedIn;
            encryptSyncSwitch.IsEnabled = loggedIn && s.EnableCloudSync;
            restoreSettingsBtn.IsEnabled = loggedIn;

            // 个人资料详情：仅登录后显示
            accountProfileSection.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

            if (s.LastCloudSyncTime.HasValue)
                cloudSyncStatusText.Text = Framework.Lang.TF("上次同步：{0:yyyy-MM-dd HH:mm}", s.LastCloudSyncTime);
            else
                cloudSyncStatusText.Text = loggedIn ? Framework.Lang.T("尚未同步") : Framework.Lang.T("登录后可同步");

            UpdateAccountProfileUI();
            RefreshBackupUI();
        }

        /// <summary>刷新账户页"个人资料"区域（昵称 + 头像预览）</summary>
        private void UpdateAccountProfileUI()
        {
            if (nicknameBox == null) return;
            var s = _app.AppSettings;

            _isLoading = true;
            nicknameBox.Text = s.AccountName ?? "";
            _isLoading = false;

            // 展示态昵称：未设置时显示占位提示
            nicknameDisplayText.Text = string.IsNullOrWhiteSpace(s.AccountName) ? Framework.Lang.T("未设置") : s.AccountName!;
            nicknameDisplayText.Foreground = string.IsNullOrWhiteSpace(s.AccountName)
                ? (System.Windows.Media.Brush)FindResource("BrushTextMuted")
                : (System.Windows.Media.Brush)FindResource("BrushTextMain");

            // 兜底头像文字：取昵称首字符
            accountAvatarFallbackText.Text = string.IsNullOrWhiteSpace(s.AccountName)
                ? "?"
                : s.AccountName!.Trim().Substring(0, 1).ToUpper();

            if (!string.IsNullOrWhiteSpace(s.AccountAvatar))
            {
                try
                {
                    var bytes = Convert.FromBase64String(s.AccountAvatar);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    accountAvatarImageBrush.ImageSource = bmp;
                    accountAvatarImageHost.Visibility = Visibility.Visible;
                    accountAvatarFallbackText.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { }
            }
            accountAvatarImageBrush.ImageSource = null;
            accountAvatarImageHost.Visibility = Visibility.Collapsed;
            accountAvatarFallbackText.Visibility = Visibility.Visible;
        }

        /// <summary>点击头像 → 触发选择头像</summary>
        private void AccountAvatar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ChooseAccountAvatar();
        }

        /// <summary>选择图片文件并压缩为 base64 存入配置</summary>
        private void ChooseAccountAvatar()
        {
            var dlg = new OpenFileDialog
            {
                Title = Framework.Lang.T("选择头像图片"),
                Filter = Framework.Lang.T("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*")
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var s = _app.AppSettings;
                s.AccountAvatar = ImageFileToAvatarBase64(dlg.FileName);
                s.Save();
                UpdateAccountProfileUI();
                // 头像直传 OSS 缓存区并同步后端（后端随后 WS 通知全部好友）；失败不阻断本地保存
                _ = SyncAvatarToBackendAsync(s.AccountAvatar);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.T("头像加载失败：") + ex.Message, Framework.Lang.T("头像"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>悬停昵称：浮现输入框外观 + 编辑提示</summary>
        private void NicknameDisplay_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            nicknameDisplayHost.Background = (System.Windows.Media.Brush)FindResource("BrushListViewItemMouseOver");
            nicknameDisplayHost.BorderBrush = (System.Windows.Media.Brush)FindResource("BrushBorder");
            nicknameEditHint.Visibility = Visibility.Visible;
        }

        /// <summary>离开昵称：恢复纯文本外观</summary>
        private void NicknameDisplay_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            nicknameDisplayHost.Background = System.Windows.Media.Brushes.Transparent;
            nicknameDisplayHost.BorderBrush = System.Windows.Media.Brushes.Transparent;
            nicknameEditHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>点击昵称 → 进入编辑态</summary>
        private void NicknameDisplay_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            EnterNicknameEdit();
        }

        /// <summary>进入昵称编辑态：显示输入框 + 保存/取消按钮</summary>
        private void EnterNicknameEdit()
        {
            nicknameBox.Text = _app.AppSettings.AccountName ?? "";
            nicknameDisplayHost.Visibility = Visibility.Collapsed;
            nicknameEditHost.Visibility = Visibility.Visible;
            nicknameBox.Focus();
            nicknameBox.SelectAll();
        }

        /// <summary>退出编辑态，回到纯文本展示</summary>
        private void ExitNicknameEdit()
        {
            nicknameEditHost.Visibility = Visibility.Collapsed;
            nicknameDisplayHost.Visibility = Visibility.Visible;
        }

        /// <summary>编辑框按键：Enter 保存，Esc 取消</summary>
        private void NicknameBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                SaveNickname_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                ExitNicknameEdit();
                e.Handled = true;
            }
        }

        /// <summary>取消编辑昵称</summary>
        private void CancelNickname_Click(object sender, RoutedEventArgs e)
        {
            ExitNicknameEdit();
        }

        /// <summary>保存昵称</summary>
        private void SaveNickname_Click(object sender, RoutedEventArgs e)
        {
            var s = _app.AppSettings;
            var name = (nicknameBox.Text ?? "").Trim();
            s.AccountName = string.IsNullOrEmpty(name) ? null : name;
            s.Save();
            ExitNicknameEdit();
            RefreshAccountUI();
            RefreshCorpSelector();
            // 同步昵称到后端（后端随后 WS 通知全部好友）；失败不阻断本地保存
            if (!string.IsNullOrEmpty(s.AccountToken) && s.AccountName != null)
                _ = SyncProfileToBackendAsync(s.AccountName, null);
        }

        /// <summary>同步昵称/头像 URL 到后端（PUT /friend/profile，发后不管）</summary>
        private async Task SyncProfileToBackendAsync(string? nickName, string? avatarUrl)
        {
            try
            {
                var (ok, msg) = await Services.FriendService.Instance.UpdateMyProfileAsync(nickName, avatarUrl);
                if (!ok) System.Diagnostics.Debug.WriteLine($"[Settings] 资料同步后端失败: {msg}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 资料同步后端异常: {ex.Message}");
            }
        }

        /// <summary>头像 base64 → OSS 缓存区直传 → URL 同步后端（发后不管）</summary>
        private async Task SyncAvatarToBackendAsync(string? avatarBase64)
        {
            try
            {
                if (string.IsNullOrEmpty(_app.AppSettings.AccountToken)) return;
                if (string.IsNullOrEmpty(avatarBase64)) return;
                byte[] bytes = Convert.FromBase64String(avatarBase64);
                string? url = await Services.FriendService.Instance.UploadAvatarToCacheAsync(bytes);
                if (string.IsNullOrEmpty(url))
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] 头像上传 OSS 缓存失败，本次不同步后端");
                    return;
                }
                await SyncProfileToBackendAsync(null, url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 头像同步后端异常: {ex.Message}");
            }
        }

        /// <summary>把图片文件缩放为不超过 256px 的 JPEG 并转为 base64 字符串</summary>
        private static string ImageFileToAvatarBase64(string path)
        {
            var src = new System.Windows.Media.Imaging.BitmapImage();
            src.BeginInit();
            src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(path);
            src.EndInit();
            src.Freeze();

            const double maxSize = 256.0;
            double scale = Math.Min(maxSize / src.PixelWidth, maxSize / src.PixelHeight);
            if (scale > 1.0) scale = 1.0;
            double w = src.PixelWidth * scale;
            double h = src.PixelHeight * scale;

            var resized = new System.Windows.Media.Imaging.TransformedBitmap(
                src,
                new System.Windows.Media.ScaleTransform(w / src.PixelWidth, h / src.PixelHeight));

            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(resized));
            using (var ms = new System.IO.MemoryStream())
            {
                encoder.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
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
            try { Services.BackendWebSocketService.Instance.Start(null); } catch { } // 降级为访客连接，保持可接收设备维度通知
            _ = Services.McpToolRegistryService.Instance.SyncAsync(); // Token 失效：注册表回退内置工具

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
                backupStatusText.Text = Framework.Lang.TF("最近备份：{0:yyyy-MM-dd HH:mm}", t);
                restoreBtn.IsEnabled = true;
            }
            else
            {
                backupStatusText.Text = Framework.Lang.T("尚无本地备份");
                restoreBtn.IsEnabled = false;
            }
        }

        /// <summary>创建本地备份（覆盖最后一个）</summary>
        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (Services.CloudSyncService.CreateLocalBackup())
                MessageBox.Show(Framework.Lang.T("已创建本地备份。"), Framework.Lang.T("备份"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(Framework.Lang.T("备份失败，请重试。"), Framework.Lang.T("备份"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshBackupUI();
        }

        /// <summary>从最后一次本地备份恢复</summary>
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (!Services.CloudSyncService.HasLocalBackup())
            {
                MessageBox.Show(Framework.Lang.T("暂无可恢复的本地备份。"), Framework.Lang.T("恢复"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = MessageBox.Show(Framework.Lang.T("恢复将用最近一次备份覆盖当前全部配置，需要重启应用后生效。确定继续吗？"),
                Framework.Lang.T("恢复备份"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            int n = Services.CloudSyncService.RestoreLocalBackup();
            if (n >= 0)
                MessageBox.Show(Framework.Lang.TF("已恢复 {0} 个配置文件，重启应用后生效。", n), Framework.Lang.T("恢复"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(Framework.Lang.T("恢复失败，请重试。"), Framework.Lang.T("恢复"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>导出全部配置为 JSON 文件</summary>
        private void ExportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = Framework.Lang.T("导出配置"),
                Filter = Framework.Lang.T("JSON 文件 (*.json)|*.json"),
                FileName = $"SideDesk-config-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, Services.CloudSyncService.BuildConfigBundleJson(indented: true));
                MessageBox.Show(Framework.Lang.T("配置已导出。"), Framework.Lang.T("导出"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.T("导出失败：") + ex.Message, Framework.Lang.T("导出"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>登录 / 退出登录</summary>
        private void AccountAction_Click(object sender, RoutedEventArgs e)
        {
            var s = _app.AppSettings;
            if (!string.IsNullOrEmpty(s.AccountToken))
            {
                // 退出登录
                var r = MessageBox.Show(Framework.Lang.T("确定要退出登录吗？退出后云同步将停止。"), Framework.Lang.T("退出登录"),
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
                try { Services.BackendWebSocketService.Instance.Start(null); } catch { } // 登出后降级为访客连接，保持可接收设备维度通知
                _ = Services.McpToolRegistryService.Instance.SyncAsync(); // 登出：注册表回退内置工具（隐私模式）
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
                s.HasLoggedInBefore = true; // 记录会员身份：曾经登录过
                // 登录成功后，若团队选择为空则默认切到"个人"(corpId='')
                if (string.IsNullOrWhiteSpace(s.SelectedCorpId))
                {
                    s.SelectedCorpId = null;
                    s.SelectedCorpName = null;
                }
                s.Save();
                App.AccountTokenExpiredMessage = null; // 清除过期提示
                App.MarkAccountOffline(false); // 清除离线标记（内部亦会关闭掉线通知）
                _app.DismissLoggedOutNotification(); // 关闭主窗口上的掉线登录提示

                // 登录成功 → 启动后端 WebSocket 长连接
                try { Services.BackendWebSocketService.Instance.Start(s.AccountToken!); } catch { }
                _ = Services.McpToolRegistryService.Instance.SyncAsync(); // 登录：立即拉取服务端 MCP 工具注册表

                RefreshAccountUI();
                RefreshCorpSelector();

                // 拉取用户详情（昵称、头像）并刷新个人资料区
                _ = _app.FetchAndSaveUserInfoAsync(s.AccountToken!).ContinueWith(t =>
                {
                    Dispatcher.Invoke(() => UpdateAccountProfileUI());
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
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
                MessageBox.Show(Framework.Lang.T("请先登录后再开启云同步。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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

        /// <summary>加密同步开关切换</summary>
        private void EncryptSyncSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var s = _app.AppSettings;
            bool on = encryptSyncSwitch.IsChecked == true;

            if (on)
            {
                // 首次开启需要输入密码派生密钥
                if (string.IsNullOrEmpty(s.SyncDerivedKey))
                {
                    if (s.AccountUserId == null || s.AccountUserId == 0)
                    {
                        ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("无法获取用户 ID，请重新登录后再试")), "提示");
                        _isLoading = true;
                        encryptSyncSwitch.IsChecked = false;
                        _isLoading = false;
                        return;
                    }

                    var dlg = new SyncPasswordDialog { Owner = this };
                    if (dlg.ShowDialog() != true)
                    {
                        _isLoading = true;
                        encryptSyncSwitch.IsChecked = false;
                        _isLoading = false;
                        return;
                    }

                    // 派生密钥并用 DPAPI 保护后存本地
                    byte[] key = Services.SyncEncryption.DeriveKey(dlg.Password, s.AccountUserId.Value);
                    s.SyncDerivedKey = Services.SyncEncryption.ProtectKeyForLocal(key);
                }

                s.SyncEncryptEnabled = true;
            }
            else
            {
                s.SyncEncryptEnabled = false;
            }

            s.Save();
        }

        /// <summary>从云端恢复设置</summary>
        private async void RestoreSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!ThemedMessageBox.Confirm(this, Framework.Lang.T("将从服务器拉取已同步的设置覆盖本地当前配置，是否继续？"), "恢复设置"))
                return;

            restoreSettingsBtn.IsEnabled = false;
            int count = await Services.CloudSyncService.Instance.RestoreSettingsAsync();
            restoreSettingsBtn.IsEnabled = true;

            if (count > 0)
                ThemedMessageBox.Show(this, Framework.Lang.TF("已恢复 {0} 项设置", count), "完成");
            else if (count == 0)
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("服务器上没有已同步的设置项")), "提示");
            else
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("恢复失败，请检查网络连接或登录状态")), "失败");
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
                    Text = Framework.Lang.T("（暂无云空间，可点击“恢复内置”或手动添加）"),
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
                        Text = Framework.Lang.T("内置"), FontSize = 10,
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
                    Content = Framework.Lang.T("打开"), Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
                };
                if (space.IsBuiltin)
                {
                    // 内置云空间：打开对应文件管理窗口，不可编辑/删除
                    if (space.Name == "百度网盘")
                        openBtn.Click += (s, e) => { new BaiduPanWindow().Show(); };
                    else
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
                        Content = Framework.Lang.T("编辑"), Style = (Style)FindResource("GhostButtonStyle"),
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
                        if (MessageBox.Show(Framework.Lang.TF("确定删除云空间“{0}”？", delName), Framework.Lang.T("删除确认"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
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
            // 提供预设选项：百度网盘 / 自定义
            var menu = new System.Windows.Controls.ContextMenu();
            var baiduItem = new System.Windows.Controls.MenuItem { Header = Framework.Lang.T("🅱️ 百度网盘") };
            baiduItem.Click += (_, _) =>
            {
                // 打开百度网盘窗口进行 OAuth 绑定，授权成功后自动添加到云空间列表
                new BaiduPanWindow().Show();
            };
            var customItem = new System.Windows.Controls.MenuItem { Header = Framework.Lang.T("➕ 自定义云空间") };
            customItem.Click += (_, _) => ShowCloudSpaceEditDialog(null);
            menu.Items.Add(baiduItem);
            menu.Items.Add(customItem);
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
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
                Title = isNew ? Framework.Lang.T("添加云空间") : Framework.Lang.T("编辑云空间"),
                Width = 380, Height = 310,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushBG")
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock { Text = Framework.Lang.T("图标（emoji）"), Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var iconBox = new TextBox { Text = editing.Icon, Width = 60, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(iconBox);

            root.Children.Add(new TextBlock { Text = Framework.Lang.T("名称"), Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var nameBox = new TextBox { Text = editing.Name, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(nameBox);

            root.Children.Add(new TextBlock { Text = Framework.Lang.T("网址"), Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var urlBox = new TextBox { Text = editing.Url, Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(urlBox);

            root.Children.Add(new TextBlock { Text = Framework.Lang.T("备注（可选）"), Foreground = (Brush)FindResource("BrushTextMain"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var noteBox = new TextBox { Text = editing.Note ?? "", Style = (Style)FindResource("GlobalTextBoxStyle") };
            root.Children.Add(noteBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
            okBtn.Click += (s, e) =>
            {
                string name = nameBox.Text.Trim();
                string url = urlBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) { MessageBox.Show(Framework.Lang.T("请输入名称")); return; }
                if (string.IsNullOrEmpty(url)) { MessageBox.Show(Framework.Lang.T("请输入网址")); return; }
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
                corpSelectorCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("未登录"), IsEnabled = false });
                corpSelectorCombo.SelectedIndex = 0;
                corpSelectorCombo.IsEnabled = false;
                _corpLoading = false;
                return;
            }

            corpSelectorCombo.IsEnabled = true;

            // 个人选项（有账户名/手机号时显示账户名，否则显示"个人"）
            string personalLabel = string.IsNullOrEmpty(s.AccountName) ? Framework.Lang.T("个人") : s.AccountName;
            corpSelectorCombo.Items.Add(new ComboBoxItem { Content = personalLabel, Tag = null });

            // 异步加载团队列表
            var teams = await TeamService.Instance.ListTeamsAsync();
            if (corpSelectorCombo == null) return; // 窗口可能已关闭

            // 检测 Token 失效：清除登录状态并重置下拉框
            if (teams == null && TeamService.Instance.LastAuthFailed)
            {
                InvalidateLocalToken(Framework.Lang.T("登录已过期，请重新登录"));
                return; // InvalidateLocalToken 已调用 RefreshCorpSelector 重新填充
            }

            if (teams != null)
            {
                // 分组表头：「团队」标题 + 横向分割线，与上方个人账户区分（禁用项，不可选）
                if (teams.Count > 0)
                {
                    var headerPanel = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var headerText = new System.Windows.Controls.TextBlock
                    {
                        Text = Framework.Lang.T("团队"),
                        FontSize = 12,
                        Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    System.Windows.Controls.DockPanel.SetDock(headerText, System.Windows.Controls.Dock.Left);
                    headerPanel.Children.Add(headerText);
                    headerPanel.Children.Add(new System.Windows.Shapes.Rectangle
                    {
                        Height = 1,
                        Fill = (System.Windows.Media.Brush)FindResource("BrushBorder"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    });
                    corpSelectorCombo.Items.Add(new ComboBoxItem { Content = headerPanel, IsEnabled = false, Tag = "__group_header__" });
                }

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
                teamListPanel.Children.Add(BuildTeamHintText(Framework.Lang.T("请先登录后再查看团队")));
                return;
            }

            teamListPanel.Children.Add(BuildTeamHintText(Framework.Lang.T("正在加载...")));
            var teams = await TeamService.Instance.ListTeamsAsync();

            teamListPanel.Children.Clear();
            if (teams == null)
            {
                // 检测是否因 Token 过期/无效导致失败
                if (TeamService.Instance.LastAuthFailed)
                {
                    InvalidateLocalToken(Framework.Lang.T("登录已过期，请重新登录"));
                    teamListPanel.Children.Add(BuildTeamHintText(Framework.Lang.T("登录已过期，请重新登录后再查看团队")));
                }
                else
                {
                    teamListPanel.Children.Add(BuildTeamHintText(Framework.Lang.T("加载失败，请重试")));
                }
                return;
            }
            if (teams.Count == 0)
            {
                teamListPanel.Children.Add(BuildTeamHintText(Framework.Lang.T("（暂无团队，可点击右上角添加）")));
            }
            else
            {
                foreach (var team in teams)
                    teamListPanel.Children.Add(BuildTeamRow(team));
            }

            // 同步刷新待处理加入申请（管理员）与收到的团队邀请
            _ = RefreshTeamApplicationsAsync();
            _ = RefreshTeamInvitationsAsync();
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
                Text = Framework.Lang.TF("{0} 人", members.Count),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });

            // 团队码（点击复制，供成员分享）
            if (!string.IsNullOrEmpty(team.InviteCode))
            {
                string code = team.InviteCode;
                var codeText = new TextBlock
                {
                    Text = Framework.Lang.T("团队码") + ": " + code,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = Framework.Lang.T("点击复制")
                };
                codeText.MouseLeftButtonUp += (s, e) =>
                {
                    try
                    {
                        Clipboard.SetText(code);
                        codeText.Text = Framework.Lang.T("已复制");
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                        timer.Tick += (st, at) => { timer.Stop(); codeText.Text = Framework.Lang.T("团队码") + ": " + code; };
                        timer.Start();
                    }
                    catch { /* 剪贴板占用失败忽略 */ }
                };
                headerRow.Children.Add(codeText);
            }
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

            // 「+」添加成员按钮（仅管理员可邀请）
            if (team.IsAdmin)
            {
                var addBtn = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(shown > 0 || members.Count > maxAvatars ? -8 : 0, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = Framework.Lang.T("添加成员"),
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
            }

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

        /// <summary>添加成员按钮点击：输入用户名邀请加入团队（后端 WS 通知对方）</summary>
        private async void AddMember_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Team team) return;

            var dlg = new RenameDialog(Framework.Lang.TF("邀请成员加入「{0}」", team.Name), "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string userName = dlg.NewName.Trim();
            if (string.IsNullOrWhiteSpace(userName)) return;

            var (ok, msg) = await TeamService.Instance.InviteMemberAsync(team.Id, userName);
            if (ok)
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("邀请已发送，等待对方接受") : msg, Framework.Lang.T("提示"));
            else
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
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
            var dlg = new RenameDialog(Framework.Lang.T("新建团队"), "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string name = dlg.NewName;
            if (string.IsNullOrWhiteSpace(name)) return;

            var team = await TeamService.Instance.CreateTeamAsync(name);
            if (team == null)
            {
                MessageBox.Show(Framework.Lang.T("创建团队失败，请重试"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await RefreshTeamListAsync();
            RefreshCorpSelector();
        }

        /// <summary>加入团队：弹出输入框输入团队码，提交申请（后端 WS 通知管理员审批）</summary>
        private async void JoinTeam_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RenameDialog(Framework.Lang.T("输入团队码"), "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string code = dlg.NewName.Trim();
            if (string.IsNullOrWhiteSpace(code)) return;

            var (ok, msg) = await TeamService.Instance.JoinTeamAsync(code);
            if (ok)
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("申请已发送，等待管理员审批") : msg, Framework.Lang.T("提示"));
            else
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
        }

        /// <summary>刷新待处理加入申请（管理员视角；无申请时隐藏区块）</summary>
        private async Task RefreshTeamApplicationsAsync()
        {
            if (teamApplyPanel == null) return;
            teamApplyPanel.Children.Clear();

            var list = await TeamService.Instance.GetApplicationsAsync();
            if (list == null || list.Count == 0)
            {
                teamApplySection.Visibility = Visibility.Collapsed;
                return;
            }

            teamApplySection.Visibility = Visibility.Visible;
            foreach (var a in list)
                teamApplyPanel.Children.Add(BuildTeamApplicationRow(a));
        }

        /// <summary>构建加入申请行：申请人头像 + 名称 + 同意/拒绝按钮</summary>
        private UIElement BuildTeamApplicationRow(TeamApplication a)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(BuildAvatarCircle(a.ApplicantName, a.ApplicantAvatar, 30));

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(btnPanel, Dock.Right);

            var acceptBtn = new Button
            {
                Content = Framework.Lang.T("同意"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            acceptBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await TeamService.Instance.HandleApplicationAsync(a.Id, "accept");
                if (ok)
                    await RefreshTeamListAsync(); // 后端会 WS 通知申请人；本地刷新成员列表与剩余申请
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
            };
            btnPanel.Children.Add(acceptBtn);

            var rejectBtn = new Button
            {
                Content = Framework.Lang.T("拒绝"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.7
            };
            rejectBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await TeamService.Instance.HandleApplicationAsync(a.Id, "reject");
                if (ok)
                    await RefreshTeamApplicationsAsync();
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
            };
            btnPanel.Children.Add(rejectBtn);
            row.Children.Add(btnPanel);

            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = a.ApplicantName,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            });
            info.Children.Add(new TextBlock
            {
                Text = Framework.Lang.TF("申请加入团队「{0}」", a.TeamName),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
            row.Children.Add(info);

            return row;
        }

        /// <summary>刷新我收到的待处理团队邀请（无邀请时隐藏区块）</summary>
        private async Task RefreshTeamInvitationsAsync()
        {
            if (teamInvitePanel == null) return;
            teamInvitePanel.Children.Clear();

            var list = await TeamService.Instance.GetInvitationsAsync();
            if (list == null || list.Count == 0)
            {
                teamInviteSection.Visibility = Visibility.Collapsed;
                return;
            }

            teamInviteSection.Visibility = Visibility.Visible;
            foreach (var inv in list)
                teamInvitePanel.Children.Add(BuildTeamInvitationRow(inv));
        }

        /// <summary>构建团队邀请行：团队名 + 邀请人 + 接受/拒绝按钮</summary>
        private UIElement BuildTeamInvitationRow(TeamInvitation inv)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(BuildAvatarCircle(inv.TeamName, null, 30));

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(btnPanel, Dock.Right);

            var acceptBtn = new Button
            {
                Content = Framework.Lang.T("接受"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            acceptBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await TeamService.Instance.HandleInvitationAsync(inv.Id, "accept");
                if (ok)
                {
                    await RefreshTeamListAsync();
                    RefreshCorpSelector(); // 入团后团队切换下拉需补齐
                }
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
            };
            btnPanel.Children.Add(acceptBtn);

            var rejectBtn = new Button
            {
                Content = Framework.Lang.T("拒绝"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.7
            };
            rejectBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await TeamService.Instance.HandleInvitationAsync(inv.Id, "reject");
                if (ok)
                    await RefreshTeamInvitationsAsync();
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, Framework.Lang.T("提示"));
            };
            btnPanel.Children.Add(rejectBtn);
            row.Children.Add(btnPanel);

            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = inv.TeamName,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            });
            info.Children.Add(new TextBlock
            {
                Text = Framework.Lang.TF("{0} 邀请你加入团队", inv.InviterName),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
            row.Children.Add(info);

            return row;
        }

        #endregion

        #region 好友管理

        /// <summary>刷新好友页面（好友列表 + 请求 + 好友码）</summary>
        private async Task RefreshFriendsPageAsync()
        {
            bool loggedIn = !string.IsNullOrEmpty(_app.AppSettings.AccountToken);
            ApplyFriendLoginState(loggedIn);
            if (!loggedIn) return; // 未登录不请求接口

            _ = RefreshFriendListAsync();
            _ = RefreshFriendRequestsAsync();
            _ = RefreshMyFriendCodeAsync();
            await Task.CompletedTask;
        }

        /// <summary>根据登录状态启用/禁用好友面板控件并更新提示</summary>
        private void ApplyFriendLoginState(bool loggedIn)
        {
            friendSearchBox.IsEnabled = loggedIn;
            friendSearchBtn.IsEnabled = loggedIn;
            refreshFriendCodeBtn.IsEnabled = loggedIn;
            copyFriendCodeBtn.IsEnabled = loggedIn;
            allowFriendSearchSwitch.IsEnabled = loggedIn;

            if (!loggedIn)
            {
                friendPageSubtitle.Text = Framework.Lang.T("请先登录后使用好友功能（搜索、添加、好友码等）");
                myFriendCodeText.Text = Framework.Lang.T("请先登录");
                friendListPanel.Children.Clear();
                friendRequestPanel.Children.Clear();
                friendRequestSection.Visibility = Visibility.Collapsed;
                friendSearchResultPanel.Children.Clear();
            }
            else
            {
                friendPageSubtitle.Text = Framework.Lang.T("通过用户名、邮箱、手机号或好友码搜索添加好友");
            }
        }

        /// <summary>刷新好友列表</summary>
        private async Task RefreshFriendListAsync()
        {
            var friends = await Services.FriendService.Instance.GetFriendListAsync();
            friendListPanel.Children.Clear();

            if (friends == null || friends.Count == 0)
            {
                friendListPanel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.T("还没有好友，通过上方搜索添加"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 4)
                });
                return;
            }

            foreach (var f in friends)
            {
                friendListPanel.Children.Add(BuildFriendRow(f));
            }
        }

        /// <summary>构建好友列表行：头像 + 昵称 + 在线状态点，右键删除</summary>
        private UIElement BuildFriendRow(Services.FriendUser f)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            // 头像
            row.Children.Add(BuildAvatarCircle(f.NickName, f.Avatar, 30));

            // 在线状态点
            bool online = f.IsOnline == 1;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = online ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)) : new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = online ? Framework.Lang.T("在线") : Framework.Lang.T("离线")
            };
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(dot);

            // 昵称 + 用户名
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = f.NickName,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            });
            info.Children.Add(new TextBlock
            {
                Text = $"@{f.UserName}",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
            row.Children.Add(info);

            // 右键删除好友
            var menu = new ContextMenu();
            var removeMi = new MenuItem { Header = Framework.Lang.T("删除好友") };
            removeMi.Click += async (s, e) =>
            {
                if (!ThemedMessageBox.Confirm(this, Framework.Lang.TF("确定删除好友「{0}」吗？", f.NickName), "确认")) return;
                var (ok, msg) = await Services.FriendService.Instance.RemoveFriendAsync(f.UserId);
                if (ok)
                    await RefreshFriendListAsync();
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("删除失败") : msg, "提示");
            };
            menu.Items.Add(removeMi);
            row.ContextMenu = menu;

            return row;
        }

        /// <summary>刷新好友请求列表</summary>
        private async Task RefreshFriendRequestsAsync()
        {
            var requests = await Services.FriendService.Instance.GetRequestsAsync();
            friendRequestPanel.Children.Clear();

            if (requests == null || requests.Count == 0)
            {
                friendRequestSection.Visibility = Visibility.Collapsed;
                return;
            }

            friendRequestSection.Visibility = Visibility.Visible;
            foreach (var r in requests)
            {
                if (r.FromUser == null) continue;
                friendRequestPanel.Children.Add(BuildRequestRow(r));
            }
        }

        /// <summary>构建好友请求行：头像 + 昵称 + 同意/拒绝按钮</summary>
        private UIElement BuildRequestRow(Services.FriendRequest r)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var user = r.FromUser!;

            row.Children.Add(BuildAvatarCircle(user.NickName, user.Avatar, 30));

            // 同意/拒绝按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(btnPanel, Dock.Right);

            var acceptBtn = new Button
            {
                Content = Framework.Lang.T("同意"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            acceptBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await Services.FriendService.Instance.HandleRequestAsync(r.Id, "accept");
                if (ok)
                {
                    await RefreshFriendRequestsAsync();
                    await RefreshFriendListAsync();
                }
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, "提示");
            };
            btnPanel.Children.Add(acceptBtn);

            var rejectBtn = new Button
            {
                Content = Framework.Lang.T("拒绝"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.7
            };
            rejectBtn.Click += async (s, e) =>
            {
                var (ok, msg) = await Services.FriendService.Instance.HandleRequestAsync(r.Id, "reject");
                if (ok)
                    await RefreshFriendRequestsAsync();
                else
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("操作失败") : msg, "提示");
            };
            btnPanel.Children.Add(rejectBtn);
            row.Children.Add(btnPanel);

            // 昵称 + 用户名
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = user.NickName,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            });
            info.Children.Add(new TextBlock
            {
                Text = Framework.Lang.TF("@{0} 请求添加你为好友", user.UserName),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
            row.Children.Add(info);

            return row;
        }

        /// <summary>刷新我的好友码</summary>
        private async Task RefreshMyFriendCodeAsync()
        {
            var (code, allowSearch) = await Services.FriendService.Instance.GetMyCodeAsync();

            // 初始化开关状态（避免触发 Changed 事件）
            allowFriendSearchSwitch.Checked -= AllowFriendSearch_Changed;
            allowFriendSearchSwitch.Unchecked -= AllowFriendSearch_Changed;
            allowFriendSearchSwitch.IsChecked = allowSearch;
            allowFriendSearchSwitch.Checked += AllowFriendSearch_Changed;
            allowFriendSearchSwitch.Unchecked += AllowFriendSearch_Changed;

            // 关闭被搜索时不显示好友码
            if (!allowSearch)
            {
                myFriendCodeText.Text = Framework.Lang.T("已关闭被搜索");
                return;
            }
            myFriendCodeText.Text = string.IsNullOrEmpty(code) ? Framework.Lang.T("获取失败") : code;
        }

        /// <summary>切换是否允许被搜索添加好友</summary>
        private async void AllowFriendSearch_Changed(object sender, RoutedEventArgs e)
        {
            var allow = allowFriendSearchSwitch.IsChecked ?? false;

            // 关闭时二次确认（会清空好友码）
            if (!allow)
            {
                if (!ThemedMessageBox.Confirm(this, Framework.Lang.T("关闭后别人将无法搜索到你，同时会清空你的好友码。\n确定关闭吗？"), "关闭被搜索"))
                {
                    // 恢复勾选（临时移除事件避免递归）
                    allowFriendSearchSwitch.Checked -= AllowFriendSearch_Changed;
                    allowFriendSearchSwitch.IsChecked = true;
                    allowFriendSearchSwitch.Checked += AllowFriendSearch_Changed;
                    return;
                }
            }

            allowFriendSearchSwitch.IsEnabled = false;
            try
            {
                var (ok, msg) = await Services.FriendService.Instance.SetSearchableAsync(allow);
                if (!ok)
                {
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("设置失败") : msg, "提示");
                    // 恢复原状态
                    allowFriendSearchSwitch.Checked -= AllowFriendSearch_Changed;
                    allowFriendSearchSwitch.Unchecked -= AllowFriendSearch_Changed;
                    allowFriendSearchSwitch.IsChecked = !allow;
                    allowFriendSearchSwitch.Checked += AllowFriendSearch_Changed;
                    allowFriendSearchSwitch.Unchecked += AllowFriendSearch_Changed;
                    return;
                }

                // 刷新好友码显示
                await RefreshMyFriendCodeAsync();
            }
            finally
            {
                allowFriendSearchSwitch.IsEnabled = true;
            }
        }

        /// <summary>右上角「添加好友」：展开/收起搜索区，展开后聚焦输入框</summary>
        private void ToggleFriendSearch_Click(object sender, RoutedEventArgs e)
        {
            var show = friendSearchSection.Visibility != Visibility.Visible;
            friendSearchSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) friendSearchBox.Focus();
        }

        /// <summary>搜索好友</summary>
        private async void FriendSearch_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_app.AppSettings.AccountToken))
            {
                ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("请先登录后再搜索好友。")), "提示");
                return;
            }

            var keyword = friendSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(keyword)) return;

            friendSearchBtn.IsEnabled = false;
            friendSearchResultPanel.Children.Clear();

            try
            {
                var users = await Services.FriendService.Instance.SearchUserAsync(keyword);

                if (users == null || users.Count == 0)
                {
                    friendSearchResultPanel.Children.Add(new TextBlock
                    {
                        Text = Framework.Lang.T("未找到相关用户"),
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontSize = 12,
                        Margin = new Thickness(0, 4, 0, 4)
                    });
                    return;
                }

                foreach (var u in users)
                {
                    friendSearchResultPanel.Children.Add(BuildSearchResultRow(u));
                }
            }
            finally
            {
                friendSearchBtn.IsEnabled = true;
            }
        }

        /// <summary>构建搜索结果行：头像 + 昵称 + 在线状态 + 添加按钮</summary>
        private UIElement BuildSearchResultRow(Services.FriendUser u)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            row.Children.Add(BuildAvatarCircle(u.NickName, u.Avatar, 30));

            // 添加按钮
            var addBtn = new Button
            {
                Content = Framework.Lang.T("+ 添加"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(addBtn, Dock.Right);
            addBtn.Click += async (s, e) =>
            {
                addBtn.IsEnabled = false;
                var (ok, msg) = await Services.FriendService.Instance.AddFriendAsync(u.UserId);
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? (ok ? Framework.Lang.T("好友请求已发送") : Framework.Lang.T("发送失败")) : msg, "提示");
                addBtn.IsEnabled = true;
                // 发送成功后立即刷新请求列表（自己加自己时可直接看到并处理）
                if (ok) await RefreshFriendRequestsAsync();
            };
            row.Children.Add(addBtn);

            // 在线状态点
            bool online = u.IsOnline == 1;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = online ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)) : new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(dot);

            // 昵称 + 用户名
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = u.NickName,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            });
            info.Children.Add(new TextBlock
            {
                Text = $"@{u.UserName}",
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
            row.Children.Add(info);

            return row;
        }

        /// <summary>搜索框回车触发搜索</summary>
        private void FriendSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                FriendSearch_Click(sender, e);
        }

        /// <summary>复制好友码</summary>
        private void CopyFriendCode_Click(object sender, RoutedEventArgs e)
        {
            var code = myFriendCodeText.Text;
            if (string.IsNullOrEmpty(code) || code == "获取失败" || code == "加载中...") return;
            try
            {
                Clipboard.SetText(code);
                copyFriendCodeBtn.Content = Framework.Lang.T("已复制");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, args) => { timer.Stop(); copyFriendCodeBtn.Content = Framework.Lang.T("复制"); };
                timer.Start();
            }
            catch { }
        }

        /// <summary>更换好友码（旧码失效）</summary>
        private async void RefreshFriendCode_Click(object sender, RoutedEventArgs e)
        {
            if (!ThemedMessageBox.Confirm(this, Framework.Lang.T("更换后旧好友码将立即失效，别人无法再通过旧码搜索到你。\n确定更换吗？"), "更换好友码")) return;

            refreshFriendCodeBtn.IsEnabled = false;
            try
            {
                var newCode = await Services.FriendService.Instance.RefreshCodeAsync();
                if (!string.IsNullOrEmpty(newCode))
                    myFriendCodeText.Text = newCode;
                else
                    ThemedMessageBox.Show(this, Framework.Lang.T(Framework.Lang.T("更换失败，请重试")), Framework.Lang.T("提示"));
            }
            finally
            {
                refreshFriendCodeBtn.IsEnabled = true;
            }
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

        /// <summary>配色主题选择：默认 = 纯 XAML 深浅主题；其余 = resources/themes 下的 JSON 自定义主题</summary>
        private void CustomTheme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            if (customThemeCombo.SelectedIndex <= 0)
            {
                // 默认：清除 JSON 覆盖，还原 XAML 主题
                Services.ThemeManager.ClearTheme();
                Framework.AppSettings.Instance.CurrentTheme = "";
            }
            else
            {
                var name = customThemeCombo.SelectedItem?.ToString();
                var theme = Services.ThemeManager.GetThemeByName(name);
                if (theme == null) return;
                Services.ThemeManager.ApplyTheme(theme);
                Framework.AppSettings.Instance.CurrentTheme = theme.Name;
            }

            Framework.AppSettings.Instance.Save();
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

        private void ShowNotificationStripSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.ShowNotificationStrip = showNotificationStripSwitch.IsChecked ?? false;
            _app.AppSettings.Save();
            // 即时应用所有主窗口的通知条显隐
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                    mw.ApplyNotificationStrip();
            }
        }

        // ===== 分享/团队组件边框颜色 =====

        private static readonly string[] BorderColorPresets =
            { "#A855F7", "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#06B6D4", "#8E8E8E" };

        /// <summary>构建预设色板（点击即应用）</summary>
        private void BuildBorderSwatches(WrapPanel panel, TextBox hexBox, bool isShare)
        {
            panel.Children.Clear();
            foreach (var c in BorderColorPresets)
            {
                var swatch = new Border
                {
                    Width = 18, Height = 18, Margin = new Thickness(0, 0, 5, 0),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand, Tag = c, ToolTip = c,
                    BorderThickness = new Thickness(2)
                };
                try { swatch.Background = (SolidColorBrush)new BrushConverter().ConvertFrom(c)!; }
                catch { swatch.Background = Brushes.Gray; }
                swatch.MouseLeftButtonUp += (s, e) =>
                {
                    hexBox.Text = c;
                    ApplyBorderColor(isShare, c);
                };
                panel.Children.Add(swatch);
            }
            // 初始高亮当前选中的色块
            UpdateBorderSwatchHighlight(panel, hexBox.Text);
        }

        /// <summary>刷新色板选中态：与当前颜色匹配的色块加高亮框</summary>
        private void UpdateBorderSwatchHighlight(WrapPanel panel, string? currentColor)
        {
            var accent = TryFindResource("BrushAccent") as Brush ?? Brushes.White;
            foreach (var child in panel.Children)
            {
                if (child is Border swatch)
                {
                    bool selected = string.Equals(swatch.Tag as string, currentColor?.Trim(), StringComparison.OrdinalIgnoreCase);
                    swatch.BorderBrush = selected ? accent : Brushes.Transparent;
                }
            }
        }

        /// <summary>应用边框颜色：校验合法性后保存并重新渲染所有主窗口面板</summary>
        private void ApplyBorderColor(bool isShare, string color)
        {
            if (_isLoading) return;
            var box = isShare ? shareBorderHexBox : teamBorderHexBox;
            var appSettings = Framework.AppSettings.Instance;
            string fallback = isShare ? appSettings.ShareBorderColor : appSettings.TeamBorderColor;
            try
            {
                new BrushConverter().ConvertFrom(color.Trim());
            }
            catch
            {
                box.Text = fallback; // 非法色值回退为当前值
                return;
            }
            if (isShare) appSettings.ShareBorderColor = color.Trim();
            else appSettings.TeamBorderColor = color.Trim();
            appSettings.Save();
            // 刷新对应色板的选中高亮
            UpdateBorderSwatchHighlight(isShare ? shareBorderSwatches : teamBorderSwatches, color.Trim());
            // 即时重新渲染所有主窗口的原生面板，让新边框色生效
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                    mw.RefreshNativePanels();
            }
        }

        private void BorderHex_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)sender;
            ApplyBorderColor(Equals(box.Tag, "team") ? false : true, box.Text);
        }

        private void BorderHex_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            var box = (TextBox)sender;
            ApplyBorderColor(Equals(box.Tag, "team") ? false : true, box.Text);
        }

        // ===== 虚拟桌面配置 =====

        private void PerVirtualDesktopSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool on = perVirtualDesktopSwitch.IsChecked == true;

            if (on && (_app.VirtualDesktop == null || !_app.VirtualDesktop.IsAvailable))
            {
                MessageBox.Show(Framework.Lang.T("当前系统不支持虚拟桌面，或仅有 1 个桌面。"), Framework.Lang.T("提示"),
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
                string name = !string.IsNullOrEmpty(profile.DesktopName) ? profile.DesktopName : Framework.Lang.TF("桌面 {0}", i + 1);
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
                string primary = mon.IsPrimary ? Framework.Lang.T(" · 主") : "";
                string res = mon.PixelWidth > 0 ? $" · {mon.PixelWidth}×{mon.PixelHeight}" : "";
                outer.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.TF("显示器 {0}{1}{2}", monitorIndex + 1, primary, res),
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
            dockCombo.Items.Add(Framework.Lang.T("无停靠"));
            dockCombo.Items.Add(Framework.Lang.T("左侧"));
            dockCombo.Items.Add(Framework.Lang.T("右侧"));
            dockCombo.Items.Add(Framework.Lang.T("两侧"));
            dockCombo.SelectedIndex = cfg.Layout switch
            {
                LayoutMode.Left => 1,
                LayoutMode.Right => 2,
                LayoutMode.Both => 3,
                _ => 0
            };
            outer.Children.Add(BuildInfoRow(Framework.Lang.T("停靠位置"), "面板停靠在该显示器的哪一侧", dockCombo, new Thickness(0, 0, 0, 16)));

            // 左右侧子区块
            var leftBlock = BuildSideBlock(Framework.Lang.T("左侧面板"), monitorIndex, DockEdge.Left);
            var rightBlock = BuildSideBlock(Framework.Lang.T("右侧面板"), monitorIndex, DockEdge.Right);
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
                if (!int.TryParse(widthBox.Text, out int w) || w < 5 || w > 500)
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
            panel.Children.Add(BuildInputRow(Framework.Lang.T("侧边栏宽度"), "侧边栏停靠宽度（5-500）", widthBox, "px", new Thickness(0, 0, 0, 12)));

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
            var hoverWidthRow = BuildInputRow(Framework.Lang.T("浮动宽度"), "悬停展开宽度（100-500）", hoverWidthBox, "px", new Thickness(0));
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
            panel.Children.Add(BuildInfoRow(Framework.Lang.T("开启浮动"), "开启浮动（鼠标悬停展开）", hoverCheck, new Thickness(0, 0, 0, 12)));
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
                    Text = Framework.Lang.T("暂无已注册程序"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
            }
        }

        private void AddProgram_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = Framework.Lang.T("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
                Title = Framework.Lang.T("选择程序")
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
                        Text = Framework.Lang.T("UserData 配置"),
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
                Filter = Framework.Lang.T("浏览器 (*.exe)|*.exe"),
                Title = Framework.Lang.T("选择浏览器主程序")
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
                Title = Framework.Lang.T("选择 UserData 目录")
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
                Title = Framework.Lang.T("选择默认 Profile 目录")
            };
            if (dlg.ShowDialog() == true)
            {
                _registry.DefaultProfileBaseDir = dlg.FolderName;
                _registry.Save();
                profileBaseDirText.Text = dlg.FolderName;
            }
        }

        #region 插件管理

        /// <summary>右上角插件市场按钮：打开插件市场窗口（MarketWindow 自动拦截 desktxp:// 本地桥协议）</summary>
        private void OpenPluginMarket_Click(object sender, RoutedEventArgs e)
        {
            MarketWindow.Open("/plugin-market", Framework.Lang.T("🧩 插件市场"));
        }

        /// <summary>刷新插件列表按钮</summary>
        private void RefreshPluginList_Click(object sender, RoutedEventArgs e)
        {
            RefreshPluginList();
        }

        /// <summary>导入插件按钮：选择本地 zip 插件包导入安装（解压校验/部署/热加载由桥服务异步完成，结束后自动刷新列表并弹窗通知）</summary>
        private void ImportPlugin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = Framework.Lang.T("插件包 (*.zip)|*.zip"),
                Title = Framework.Lang.T("选择插件包")
            };
            if (dlg.ShowDialog() == true)
            {
                Services.Plugins.PluginMarketBridge.InstallFromLocalZip(dlg.FileName);
            }
        }

        /// <summary>刷新插件列表（扫描插件目录，展示所有插件及启用状态）</summary>
        public void RefreshPluginList()
        {
            pluginListPanel.Children.Clear();

            var configs = Services.Plugins.PluginLoaderService.Instance.ScanAllPluginConfigs();
            var disabled = _app.AppSettings.DisabledPlugins;

            if (configs.Count == 0)
            {
                pluginListPanel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.T("插件目录下没有插件"),
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

                // 右侧删除按钮
                var delBtn = new Button
                {
                    Content = Framework.Lang.T("删除"),
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(8, 3, 8, 3),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = Framework.Lang.T("卸载并删除该插件文件夹")
                };
                var pluginCfg = cfg;
                delBtn.Click += (s, e) => DeletePlugin(pluginCfg);
                DockPanel.SetDock(delBtn, Dock.Right);
                dock.Children.Add(delBtn);

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

            // 即时重新渲染所有主窗口的原生面板（禁用后立即显示「插件未加载」，启用后立即加载 UI）
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                    mw.RefreshNativePanels();
            }
        }

        /// <summary>删除插件：卸载并删除插件文件夹</summary>
        private void DeletePlugin(Services.Plugins.PluginConfig cfg)
        {
            var msg = Framework.Lang.TF("确定删除插件「{0}」吗？\n将卸载并删除其文件夹，此操作不可恢复。", cfg.Name);
            var result = MessageBox.Show(msg, Framework.Lang.T("删除插件"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            var loader = Services.Plugins.PluginLoaderService.Instance;

            // 先卸载已加载的插件实例（释放 DLL 文件锁）
            try { loader.UnloadPlugin(cfg.Id); } catch { }

            // 从禁用列表中移除
            var disabled = _app.AppSettings.DisabledPlugins;
            if (disabled.Contains(cfg.Id))
            {
                disabled.Remove(cfg.Id);
                _app.AppSettings.Save();
            }

            // 删除插件文件夹
            if (!string.IsNullOrEmpty(cfg.SourceDirectory) && System.IO.Directory.Exists(cfg.SourceDirectory))
            {
                try
                {
                    System.IO.Directory.Delete(cfg.SourceDirectory, true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Framework.Lang.TF("删除插件文件夹失败：{0}\n请确认没有文件被占用后重试。", ex.Message), Framework.Lang.T("删除失败"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            RefreshPluginList();

            // 即时重新渲染所有主窗口的原生面板（插件已删除，立即回退为未加载状态）
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw)
                    mw.RefreshNativePanels();
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

        /// <summary>AI 对话模式切换（auto/client/web）</summary>
        private void AIChatModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (aiChatModeCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
            {
                _app.AppSettings.AIChatMode = tag;
                _app.AppSettings.Save();
            }
        }

        /// <summary>界面语言切换（auto/zh-CN/en）</summary>
        private void LanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (languageCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
            {
                if (_app.AppSettings.Language == tag) return;
                _app.AppSettings.Language = tag;
                _app.AppSettings.Save();
                Framework.Lang.Apply(tag);
            }
        }

        /// <summary>刷新 MCP 工具配置状态文本（下载情况 / 版本 / 当前来源）</summary>
        private void RefreshMcpConfigStatus()
        {
            var reg = Services.McpToolRegistryService.Instance;
            var fileTime = Services.McpToolRegistryService.GetLocalPresetFileTime();
            if (fileTime.HasValue)
            {
                // 注册表尚未加载过时（刚启动）直接从文件读版本
                int version = reg.PresetVersion;
                string updatedAt = reg.PresetUpdatedAt;
                if (version == 0)
                {
                    try
                    {
                        var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(
                            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "mcp-tools-preset.json")));
                        version = root.Value<int?>("version") ?? 0;
                        updatedAt = root.Value<string>("updatedAt") ?? "";
                    }
                    catch { }
                }

                string status = Framework.Lang.TF("已下载（v{0}，配置更新于 {1}），本地下载时间 {2:yyyy-MM-dd HH:mm}", version, updatedAt, fileTime);
                if (!string.IsNullOrEmpty(_app.AppSettings.AccountToken))
                    status += "；已登录，当前优先使用账号云端清单";
                else
                    status += "；未登录，当前已生效";
                mcpConfigStatusText.Text = status;
            }
            else
            {
                mcpConfigStatusText.Text = Framework.Lang.T("未下载，使用内置工具；点击下载服务端最新配置（无需登录）");
            }
        }

        /// <summary>从服务端下载 MCP 工具配置更新到本地</summary>
        private async void DownloadMcpConfig_Click(object sender, RoutedEventArgs e)
        {
            downloadMcpConfigBtn.IsEnabled = false;
            downloadMcpConfigBtn.Content = Framework.Lang.T("下载中...");
            try
            {
                var (ok, msg) = await Services.McpToolRegistryService.Instance.DownloadPresetAsync();
                MessageBox.Show(msg, Framework.Lang.T("MCP 工具配置"), MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                RefreshMcpConfigStatus();
            }
            finally
            {
                downloadMcpConfigBtn.IsEnabled = true;
                downloadMcpConfigBtn.Content = Framework.Lang.T("下载更新");
            }
        }

        private void HttpServerSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _app.AppSettings.EnableHttpServer = httpServerSwitch.IsChecked == true;
            _app.AppSettings.Save();

            try
            {
                if (_app.AppSettings.EnableHttpServer)
                    Services.HttpServerService.Instance.Start(_app.AppSettings.HttpServerPort);
                else
                    Services.HttpServerService.Instance.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] HTTP Server 启停异常: {ex.Message}");
            }
            UpdateHttpServerStatus();
        }

        private void HttpPort_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyHttpServerPort();
        }

        private void HttpPort_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyHttpServerPort();
        }

        /// <summary>校验并应用 HTTP 端口，变化时重启服务</summary>
        private void ApplyHttpServerPort()
        {
            if (_isLoading) return;
            if (int.TryParse(httpPortBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
            {
                if (_app.AppSettings.HttpServerPort != port)
                {
                    _app.AppSettings.HttpServerPort = port;
                    _app.AppSettings.Save();
                    try
                    {
                        if (_app.AppSettings.EnableHttpServer)
                            Services.HttpServerService.Instance.Start(port); // 端口变化时先停后启
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Settings] HTTP Server 改端口异常: {ex.Message}");
                    }
                }
            }
            else
            {
                httpPortBox.Text = _app.AppSettings.HttpServerPort.ToString();
            }
            UpdateHttpServerStatus();
        }

        /// <summary>更新 HTTP Server 运行状态显示</summary>
        private void UpdateHttpServerStatus()
        {
            if (httpServerStatusText == null) return;
            var svc = Services.HttpServerService.Instance;
            if (_app.AppSettings.EnableHttpServer && svc.IsRunning)
                httpServerStatusText.Text = Framework.Lang.TF("运行中：http://127.0.0.1:{0}/", svc.CurrentPort);
            else if (_app.AppSettings.EnableHttpServer)
                httpServerStatusText.Text = Framework.Lang.T("未运行（端口可能被占用）");
            else
                httpServerStatusText.Text = Framework.Lang.T("已停止");
        }

        private void UsageStatsSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enabled = usageStatsSwitch.IsChecked == true;
            Framework.AppSettings.Instance.ShowUsageStats = enabled;
            Framework.AppSettings.Instance.Save();

            try
            {
                Services.WindowUsageService.Instance.ApplyEnabled(enabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 窗口使用时长采集启停异常: {ex.Message}");
            }
        }

        private void AutoStartSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enabled = autoStartSwitch.IsChecked == true;
            Framework.AppSettings.Instance.RunAtStartup = enabled;
            Framework.AppSettings.Instance.Save();
            Services.AutoStartService.Apply(enabled);
        }

        #endregion

        #region 模块 - 数据模版管理

        // 「我的数据看板」独立窗口的列表容器（原内嵌在设置页，现移入独立窗口）
        private Window? _dashboardListWindow;
        private StackPanel dashboardItemListPanel = new();

        /// <summary>打开「我的数据看板」独立窗口（已打开则聚焦），列表/添加/隐藏浏览器配置都在该窗口内</summary>
        private void OpenDashboardList_Click(object sender, RoutedEventArgs e)
        {
            if (_dashboardListWindow != null)
            {
                _dashboardListWindow.Activate();
                return;
            }

            var win = new Window
            {
                Title = Framework.Lang.T("我的数据看板"),
                Width = 560,
                Height = 520,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            win.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(win, App.IsCurrentThemeLight());
            win.Closed += (s, ev) => _dashboardListWindow = null;

            var root = new StackPanel { Margin = new Thickness(16) };

            // 顶部栏：添加后台数据项 + 说明
            var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var addBtn = new Button
            {
                Content = Framework.Lang.T("+ 添加后台数据项"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6)
            };
            addBtn.Click += AddBackgroundDashItem_Click;
            rightActions.Children.Add(addBtn);
            DockPanel.SetDock(rightActions, Dock.Right);
            topBar.Children.Add(rightActions);

            var hint = new TextBlock
            {
                Text = Framework.Lang.T("统一轮询数据源（面板组件仅展示，可添加不在面板显示的后台数据项）"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                VerticalAlignment = VerticalAlignment.Center
            };
            topBar.Children.Add(hint);
            root.Children.Add(topBar);

            // 列表（可滚动）
            dashboardItemListPanel = new StackPanel();
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = dashboardItemListPanel
            });

            win.Content = root;
            _dashboardListWindow = win;
            RefreshDashboardItemList();
            win.Show();
        }

        /// <summary>刷新模块页面（连接方式开关 + 模版列表）</summary>
        private void RefreshModulesPage()
        {
            // 初始化连接方式 RadioButton 状态
            var mode = _app.AppSettings.DataConnectionMode;
            rbModeExtension.IsChecked = mode == "extension";
            rbModeCdp.IsChecked = mode == "cdp";
            cdpPortSection.Visibility = mode == "cdp" ? Visibility.Visible : Visibility.Collapsed;
            hiddenBrowserSection.Visibility = mode == "cdp" ? Visibility.Visible : Visibility.Collapsed;
            cdpPortBox.Text = _app.AppSettings.CdpDebugPort.ToString();

            // 初始化隐藏浏览器模式下拉框
            var hbMode = _app.AppSettings.HiddenBrowserMode;
            hiddenBrowserModeCombo.SelectedIndex = hbMode == "hidden" ? 1 : 0;

            RefreshDashboardItemList();
            RefreshTemplateList();
        }

        /// <summary>隐藏浏览器模式切换（模块配置区下拉框触发）</summary>
        private void HiddenBrowserMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            var mode = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag as string ?? "headless";
            _app.AppSettings.HiddenBrowserMode = mode;
            _app.AppSettings.Save();
        }

        /// <summary>添加后台数据项（不在面板显示，仅后台轮询）：选数据模板或无模板自定义 → 名字 → 变量名 → 浏览器/Profile 即可生效</summary>
        private void AddBackgroundDashItem_Click(object sender, RoutedEventArgs e)
        {
            var templates = DataTemplateManager.Load().Templates;

            var dialog = new Window
            {
                Title = Framework.Lang.T("添加后台数据项"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog);

            var root = new StackPanel { Margin = new Thickness(20) };
            var labelBrush = (Brush)FindResource("BrushTextSec");
            var smallFont = (double)FindResource("FontSizeSmall");
            var textBoxStyle = (Style)FindResource("GlobalTextBoxStyle");
            var comboStyle = (Style)FindResource("GlobalComboBoxStyle");

            // 数据模板（URL/选择器/刷新间隔由模板提供；选「无模板」则手动配置）
            root.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("数据模板"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var templateCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            templateCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("无模板"), Tag = (string?)null });
            foreach (var t in templates)
                templateCombo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(t.Category) ? t.Name : $"{t.Name}  [{t.Category}]", Tag = t.Id, ToolTip = t.Description });
            templateCombo.SelectedIndex = templates.Count > 0 ? 1 : 0;
            root.Children.Add(templateCombo);

            // 无模板自定义配置区（选择器 或 API 请求）
            var customPanel = new StackPanel();
            customPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("获取方式"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var modeCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("页面选择器"), Tag = "SEL" });
            modeCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("API 请求"), Tag = "API" });
            modeCombo.SelectedIndex = 0;
            customPanel.Children.Add(modeCombo);

            customPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("网址"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var urlBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(urlBox);

            var selectorLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("选择器 (CSS Selector 或 XPath)"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) };
            customPanel.Children.Add(selectorLabel);
            var selectorBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(selectorBox);

            var methodLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("请求方式"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) };
            customPanel.Children.Add(methodLabel);
            var methodCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            methodCombo.Items.Add(new ComboBoxItem { Content = "GET", Tag = "GET" });
            methodCombo.Items.Add(new ComboBoxItem { Content = "POST", Tag = "POST" });
            methodCombo.SelectedIndex = 0;
            customPanel.Children.Add(methodCombo);

            var postBodyLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("POST 内容"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) };
            customPanel.Children.Add(postBodyLabel);
            var postBodyBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            customPanel.Children.Add(postBodyBox);

            var extractLabel = new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("响应提取（JSON 路径，如 data.count）"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) };
            customPanel.Children.Add(extractLabel);
            var extractBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(extractBox);

            customPanel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("刷新间隔（秒）"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var intervalBox = new TextBox { Style = textBoxStyle, Text = "60", Margin = new Thickness(0, 0, 0, 12) };
            customPanel.Children.Add(intervalBox);
            root.Children.Add(customPanel);

            // 所属团队（个人 + 团队列表，异步加载；决定变量名唯一性作用域）
            root.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("所属团队"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var teamCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = (string?)null });
            teamCombo.SelectedIndex = 0;
            root.Children.Add(teamCombo);
            string? pendingTeamId = null; // 模板切换时待恢复的团队选中
            _ = Task.Run(async () =>
            {
                try
                {
                    var teams = await TeamService.Instance.ListTeamsAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (teamCombo.Items.Count > 1) return; // 已加载过，避免重复
                        if (teams != null)
                        {
                            foreach (var t in teams)
                                teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
                        }
                        if (pendingTeamId != null)
                        {
                            for (int i = 1; i < teamCombo.Items.Count; i++)
                            {
                                if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == pendingTeamId)
                                { teamCombo.SelectedIndex = i; break; }
                            }
                        }
                    });
                }
                catch { }
            });

            root.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("名称"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(nameBox);

            root.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("变量名（团队/个人内唯一，如 qcoder.count）"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var varKeyBox = new TextBox { Style = textBoxStyle, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(varKeyBox);

            // 浏览器（与数据看板对话框一致：默认浏览器 + 已登记浏览器，Profile 联动）
            var browsers = _app.ProgramRegistry.Browsers;

            root.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("浏览器"), Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var browserLabel = (Emoji.Wpf.TextBlock)root.Children[root.Children.Count - 1];
            var browserCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
            browserCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("默认浏览器"), Tag = (string?)null });
            foreach (var b in browsers)
                browserCombo.Items.Add(new ComboBoxItem { Content = b.Name, Tag = b.Id });
            browserCombo.SelectedIndex = 0;
            root.Children.Add(browserCombo);

            root.Children.Add(new Emoji.Wpf.TextBlock { Text = "Profile", Foreground = labelBrush, FontSize = smallFont, Margin = new Thickness(0, 0, 0, 4) });
            var profileLabel = (Emoji.Wpf.TextBlock)root.Children[root.Children.Count - 1];
            var profileCombo = new ComboBox { Style = comboStyle, Margin = new Thickness(0, 0, 0, 16), IsEditable = false };
            root.Children.Add(profileCombo);

            // Profile 联动刷新
            Action refreshProfiles = () =>
            {
                profileCombo.Items.Clear();
                if (browserCombo.SelectedItem is ComboBoxItem selectedBrowserItem && selectedBrowserItem.Tag is string browserId)
                {
                    var browser = browsers.FirstOrDefault(b => b.Id == browserId);
                    if (browser != null)
                    {
                        foreach (var p in browser.Profiles)
                            profileCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                        if (profileCombo.Items.Count > 0)
                            profileCombo.SelectedIndex = 0;
                    }
                }
            };
            browserCombo.SelectionChanged += (s, ev) => refreshProfiles();

            // 选中模板时预填名称、模板归属团队与模板默认浏览器/Profile
            void ApplyTemplate()
            {
                if (templateCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string tplId) return;
                var tpl = templates.FirstOrDefault(t => t.Id == tplId);
                if (tpl == null) return;
                nameBox.Text = tpl.Name;
                // 预填模板归属团队（列表未加载完时暂存，加载完成后自动恢复）
                pendingTeamId = tpl.TeamId;
                teamCombo.SelectedIndex = 0;
                for (int i = 1; i < teamCombo.Items.Count; i++)
                {
                    if ((teamCombo.Items[i] as ComboBoxItem)?.Tag as string == tpl.TeamId)
                    { teamCombo.SelectedIndex = i; break; }
                }
                // 预填模板默认浏览器
                for (int i = 0; i < browserCombo.Items.Count; i++)
                {
                    if (browserCombo.Items[i] is ComboBoxItem bci
                        && (tpl.DefaultBrowserId == null ? bci.Tag == null : (bci.Tag as string) == tpl.DefaultBrowserId))
                    { browserCombo.SelectedIndex = i; break; }
                }
                refreshProfiles();
                if (!string.IsNullOrEmpty(tpl.DefaultProfileId))
                {
                    for (int i = 0; i < profileCombo.Items.Count; i++)
                    {
                        if (profileCombo.Items[i] is ComboBoxItem pci && (pci.Tag as string) == tpl.DefaultProfileId)
                        { profileCombo.SelectedIndex = i; break; }
                    }
                }
            }
            templateCombo.SelectionChanged += (s, ev) => { ApplyTemplate(); SyncCustomVisibility(); };
            modeCombo.SelectionChanged += (s, ev) => SyncCustomVisibility();
            methodCombo.SelectionChanged += (s, ev) => SyncCustomVisibility();

            // 无模板/获取方式切换时同步各输入区显隐（API 模式不需要浏览器/Profile）
            void SyncCustomVisibility()
            {
                bool isCustom = templateCombo.SelectedIndex >= 0
                    && (templateCombo.SelectedItem as ComboBoxItem)?.Tag is not string;
                bool isApi = isCustom && (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                bool isPost = (methodCombo.SelectedItem as ComboBoxItem)?.Tag as string == "POST";
                customPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                selectorLabel.Visibility = selectorBox.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
                methodLabel.Visibility = methodCombo.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
                postBodyLabel.Visibility = postBodyBox.Visibility = isApi && isPost ? Visibility.Visible : Visibility.Collapsed;
                extractLabel.Visibility = extractBox.Visibility = isApi ? Visibility.Visible : Visibility.Collapsed;
                browserLabel.Visibility = browserCombo.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
                profileLabel.Visibility = profileCombo.Visibility = isApi ? Visibility.Collapsed : Visibility.Visible;
            }
            SyncCustomVisibility();
            ApplyTemplate();

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 70, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 70, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };

            okBtn.Click += async (s, ev) =>
            {
                var tplId = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                Models.DataTemplate? tpl = null;
                if (tplId != null)
                {
                    tpl = templates.FirstOrDefault(t => t.Id == tplId);
                    if (tpl == null)
                    {
                        MessageBox.Show(Framework.Lang.T("请选择数据模板"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    // 无模板自定义：网址必填；选择器模式下选择器必填
                    if (string.IsNullOrWhiteSpace(urlBox.Text))
                    {
                        MessageBox.Show(Framework.Lang.T("请输入请求网址"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        urlBox.Focus();
                        return;
                    }
                    bool isApi = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                    if (!isApi && string.IsNullOrWhiteSpace(selectorBox.Text))
                    {
                        MessageBox.Show(Framework.Lang.T("请输入选择器"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        selectorBox.Focus();
                        return;
                    }
                }
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show(Framework.Lang.T("请输入名称"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameBox.Focus();
                    return;
                }
                string varKey = varKeyBox.Text.Trim();
                if (string.IsNullOrEmpty(varKey))
                {
                    MessageBox.Show(Framework.Lang.T("请输入变量名"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    varKeyBox.Focus();
                    return;
                }

                // 变量作用域：团队下拉选中团队则为团队内唯一，否则个人内唯一
                string? teamId = (teamCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                string? teamName = teamCombo.SelectedIndex > 0 ? (teamCombo.SelectedItem as ComboBoxItem)?.Content as string : null;

                // 变量名唯一性校验：本地（全部面板 + 后台数据项）+ 后端权威校验
                if (IsVarKeyDuplicateLocally(varKey, teamId, null))
                {
                    MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    varKeyBox.Focus();
                    return;
                }
                okBtn.IsEnabled = false;
                bool available;
                try { available = await DataVariableService.Instance.CheckAsync(varKey, teamId); }
                finally { okBtn.IsEnabled = true; }
                if (!available)
                {
                    MessageBox.Show(Framework.Lang.TF("变量名 \"{0}\" 在当前范围（团队/个人）内已存在，请换一个名称。", varKey), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    varKeyBox.Focus();
                    return;
                }

                var item = new DataDashboardItem
                {
                    Label = name,
                    TeamId = teamId,
                    TeamName = teamId == null ? null : teamName,
                    VarKey = varKey,
                    Value = "--"
                };
                if (tpl != null)
                {
                    item.TemplateId = tpl.Id;
                    item.TargetUrl = tpl.TargetUrl;
                    item.Selector = tpl.Selector;
                    item.RefreshIntervalSeconds = tpl.RefreshIntervalSeconds > 0 ? tpl.RefreshIntervalSeconds : 60;
                    item.BrowserId = (browserCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    item.ProfileId = (profileCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                }
                else
                {
                    // 无模板：选择器 或 API 请求手动配置
                    bool isApi = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "API";
                    item.TemplateId = "";
                    item.TargetUrl = urlBox.Text.Trim();
                    if (isApi)
                    {
                        item.Selector = "";
                        item.ApiMethod = (methodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "GET";
                        item.ApiBody = postBodyBox.Text.Trim();
                        item.ResponseExtract = extractBox.Text.Trim();
                        item.BrowserId = null;
                        item.ProfileId = null;
                    }
                    else
                    {
                        item.Selector = selectorBox.Text.Trim();
                        item.BrowserId = (browserCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                        item.ProfileId = (profileCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                    }
                    item.RefreshIntervalSeconds = int.TryParse(intervalBox.Text.Trim(), out var secs) && secs >= 5 ? secs : 60;
                }
                _app.WindowConfigs.BackgroundDashboardItems.Add(item);
                _app.WindowConfigs.Save();

                // 同步到轮询服务
                DataPollingService.Instance.SyncFromConfigs(_app.WindowConfigs);
                dialog.Close();
                RefreshDashboardItemList();
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dialog.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
            dialog.ShowDialog();
        }

        /// <summary>
        /// 变量名本地唯一性校验：跨所有窗口面板与后台数据项，同作用域（同为个人，或同一团队）内不能有同名变量。
        /// excludeItemId 用于编辑时排除数据项自身。
        /// </summary>
        private bool IsVarKeyDuplicateLocally(string varKey, string? teamId, string? excludeItemId)
        {
            bool SameScope(DataDashboardItem di)
            {
                if (di.Id == excludeItemId) return false;
                if (string.IsNullOrEmpty(di.VarKey)) return false;
                if (!string.Equals(di.VarKey, varKey, StringComparison.OrdinalIgnoreCase)) return false;
                bool diPersonal = string.IsNullOrEmpty(di.TeamId);
                bool targetPersonal = string.IsNullOrEmpty(teamId);
                return (diPersonal && targetPersonal) || (!diPersonal && !targetPersonal && di.TeamId == teamId);
            }

            bool PanelHas(PanelItem panel)
            {
                if (panel.DashboardItems != null && panel.DashboardItems.Any(SameScope)) return true;
                if (panel.ChildPanels != null)
                {
                    foreach (var child in panel.ChildPanels)
                        if (PanelHas(child)) return true;
                }
                return false;
            }

            foreach (var win in _app.WindowConfigs.Windows)
                foreach (var panel in win.Panels)
                    if (PanelHas(panel)) return true;
            return _app.WindowConfigs.BackgroundDashboardItems.Any(SameScope);
        }

        /// <summary>刷新“我的数据看板”列表（遍历所有窗口/面板的 DashboardItems）</summary>
        private void RefreshDashboardItemList()
        {
            dashboardItemListPanel.Children.Clear();

            var polling = Services.DataPollingService.Instance;
            var rawItems = new List<(string source, Models.DataDashboardItem item)>();

            // 面板数据项
            foreach (var win in _app.WindowConfigs.Windows)
            {
                foreach (var panel in win.Panels)
                {
                    if (panel.DashboardItems == null || panel.DashboardItems.Count == 0) continue;
                    var panelTitle = panel.Title ?? panel.Type.ToString();
                    foreach (var item in panel.DashboardItems)
                        rawItems.Add(($"{win.Name} / {panelTitle}", item));
                }
            }

            // 后台数据项
            foreach (var item in _app.WindowConfigs.BackgroundDashboardItems)
                rawItems.Add((Framework.Lang.T("后台轮询"), item));

            // 同一数据源（同看板模板/同变量）出现在多个窗口时只轮询一个实例，列表同样合并展示
            var allItems = new List<(string source, Models.DataDashboardItem item)>();
            foreach (var (source, item) in rawItems)
            {
                var key = Services.DataPollingService.GetItemIdentity(item);
                var existing = allItems.FindIndex(t => Services.DataPollingService.GetItemIdentity(t.item) == key);
                if (existing >= 0)
                    allItems[existing] = ($"{allItems[existing].source}、{source}", allItems[existing].item);
                else
                    allItems.Add((source, item));
            }

            if (allItems.Count == 0)
            {
                dashboardItemListPanel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.T("暂无数据看板项，请在侧边栏“数据看板”模块中添加，或点击下方“+ 添加后台数据项”"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeSmall")
                });
                return;
            }

            foreach (var (source, item) in allItems)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 状态点
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 信息
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 按钮

                // 连接状态点
                var state = polling.GetItemState(item);
                var dotColor = state switch
                {
                    Services.ConnectionState.Connected => System.Windows.Media.Brushes.LimeGreen,
                    Services.ConnectionState.Connecting => System.Windows.Media.Brushes.Gold,
                    Services.ConnectionState.Error => System.Windows.Media.Brushes.IndianRed,
                    _ => System.Windows.Media.Brushes.Gray
                };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Fill = dotColor,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = state.ToString()
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                // 信息区
                var info = new StackPanel();
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = $"{item.Label}  =  {item.Value}",
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                });
                info.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.TF("目标: {0}  |  刷新: {1}s  |  所属: {2}", item.TargetUrl, item.RefreshIntervalSeconds, source)
                        + (string.IsNullOrEmpty(item.VarKey) ? "" : Framework.Lang.TF("  |  变量: {0}", item.VarKey)),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = (double)FindResource("FontSizeTiny"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                // 按钮区：刷新 + 断开
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var refreshBtn = new Button
                {
                    Content = "↻", Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2), FontSize = 11, Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = Framework.Lang.T("立即刷新")
                };
                var capturedItem = item;
                refreshBtn.Click += async (s, e) =>
                {
                    try { await polling.RefreshItemAsync(capturedItem); RefreshDashboardItemList(); }
                    catch { }
                };
                btnPanel.Children.Add(refreshBtn);

                var connKey = polling.GetConnectionKey(item);
                var conn = polling.GetConnection(connKey);
                if (conn != null)
                {
                    var disconnectBtn = new Button
                    {
                        Content = Framework.Lang.T("断开"), Style = (Style)FindResource("GhostButtonStyle"),
                        Padding = new Thickness(6, 2, 6, 2), FontSize = 10,
                        ToolTip = conn.IsSelfLaunched ? Framework.Lang.T("断开并关闭自启动的隐藏浏览器") : Framework.Lang.T("断开 CDP 连接（不关闭浏览器）")
                    };
                    var capturedKey = connKey;
                    disconnectBtn.Click += (s, e) => { polling.DisconnectConnection(capturedKey); RefreshDashboardItemList(); };
                    btnPanel.Children.Add(disconnectBtn);
                }

                Grid.SetColumn(btnPanel, 2);
                row.Children.Add(btnPanel);

                dashboardItemListPanel.Children.Add(row);
            }
        }

        /// <summary>数据模块连接方式切换（RadioButton）</summary>
        private void DataFetchMode_Changed(object sender, RoutedEventArgs e)
        {
            if (rbModeExtension == null || rbModeCdp == null) return;
            var mode = rbModeCdp.IsChecked == true ? "cdp" : "extension";
            _app.AppSettings.DataConnectionMode = mode;
            _app.AppSettings.Save();

            cdpPortSection.Visibility = mode == "cdp" ? Visibility.Visible : Visibility.Collapsed;
            hiddenBrowserSection.Visibility = mode == "cdp" ? Visibility.Visible : Visibility.Collapsed;

            // WS 服务始终保持运行：插件市场 / 插件编辑器依赖 19191 桥，
            // 切换 CDP 模式只是不再依赖浏览器扩展，不能停掉 WS
            try
            {
                if (!Services.WebSocketServerService.Instance.IsRunning)
                    Services.WebSocketServerService.Instance.Start();
            }
            catch { /* ignore */ }
        }

        /// <summary>CDP 端口修改</summary>
        private void CdpPort_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(cdpPortBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
            {
                _app.AppSettings.CdpDebugPort = port;
                _app.AppSettings.Save();
            }
            else
            {
                cdpPortBox.Text = _app.AppSettings.CdpDebugPort.ToString();
            }
        }

        /// <summary>测试 CDP 连接：列出浏览器所有标签页</summary>
        private async void TestCdpConnection_Click(object sender, RoutedEventArgs e)
        {
            int port = _app.AppSettings.CdpDebugPort;
            if (int.TryParse(cdpPortBox.Text.Trim(), out int p) && p > 0) port = p;

            string resultText;
            try
            {
                // 复用已缓存的 WebSocket CDP 连接（避免重复新建连接触发浏览器弹窗）
                var client = await Services.CdpBrowserService.Instance.GetOrConnectBrowserAsync(port);
                if (client == null)
                {
                    resultText = Framework.Lang.TF("WebSocket CDP 连接失败（127.0.0.1:{0}）。\n\n请确认：\n1. 浏览器已开启远程调试（edge://inspect 或 --remote-debugging-port={1}）\n2. 没有防火墙/安全软件拦截本地连接", port, port);
                }
                else
                {
                    // 通过 Target.getTargets 获取所有标签页
                    var targets = await client.SendCommandAsync(ChromeProtocol.Domains.Target.GetTargets());
                    var pages = targets.TargetInfos.Where(t => t.Type == "page").ToList();

                    if (pages.Count == 0)
                    {
                        resultText = Framework.Lang.TF("连接成功（ws://127.0.0.1:{0}），但没有打开的页面标签页。", port);
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"WebSocket CDP 连接成功（127.0.0.1:{port}），共 {pages.Count} 个页面标签页：");
                        sb.AppendLine(new string('─', 40));
                        int idx = 1;
                        foreach (var tab in pages)
                        {
                            var title = string.IsNullOrEmpty(tab.Title) ? Framework.Lang.T("(无标题)") : tab.Title;
                            sb.AppendLine($"{idx}. {title}");
                            sb.AppendLine($"   {tab.Url}");
                            idx++;
                        }
                        resultText = sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                resultText = Framework.Lang.TF("连接失败：{0}\n\n请确认浏览器已开启远程调试（edge://inspect 或 --remote-debugging-port={1}）", ex.Message, port);
            }

            // 弹出对话框显示结果
            var dialog = new Window
            {
                Title = Framework.Lang.T("CDP 连接测试"),
                Width = 520,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain"),
                ResizeMode = ResizeMode.CanResize
            };
            dialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var textBox = new TextBox
            {
                Text = resultText,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 12,
                Padding = new Thickness(12),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("BrushTextMain")
            };

            dialog.Content = textBox;
            dialog.ShowDialog();
        }

        private void RefreshTemplateList()
        {
            templateListPanel.Children.Clear();
            var manager = DataTemplateManager.Load();

            if (manager.Templates.Count == 0)
            {
                templateListPanel.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = Framework.Lang.T("暂无数据模版"),
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
                // 名称 + 团队标签
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                nameRow.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = tpl.Name,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    FontSize = (double)FindResource("FontSizeNormal"),
                    FontWeight = FontWeights.SemiBold
                });
                if (!string.IsNullOrEmpty(tpl.TeamName))
                {
                    nameRow.Children.Add(new TextBlock
                    {
                        Text = $" {tpl.TeamName}",
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0),
                        Opacity = 0.8
                    });
                }
                info.Children.Add(nameRow);

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
                    Text = Framework.Lang.TF("目标: {0}  |  选择器: {1}  |  刷新: {2}s", tpl.TargetUrl, tpl.Selector, tpl.RefreshIntervalSeconds)
                        + (string.IsNullOrEmpty(tpl.TeamName) ? "" : Framework.Lang.TF("  |  团队: {0}", tpl.TeamName)),
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

        private async void AddTemplate_Click(object sender, RoutedEventArgs e)
        {
            // 预加载团队列表
            List<Services.Team>? teams = null;
            try { teams = await Services.TeamService.Instance.ListTeamsAsync(); } catch { }

            var dialog = new Window
            {
                Title = Framework.Lang.T("新建数据模版"),
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
                Text = Framework.Lang.T("新建数据模版"),
                FontSize = (double)FindResource("FontSizeSubtitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 名称
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("模版名称"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            // 描述
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("描述"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var descBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(descBox);

            // 分类
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("分类"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var categoryBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(categoryBox);

            // 所属团队
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("所属团队"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var teamCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            teamCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("个人"), Tag = null });
            if (teams != null)
            {
                foreach (var t in teams)
                    teamCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
            }
            teamCombo.SelectedIndex = 0;
            panel.Children.Add(teamCombo);

            // 目标网址
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("目标网址"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var urlBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(urlBox);

            // 浏览器选择
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("浏览器"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var browserCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            browserCombo.Items.Add(Framework.Lang.T("默认浏览器"));
            foreach (var b in _registry.Browsers)
                browserCombo.Items.Add(b.Name);
            browserCombo.SelectedIndex = 0;
            panel.Children.Add(browserCombo);

            // 容器(Profile)选择
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("容器 (Profile)"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var profileCombo = new ComboBox { Style = (Style)FindResource("GlobalComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
            profileCombo.Items.Add(Framework.Lang.T("默认"));
            profileCombo.SelectedIndex = 0;
            panel.Children.Add(profileCombo);

            // 浏览器切换时更新Profile列表
            browserCombo.SelectionChanged += (s, ev) =>
            {
                profileCombo.Items.Clear();
                profileCombo.Items.Add(Framework.Lang.T("默认"));
                if (browserCombo.SelectedIndex > 0)
                {
                    var selectedBrowser = _registry.Browsers[browserCombo.SelectedIndex - 1];
                    foreach (var p in selectedBrowser.Profiles)
                        profileCombo.Items.Add(p.Name);
                }
                profileCombo.SelectedIndex = 0;
            };

            // 选择器
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("选择器 (CSS Selector 或 XPath)"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var selectorGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selectorBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            Grid.SetColumn(selectorBox, 0);
            selectorGrid.Children.Add(selectorBox);
            var pickSelectorBtn = new Button
            {
                Content = Framework.Lang.T("选择"),
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
                    MessageBox.Show(Framework.Lang.T("请先输入目标网址"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        MessageBox.Show(Framework.Lang.T("未找到可用浏览器，请在浏览器下拉框中选择一个已注册的浏览器"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // 禁用按钮
                pickSelectorBtn.IsEnabled = false;
                pickSelectorBtn.Content = Framework.Lang.T("选择中...");

                try
                {
                    var (port, process) = await Services.CdpBrowserService.Instance.LaunchVisibleBrowserAsync(browserPath, profileDir, urlBox.Text.Trim());
                    if (process == null)
                    {
                        MessageBox.Show(Framework.Lang.T("启动浏览器失败\n\n可能原因：该浏览器已在运行中，请先关闭浏览器后重试"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                            MessageBox.Show(Framework.Lang.T("未选择到元素（超时或取消）"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                    MessageBox.Show(Framework.Lang.TF("选择器获取失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    pickSelectorBtn.IsEnabled = true;
                    pickSelectorBtn.Content = Framework.Lang.T("选择");
                }
            };

            // 刷新间隔
            panel.Children.Add(new Emoji.Wpf.TextBlock { Text = Framework.Lang.T("刷新间隔（秒）"), Foreground = (Brush)FindResource("BrushTextSec"), FontSize = (double)FindResource("FontSizeSmall"), Margin = new Thickness(0, 0, 0, 4) });
            var intervalBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle"), Text = "60", Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(intervalBox);

            // 按钮
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Style = (Style)FindResource("StandardButtonStyle"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button { Content = Framework.Lang.T("保存"), Style = (Style)FindResource("PrimaryButtonStyle"), Padding = new Thickness(16, 8, 16, 8) };
            okBtn.Click += (s, ev) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text) || string.IsNullOrWhiteSpace(selectorBox.Text))
                {
                    MessageBox.Show(Framework.Lang.T("请填写名称、目标网址和选择器"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        : null,
                    TeamId = (teamCombo.SelectedItem as ComboBoxItem)?.Tag as string,
                    TeamName = teamCombo.SelectedIndex > 0 ? (teamCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() : null
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
            if (MessageBox.Show(Framework.Lang.T("确定删除该模版？"), Framework.Lang.T("确认"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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

        /// <summary>从已有数据看板项导入为模板（去重：同 URL+Selector 不重复导入）</summary>
        private void ImportFromDashboard_Click(object sender, RoutedEventArgs e)
        {
            var manager = DataTemplateManager.Load();
            int imported = 0;

            foreach (var win in _app.WindowConfigs.Windows)
            {
                foreach (var panel in win.Panels)
                {
                    if (panel.DashboardItems == null || panel.DashboardItems.Count == 0) continue;
                    foreach (var item in panel.DashboardItems)
                    {
                        // 去重：同 URL + Selector 已存在则跳过
                        bool exists = manager.Templates.Any(t =>
                            string.Equals(t.TargetUrl, item.TargetUrl, StringComparison.OrdinalIgnoreCase) &&
                            t.Selector == item.Selector);
                        if (exists) continue;

                        var template = new Models.DataTemplate
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = item.Label,
                            Description = Framework.Lang.TF("从数据看板导入（{0}）", win.Name),
                            Category = Framework.Lang.T("自定义"),
                            TargetUrl = item.TargetUrl,
                            Selector = item.Selector,
                            RefreshIntervalSeconds = item.RefreshIntervalSeconds,
                            DefaultBrowserId = item.BrowserId,
                            DefaultProfileId = item.ProfileId,
                            TeamId = item.TeamId,
                            TeamName = item.TeamName
                        };
                        manager.SaveUserTemplate(template);
                        manager.Templates.Add(template);
                        imported++;
                    }
                }
            }

            if (imported > 0)
            {
                RefreshTemplateList();
                MessageBox.Show(Framework.Lang.TF("已导入 {0} 个数据看板项为模板", imported), Framework.Lang.T("导入成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(Framework.Lang.T("没有新的数据看板项可导入（已存在相同 URL+选择器 的模板）"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
                    versionText.Text = Framework.Lang.TF("版本 v{0}", mgr.CurrentVersion);
            }
            catch { }

            // 订阅更新事件
            SubscribeUpdateEvents();

            // 如果已有更新就绪，直接显示
            if (VelopackUpdateService.Instance.IsUpdateReady)
            {
                applyUpdatePanel.Visibility = Visibility.Visible;
                updateStatusText.Text = Framework.Lang.T("更新包已就绪，重启即可生效！");
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
                updateStatusText.Text = Framework.Lang.TF("检查失败: {0}", ex.Message);
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
                MessageBox.Show(Framework.Lang.T("更新信息不存在，请重新检查更新。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                VelopackUpdateService.Instance.ApplyAndRestart(updateInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Framework.Lang.TF("应用更新并重启失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                windowListPanel.Children.Add(BuildSectionHeader(Framework.Lang.T("我的窗口")));
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
                        System.Net.Http.HttpMethod.Get, Constants.URLs.ServerBaseUrl + "/api/sidebar/bar/visible");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200 && root["data"] is Newtonsoft.Json.Linq.JArray bars)
                    {
                        var teamBars = bars.Where(b => b["ownerType"]?.ToString() == "team").ToList();
                        if (teamBars.Count > 0)
                        {
                            windowListPanel.Children.Add(BuildSectionHeader(Framework.Lang.T("团队窗口")));
                            windowListPanel.Children.Add(BuildListHeaderRow());
                            foreach (var bar in teamBars)
                            {
                                string title = bar["title"]?.ToString() ?? Framework.Lang.T("未命名");
                                bool canEdit = bar["canEdit"]?.ToObject<bool>() ?? false;
                                bool canDelete = bar["canDelete"]?.ToObject<bool>() ?? false;
                                string perm = canEdit ? Framework.Lang.T("可编辑") : Framework.Lang.T("只读");
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
                    Text = Framework.Lang.T("暂无窗口，请在主界面创建小屏窗口"),
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
            AddHeader(Framework.Lang.T("显示"), 0);
            AddHeader(Framework.Lang.T("名称"), 1);
            AddHeader(Framework.Lang.T("类型"), 2);
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
            string tag = ownerType == "team" ? Framework.Lang.T("团队") : Framework.Lang.T("个人");
            if (shareMode == "copy") tag = Framework.Lang.T("复制");
            else if (shareMode == "reference") tag = Framework.Lang.T("引用");
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

            var editItem = new MenuItem { Header = Framework.Lang.T("编辑") };
            editItem.Click += (_, _) => WindowEdit_Click(tag);
            if (string.IsNullOrEmpty(tag.LocalId)) editItem.IsEnabled = false; // 团队窗口暂无本地结构可编辑
            menu.Items.Add(editItem);

            if (!string.IsNullOrEmpty(tag.ServerId))
            {
                var shareItem = new MenuItem { Header = Framework.Lang.T("分享") };
                shareItem.Click += (_, _) => ShareWindow_Click(new Button { Tag = tag.ServerId }, new RoutedEventArgs());
                menu.Items.Add(shareItem);
            }

            var deleteItem = new MenuItem { Header = Framework.Lang.T("删除"), Foreground = System.Windows.Media.Brushes.IndianRed };
            deleteItem.Click += (_, _) => WindowDelete_Click(tag);
            menu.Items.Add(deleteItem);

            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>「编辑」菜单项点击 → 弹出树状结构窗口，列出该窗口下所有组件和分组（支持删除/拖拽排序，保存后更新主窗口）</summary>
        private void WindowEdit_Click(WindowRowTag tag)
        {
            var app = (App)Application.Current;
            var win = app.WindowConfigs.GetById(tag.LocalId!);
            if (win == null)
            {
                MessageBox.Show(Framework.Lang.T("未找到该窗口的本地配置。"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ShowWindowStructureDialog(win);
        }

        /// <summary>弹出窗口结构编辑对话框：树状展示组件与分组，支持拖拽排序、右键删除，点击保存后同步更新主窗口</summary>
        private void ShowWindowStructureDialog(WindowConfig win)
        {
            var app = (App)Application.Current;

            // 通过 JSON 深拷贝：所有编辑都在副本上进行，点「保存」才写回（点「取消」则丢弃）
            var workingPanels = Newtonsoft.Json.JsonConvert.DeserializeObject<List<PanelItem>>(
                Newtonsoft.Json.JsonConvert.SerializeObject(win.Panels)) ?? new List<PanelItem>();

            var dialog = new Window
            {
                Title = Framework.Lang.TF("窗口结构 - {0}", win.Name),
                Width = 420,
                Height = 540,
                MinWidth = 340,
                MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var rootPanel = new DockPanel { Margin = new Thickness(16) };

            // 顶部统计信息
            var summaryText = new Emoji.Wpf.TextBlock
            {
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(summaryText, Dock.Top);
            rootPanel.Children.Add(summaryText);

            // 操作提示
            var hintText = new TextBlock
            {
                Text = Framework.Lang.T("提示：拖拽节点可调整顺序 / 移入分组；右键节点可删除。"),
                FontSize = 11,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(hintText, Dock.Top);
            rootPanel.Children.Add(hintText);

            // 底部按钮栏：保存 / 取消
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(btnBar, Dock.Bottom);
            var saveBtn = new Button { Content = Framework.Lang.T("保存"), Width = 80, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 80, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
            btnBar.Children.Add(saveBtn);
            btnBar.Children.Add(cancelBtn);
            rootPanel.Children.Add(btnBar);

            // 树状结构（支持拖放）
            var tree = new TreeView
            {
                AllowDrop = true,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            rootPanel.Children.Add(tree);

            PanelItem? draggedPanel = null;   // 当前正在拖拽的面板
            Point dragStart = default;        // 鼠标按下位置（用于判定拖拽阈值）

            // ── 重建树 ──
            void RebuildTree()
            {
                tree.Items.Clear();
                if (workingPanels.Count == 0)
                {
                    tree.Items.Add(new TreeViewItem
                    {
                        Header = Framework.Lang.T("（空窗口，暂无组件）"),
                        Foreground = (Brush)FindResource("BrushTextMuted"),
                        IsEnabled = false
                    });
                }
                else
                {
                    foreach (var p in workingPanels)
                        tree.Items.Add(BuildNode(p));
                }
                int total = CountPanelsRecursive(workingPanels);
                summaryText.Text = Framework.Lang.TF("🪟 {0}    共 {1} 个顶层项 / {2} 个模块", win.Name, workingPanels.Count, total);
            }

            // ── 构建树节点（含右键删除菜单）──
            TreeViewItem BuildNode(PanelItem panel)
            {
                var node = new TreeViewItem
                {
                    Header = BuildPanelNodeHeader(panel),
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    IsExpanded = panel.Type == PanelType.Group,
                    Tag = panel
                };

                var ctx = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle") };
                var delMi = new MenuItem { Header = Framework.Lang.T("删除"), Foreground = System.Windows.Media.Brushes.IndianRed };
                delMi.Click += (_, _) =>
                {
                    if (panel.Type == PanelType.Group && (panel.ChildPanels?.Count ?? 0) > 0)
                    {
                        var r = MessageBox.Show(Framework.Lang.TF("该分组内有 {0} 个组件，将一并删除。确定删除？", panel.ChildPanels!.Count), Framework.Lang.T("删除确认"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (r != MessageBoxResult.Yes) return;
                    }
                    FindContainingList(workingPanels, panel)?.Remove(panel);
                    RebuildTree();
                };
                ctx.Items.Add(delMi);
                node.ContextMenu = ctx;

                if (panel.ChildPanels != null)
                    foreach (var c in panel.ChildPanels)
                        node.Items.Add(BuildNode(c));

                return node;
            }

            // ── 拖拽开始 ──
            tree.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(null);
                draggedPanel = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.Tag as PanelItem;
            };

            tree.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || draggedPanel == null) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                var dragged = draggedPanel;
                draggedPanel = null;
                DragDrop.DoDragDrop(tree, new DataObject(typeof(PanelItem), dragged), DragDropEffects.Move);
            };

            tree.PreviewMouseLeftButtonUp += (s, e) => draggedPanel = null;

            // ── 拖拽悬停：设置允许放置效果 ──
            tree.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(typeof(PanelItem)) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };

            // ── 放置：执行移动 ──
            tree.Drop += (s, e) =>
            {
                e.Handled = true;
                if (e.Data.GetData(typeof(PanelItem)) is not PanelItem dragged) return;

                var targetItem = FindTreeViewItemAt(tree, e.GetPosition(tree));
                var target = targetItem?.Tag as PanelItem;

                double fraction = 0.5;
                if (targetItem != null)
                {
                    double y = e.GetPosition(targetItem).Y;
                    fraction = y / Math.Max(1, targetItem.ActualHeight);
                }

                if (PerformDrop(workingPanels, dragged, target, targetItem == null ? 1.0 : fraction))
                    RebuildTree();
            };

            // ── 保存：写回配置并刷新主窗口 ──
            saveBtn.Click += (s, e) =>
            {
                win.Panels = workingPanels;
                app.WindowConfigs.Save();
                app.RefreshAllNativePanels();
                dialog.Close();
            };
            cancelBtn.Click += (s, e) => dialog.Close();

            RebuildTree();
            dialog.Content = rootPanel;
            dialog.Show();
        }

        /// <summary>执行拖放移动：将 dragged 移动到 target 的前面/后面/分组内（target 为 null 表示移到顶层末尾）。返回是否有变更。</summary>
        private static bool PerformDrop(List<PanelItem> root, PanelItem dragged, PanelItem? target, double fraction)
        {
            // 空白区域 → 移到顶层末尾
            if (target == null)
            {
                var src = FindContainingList(root, dragged);
                if (src == null) return false;
                if (src == root && root.IndexOf(dragged) == root.Count - 1) return false; // 已在末尾
                src.Remove(dragged);
                root.Add(dragged);
                return true;
            }

            if (dragged == target) return false;
            if (IsDescendant(dragged, target)) return false; // 不能移入自己的子项

            var sourceList = FindContainingList(root, dragged);
            if (sourceList == null) return false;

            // 中间区域且目标是分组 → 移入分组内
            if (target.Type == PanelType.Group && fraction > 0.3 && fraction < 0.7)
            {
                target.ChildPanels ??= new List<PanelItem>();
                sourceList.Remove(dragged);
                target.ChildPanels.Add(dragged);
                target.IsExpanded = true;
                return true;
            }

            // 上部/下部 → 插入到目标的前面/后面
            var targetList = FindContainingList(root, target);
            if (targetList == null) return false;

            sourceList.Remove(dragged);
            int targetIdx = targetList.IndexOf(target);
            if (targetIdx < 0) return false;

            int insertIdx = fraction <= 0.3 ? targetIdx : targetIdx + 1;
            if (insertIdx > targetList.Count) insertIdx = targetList.Count;
            targetList.Insert(insertIdx, dragged);
            return true;
        }

        /// <summary>查找直接包含 target 的面板列表（顶层或某分组的 ChildPanels），未找到返回 null</summary>
        private static List<PanelItem>? FindContainingList(List<PanelItem> panels, PanelItem target)
        {
            if (panels.Contains(target)) return panels;
            foreach (var p in panels)
            {
                if (p.ChildPanels == null) continue;
                var found = FindContainingList(p.ChildPanels, target);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>判断 potentialDescendant 是否为 ancestor 的后代（防止拖入自身子级造成环）</summary>
        private static bool IsDescendant(PanelItem ancestor, PanelItem potentialDescendant)
        {
            if (ancestor.ChildPanels == null) return false;
            foreach (var c in ancestor.ChildPanels)
            {
                if (c == potentialDescendant) return true;
                if (IsDescendant(c, potentialDescendant)) return true;
            }
            return false;
        }

        /// <summary>沿可视树向上查找指定类型的祖先（兼容 Run 等 ContentElement：非 Visual 时改走逻辑树）</summary>
        private static T? FindAncestor<T>(DependencyObject? dep) where T : DependencyObject
        {
            while (dep != null && dep is not T)
            {
                dep = dep is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(dep)
                    : LogicalTreeHelper.GetParent(dep);
            }
            return dep as T;
        }

        /// <summary>命中测试：获取指定坐标处的 TreeViewItem</summary>
        private static TreeViewItem? FindTreeViewItemAt(TreeView tree, Point point)
        {
            var hit = VisualTreeHelper.HitTest(tree, point);
            var dep = hit?.VisualHit;
            while (dep != null && dep is not TreeViewItem)
                dep = VisualTreeHelper.GetParent(dep);
            return dep as TreeViewItem;
        }

        /// <summary>递归统计面板总数（含分组内子面板）</summary>
        private static int CountPanelsRecursive(List<PanelItem> panels)
        {
            int count = 0;
            foreach (var p in panels)
            {
                count++;
                if (p.ChildPanels != null)
                    count += CountPanelsRecursive(p.ChildPanels);
            }
            return count;
        }

        /// <summary>构建树节点的头部（显示名 + 附加信息）</summary>
        private StackPanel BuildPanelNodeHeader(PanelItem panel)
        {
            string display = GetPanelDisplayName(panel);

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = display,
                FontSize = 13,
                Foreground = (Brush)FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            });

            // 附加信息（自定义标题时显示类型名；分组显示子项数量）
            string? extra = null;
            if (panel.Type == PanelType.Group)
            {
                int childCount = panel.ChildPanels?.Count ?? 0;
                extra = Framework.Lang.TF("{0} 项", childCount);
            }
            else if (!string.IsNullOrEmpty(panel.Title))
            {
                extra = GetPanelTypeName(panel.Type);
            }

            if (extra != null)
            {
                headerPanel.Children.Add(new TextBlock
                {
                    Text = $"  ({extra})",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            return headerPanel;
        }

        /// <summary>获取面板的显示名称（优先自定义标题，其次类型中文名；快捷方式追加分组名）</summary>
        private static string GetPanelDisplayName(PanelItem panel)
        {
            string typeName = GetPanelTypeName(panel.Type);

            if (panel.Type == PanelType.QuickGroup)
            {
                // 快捷方式：分组名已为面板自有属性（原 quickgroups.json 数据已迁入）
                string groupName = string.IsNullOrEmpty(panel.QuickGroupName) ? Framework.Lang.T("快捷方式") : panel.QuickGroupName;
                return string.IsNullOrEmpty(panel.Title) ? groupName : panel.Title;
            }

            if (panel.Type == PanelType.Plugin)
            {
                // 插件：尝试解析插件名称
                string pluginName = Framework.Lang.T("插件");
                if (!string.IsNullOrEmpty(panel.PluginId))
                {
                    var p = Services.Plugins.PluginLoaderService.Instance.GetPlugin(panel.PluginId);
                    if (p != null) pluginName = p.Config.Name;
                }
                return string.IsNullOrEmpty(panel.Title) ? pluginName : panel.Title;
            }

            return string.IsNullOrEmpty(panel.Title) ? typeName : panel.Title;
        }

        /// <summary>PanelType → 中文类型名（与「添加模块」菜单的标准命名一致）</summary>
        private static string GetPanelTypeName(PanelType type) => type switch
        {
            PanelType.QuickGroup => Framework.Lang.T("快捷方式"),
            PanelType.StickyNotes => Framework.Lang.T("便签"),
            PanelType.Clipboard => Framework.Lang.T("剪贴板收藏"),
            PanelType.SystemMonitor => Framework.Lang.T("系统信息"),
            PanelType.UsageStats => Framework.Lang.T("使用统计"),
            PanelType.AIChat => Framework.Lang.T("AI 对话"),
            PanelType.FileArea => Framework.Lang.T("文件区"),
            PanelType.ClipboardHistory => Framework.Lang.T("剪贴板历史"),
            PanelType.DataDashboard => Framework.Lang.T("数据看板"),
            PanelType.Timer => Framework.Lang.T("计时器"),
            PanelType.Group => Framework.Lang.T("分组"),
            PanelType.TodoList => Framework.Lang.T("今日待办"),
            PanelType.SignalLight => Framework.Lang.T("信号灯"),
            PanelType.DigitalEmployee => Framework.Lang.T("数字员工"),
            PanelType.Friend => Framework.Lang.T("好友"),
            PanelType.ImageCarousel => Framework.Lang.T("图片轮播"),
            PanelType.DailyActivity => Framework.Lang.T("每日活跃"),
            PanelType.Calendar => Framework.Lang.T("日历"),
            PanelType.Plugin => Framework.Lang.T("插件"),
            PanelType.ScreenTime => Framework.Lang.T("屏幕时长"),
            PanelType.Countdown => Framework.Lang.T("倒计时"),
            PanelType.Pomodoro => Framework.Lang.T("番茄闹钟"),
            _ => type.ToString()
        };

        private void WindowDelete_Click(WindowRowTag tag)
        {
            var result = MessageBox.Show(Framework.Lang.T("确定要删除该窗口吗？"), Framework.Lang.T("删除确认"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var app = (App)Application.Current;
            if (!string.IsNullOrEmpty(tag.LocalId))
            {
                app.WindowConfigs.Remove(tag.LocalId);
                // 删除已同步两侧 dock，广播刷新所有窗口的顶部标签
                app.RefreshAllWindowTabs();
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

            // 分享模式选择对话框（跟随主题）
            var dialog = new System.Windows.Window
            {
                Title = Framework.Lang.T("创建分享"),
                Width = 320,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var radioStyle = (Style)FindResource("DarkRadioButtonStyle");
            var checkStyle = (Style)FindResource("DarkCheckBoxStyle");

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("选择分享模式："),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            var copyRadio = new RadioButton { Content = Framework.Lang.T("复制（对方获得独立副本）"), Style = radioStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var refRadio = new RadioButton { Content = Framework.Lang.T("引用（对方看到你的实时数据）"), Style = radioStyle, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(copyRadio);
            panel.Children.Add(refRadio);

            var permLabel = new TextBlock
            {
                Text = Framework.Lang.T("导入者权限："),
                FontSize = 12,
                Foreground = (Brush)FindResource("BrushTextSec"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(permLabel);
            var cbEdit = new CheckBox { Content = Framework.Lang.T("允许编辑"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var cbDelete = new CheckBox { Content = Framework.Lang.T("允许删除"), Style = checkStyle, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            var cbReShare = new CheckBox { Content = Framework.Lang.T("允许再分享"), Style = checkStyle, IsChecked = false, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(cbEdit);
            panel.Children.Add(cbDelete);
            panel.Children.Add(cbReShare);

            var confirmBtn = new Button
            {
                Content = Framework.Lang.T("创建分享链接"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            panel.Children.Add(confirmBtn);
            dialog.Content = panel;

            // 分享模式与权限联动：
            // 复制模式 —— 对方获得独立副本，权限固定为全部允许，整个权限区隐藏；
            // 引用模式 —— 显示权限区，逐项可配，默认全部不选中
            void ApplyShareMode(bool isCopy)
            {
                var vis = isCopy ? Visibility.Collapsed : Visibility.Visible;
                permLabel.Visibility = cbEdit.Visibility = cbDelete.Visibility = cbReShare.Visibility = vis;
                cbEdit.IsChecked = cbDelete.IsChecked = cbReShare.IsChecked = isCopy;
            }
            copyRadio.Checked += (_, _) => ApplyShareMode(true);
            refRadio.Checked += (_, _) => ApplyShareMode(false);
            ApplyShareMode(true);

            confirmBtn.Click += async (_, _) =>
            {
                string mode = copyRadio.IsChecked == true ? "copy" : "reference";
                try
                {
                    var app = (App)Application.Current;
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        barServerId = serverId,
                        shareMode = mode,
                        allowEdit = cbEdit.IsChecked == true,
                        allowDelete = cbDelete.IsChecked == true,
                        allowReShare = cbReShare.IsChecked == true
                    });
                    using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/create") { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (root["code"]?.ToObject<int>() == 200)
                    {
                        string code = root["data"]?["shareCode"]?.ToString() ?? "";
                        dialog.Close();
                        ThemedMessageBox.ShowShareResult(
                            this,
                            code,
                            $"{Constants.URLs.FrontendBaseUrl}/s/{code}"
                        );
                    }
                    else
                    {
                        MessageBox.Show(root["msg"]?.ToString() ?? Framework.Lang.T("创建分享失败"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Framework.Lang.TF("创建分享失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>导入窗口按钮点击</summary>
        private async void ImportWindow_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Window
            {
                Title = Framework.Lang.T("导入窗口"),
                Width = 340, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = Framework.Lang.T("输入分享短码或链接："), FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

            var inputBox = new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(inputBox);

            var confirmBtn = new Button
            {
                Content = Framework.Lang.T("导入"),
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
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/import") { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + app.AppSettings.AccountToken);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
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
                            Name = Framework.Lang.T("导入窗口"),
                            ServerId = barServerId,
                            ShareMode = shareMode,
                            SourceBarId = sourceBarId,
                            OwnerType = "personal"
                        };
                        app.WindowConfigs.AddToAllDocks(newWin);
                        // 新小屏同步进入两侧 dock，广播刷新所有窗口的顶部标签
                        app.RefreshAllWindowTabs();

                        dialog.Close();
                        MessageBox.Show($"导入成功！窗口已添加到你的小屏列表。\n模式：{(shareMode == "copy" ? "复制" : "引用")}", Framework.Lang.T("导入成功"), MessageBoxButton.OK, MessageBoxImage.Information);
                        _ = RefreshWindowListAsync();
                    }
                    else
                    {
                        MessageBox.Show(root["msg"]?.ToString() ?? Framework.Lang.T("导入失败"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Framework.Lang.TF("导入失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>我的分享：打开独立窗口列出正在分享的项，可逐项取消分享</summary>
        private void MyShares_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            var token = app.AppSettings.AccountToken;
            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show(Framework.Lang.T("请先登录"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new System.Windows.Window
            {
                Title = Framework.Lang.T("我的分享"),
                Width = 620, Height = 480,
                MinWidth = 520, MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)FindResource("BrushDialogBG"),
                Foreground = (Brush)FindResource("BrushTextMain")
            };
            win.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(win, App.IsCurrentThemeLight());

            var dock = new DockPanel { Margin = new Thickness(16) };
            var header = new TextBlock
            {
                Text = Framework.Lang.T("正在分享的项目"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);

            var listPanel = new StackPanel();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Content = listPanel };
            dock.Children.Add(header);
            dock.Children.Add(scroll);
            win.Content = dock;

            // 构造单行：标题 + 详情 + 取消分享按钮
            System.Windows.UIElement BuildRow(Newtonsoft.Json.Linq.JToken s)
            {
                string shareId = s["shareId"]?.ToString() ?? "";
                bool isItem = s["shareLevel"]?.ToString() == "item";
                string title = isItem
                    ? (s["itemTitle"]?.ToString() ?? Framework.Lang.T("未命名模块"))
                    : (s["barTitle"]?.ToString() ?? Framework.Lang.T("未命名窗口"));
                string mode = s["shareMode"]?.ToString() == "copy" ? Framework.Lang.T("复制") : Framework.Lang.T("引用");
                string level = isItem ? Framework.Lang.T("模块") : Framework.Lang.T("窗口");
                string code = s["shareCode"]?.ToString() ?? "";
                var maxUses = s["maxUses"]?.ToObject<int>() ?? 0;
                string usesText = maxUses == 0 ? "∞" : maxUses.ToString();
                string used = s["usedCount"]?.ToString() ?? "0";
                string views = s["viewCount"]?.ToString() ?? "0";
                int importerCount = s["importerCount"]?.ToObject<int>() ?? 0;
                string created = s["createdAt"]?.ToString() ?? "";
                if (DateTime.TryParse(created, out var dt)) created = dt.ToString("yyyy-MM-dd HH:mm");

                var border = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("BrushInputBG"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                border.Child = grid;

                var info = new StackPanel();
                Grid.SetColumn(info, 0);
                info.Children.Add(new TextBlock
                {
                    Text = $"{title}  [{level}·{mode}]",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.TF("短码: {0}    使用: {1}/{2}    查看: {3}    接收者: {4}    创建: {5}", code, used, usesText, views, importerCount, created),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                grid.Children.Add(info);

                var cancelBtn = new Button
                {
                    Content = Framework.Lang.T("取消分享"),
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Padding = new Thickness(12, 4, 12, 4),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(cancelBtn, 1);
                grid.Children.Add(cancelBtn);

                cancelBtn.Click += async (_, _) =>
                {
                    // 复制模式的副本已归属接收者本人，取消仅失效链接；引用模式才会连带清除导入记录
                    string confirmMsg = mode == "copy"
                        ? Framework.Lang.TF("确定取消分享“{0}”吗？\n取消后短码失效，接收者已导入的副本将保留。", title)
                        : Framework.Lang.TF("确定取消分享“{0}”吗？\n取消后短码失效，所有接收者的导入记录也会被清除。", title);
                    var confirm = MessageBox.Show(confirmMsg, Framework.Lang.T("取消分享"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.OK) return;

                    cancelBtn.IsEnabled = false;
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        using var req = new System.Net.Http.HttpRequestMessage(
                            System.Net.Http.HttpMethod.Delete, $"{Constants.URLs.ServerBaseUrl}/api/sidebar/share/{shareId}");
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        using var resp = await Services.HttpLog.SendAsync(http, req);
                        string json = await resp.Content.ReadAsStringAsync();
                        var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                        if (root["code"]?.ToObject<int>() == 200)
                        {
                            listPanel.Children.Remove(border);
                            if (listPanel.Children.Count == 0)
                            {
                                listPanel.Children.Add(new TextBlock
                                {
                                    Text = Framework.Lang.T("暂无分享记录"),
                                    FontSize = 13,
                                    Foreground = (Brush)FindResource("BrushTextMuted"),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    Margin = new Thickness(0, 40, 0, 0)
                                });
                            }
                        }
                        else
                        {
                            MessageBox.Show(root["msg"]?.ToString() ?? Framework.Lang.T("取消分享失败"), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            cancelBtn.IsEnabled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Framework.Lang.TF("取消分享失败: {0}", ex.Message), Framework.Lang.T("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                        cancelBtn.IsEnabled = true;
                    }
                };

                return border;
            }

            // 拉取列表并渲染
            win.Loaded += async (_, _) =>
            {
                listPanel.Children.Add(new TextBlock { Text = Framework.Lang.T("加载中..."), FontSize = 13, Foreground = (Brush)FindResource("BrushTextMuted") });
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get, Constants.URLs.ServerBaseUrl + "/api/sidebar/share/list/mine");
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    using var resp = await Services.HttpLog.SendAsync(http, req);
                    string json = await resp.Content.ReadAsStringAsync();
                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                    listPanel.Children.Clear();
                    if (root["code"]?.ToObject<int>() == 200 && root["data"] is Newtonsoft.Json.Linq.JArray shares)
                    {
                        if (shares.Count == 0)
                        {
                            listPanel.Children.Add(new TextBlock
                            {
                                Text = Framework.Lang.T("暂无分享记录"),
                                FontSize = 13,
                                Foreground = (Brush)FindResource("BrushTextMuted"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Thickness(0, 40, 0, 0)
                            });
                            return;
                        }
                        foreach (var s in shares)
                            listPanel.Children.Add(BuildRow(s));
                    }
                    else
                    {
                        listPanel.Children.Add(new TextBlock
                        {
                            Text = root["msg"]?.ToString() ?? Framework.Lang.T("获取分享列表失败"),
                            FontSize = 13
                        });
                    }
                }
                catch (Exception ex)
                {
                    listPanel.Children.Clear();
                    listPanel.Children.Add(new TextBlock { Text = Framework.Lang.TF("获取分享列表失败: {0}", ex.Message), FontSize = 13 });
                }
            };

            win.Show();
        }

        #endregion
    }
}
