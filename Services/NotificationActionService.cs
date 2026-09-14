using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 通知动作执行器：所有 Action 均异步执行（Task.Run），UI 线程只负责关闭窗口和动画。
    /// </summary>
    public static class NotificationActionService
    {
        /// <summary>
        /// 异步执行通知动作。
        /// </summary>
        /// <param name="action">要执行的动作</param>
        /// <param name="context">动作所属的通知载荷（snooze 等需要上下文）</param>
        public static async Task ExecuteAsync(NotificationAction action, NotificationPayload? context = null)
        {
            try
            {
                // 点过即已读：本地标记 + 回传服务端（红点随之更新）
                NotificationSyncService.Instance.MarkRead(context);

                // 免打扰标识（如欢迎引导）：任一入口点过即永久抑制，避免重启后同条通知再以未读复现
                if (context != null && !string.IsNullOrEmpty(context.NoticeKey)
                    && Application.Current is App app)
                {
                    app.AppSettings.DismissNotice(context.NoticeKey);
                    app.AppSettings.Save();
                }

                switch (action.ActionType)
                {
                    case NotificationActionType.OpenUrl:
                        await Task.Run(() => OpenUrl(action.GetPayloadString("url")));
                        break;

                    case NotificationActionType.OpenWebView:
                        OpenWebView(action);
                        break;

                    case NotificationActionType.OpenSettings:
                        OpenSettings(action);
                        break;

                    case NotificationActionType.OpenFile:
                        await Task.Run(() => OpenFile(action.GetPayloadString("filePath",
                            action.GetPayloadString("path"))));
                        break;

                    case NotificationActionType.ShowFileLocation:
                        await Task.Run(() => ShowFileLocation(action.GetPayloadString("filePath",
                            action.GetPayloadString("path"))));
                        break;

                    case NotificationActionType.ShowMessagebox:
                        ShowMessagebox(action, context);
                        break;

                    case NotificationActionType.RunCommand:
                        await RunCommandAsync(action);
                        break;

                    case NotificationActionType.Snooze:
                        if (context != null)
                        {
                            var minutes = action.GetPayloadNumber("snoozeInterval", 5);
                            NotificationService.Instance.Snooze(context, minutes);
                        }
                        break;

                    case NotificationActionType.InputReply:
                        ShowInputReply(action, context);
                        break;

                    case NotificationActionType.TeamApplyHandle:
                        await HandleTeamApplyAsync(action);
                        break;

                    case NotificationActionType.Dismiss:
                        // 关闭通知由 UI 层处理，此处无需额外操作
                        break;

                    default:
                        Debug.WriteLine($"[NotificationAction] unknown action type: {action.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationAction] action {action.Type} error: {ex.Message}");
            }
        }

        /// <summary>打开网页</summary>
        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>在独立窗口内嵌 WebView2 打开网页（payload: url / title）</summary>
        private static void OpenWebView(NotificationAction action)
        {
            var url = action.GetPayloadString("url");
            if (string.IsNullOrWhiteSpace(url)) return;
            var title = action.GetPayloadString("title", Framework.Lang.T("common.web"));

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                new SideDesk.Windows.WebViewWindow(url, title).Show();
            });
        }

        /// <summary>打开内部设置页（支持 path/tab 参数定位页面）</summary>
        private static void OpenSettings(NotificationAction action)
        {
            var page = action.GetPayloadString("path", action.GetPayloadString("tab"));
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var app = (App)Application.Current;
                app.OpenSettings(settingsWin =>
                {
                    if (!string.IsNullOrEmpty(page))
                        settingsWin.NavigateToPage(page);
                });
            });
        }

        /// <summary>
        /// 团队加入申请 / 邀请的审批（payload: applicationId / kind=join|invite / decision=accept|reject）。
        /// 成功后清除通知中心里的对应条目（未读红点随之更新）并刷新设置窗团队页；失败弹出后端提示语。
        /// </summary>
        private static async Task HandleTeamApplyAsync(NotificationAction action)
        {
            var applicationId = action.GetPayloadString("applicationId");
            if (string.IsNullOrWhiteSpace(applicationId)) return;

            bool isInvite = string.Equals(action.GetPayloadString("kind", "join"), "invite", StringComparison.OrdinalIgnoreCase);
            var decision = action.GetPayloadString("decision", "accept");
            bool accepted = string.Equals(decision, "accept", StringComparison.OrdinalIgnoreCase);

            var (ok, msg) = isInvite
                ? await TeamService.Instance.HandleInvitationAsync(applicationId, decision)
                : await TeamService.Instance.HandleApplicationAsync(applicationId, decision);

            if (!ok)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    ThemedMessageBox.Show(null,
                        string.IsNullOrEmpty(msg) ? Framework.Lang.T("common.operationFailed") : msg,
                        Framework.Lang.T("common.notice")));
                return;
            }

            Debug.WriteLine($"[NotificationAction] team {(isInvite ? "invite" : "apply")} {applicationId} {decision}ed");
            TeamNotificationService.Instance.RemoveForApplication(applicationId);
            // 接受邀请才改变本人成员关系（需重建团队切换下拉）；管理员审批他人申请只刷新团队页列表
            (Application.Current as App)?.NotifySettingsTeamChanged(membershipChanged: isInvite && accepted);
        }

        /// <summary>打开本地文件或文件夹</summary>
        private static void OpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }

        /// <summary>打开文件夹并选中指定文件</summary>
        private static void ShowFileLocation(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = false
            });
        }

        /// <summary>弹出消息框（按钮列表来自 payload.buttons，点击按钮递归执行对应 action）</summary>
        private static void ShowMessagebox(NotificationAction action, NotificationPayload? context)
        {
            var title = action.GetPayloadString("title", context?.Title ?? Framework.Lang.T("common.notice"));
            var message = action.GetPayloadString("message", context?.Body ?? "");

            // 解析子按钮列表
            var subButtons = new List<NotificationAction>();
            if (action.Payload != null && action.Payload.TryGetValue("buttons", out var btnVal))
            {
                if (btnVal is Newtonsoft.Json.Linq.JArray btnArray)
                {
                    foreach (var btn in btnArray)
                    {
                        var sub = NotificationAction.FromJson(btn);
                        if (sub != null) subButtons.Add(sub);
                    }
                }
            }

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (subButtons.Count == 0)
                {
                    ThemedMessageBox.Show(null, message, title);
                    return;
                }

                // 带自定义按钮的消息框
                var dialog = new Window
                {
                    Title = title,
                    Width = 360,
                    MinHeight = 150,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true
                };

                var root = new Border
                {
                    Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                    BorderBrush = (Brush)Application.Current.FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10)
                };

                var stack = new StackPanel { Margin = new Thickness(20) };

                stack.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = title,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    Margin = new Thickness(0, 0, 0, 12)
                });

                stack.Children.Add(new Emoji.Wpf.TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                });

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                foreach (var sub in subButtons)
                {
                    var btn = new Button
                    {
                        Content = sub.Label ?? sub.Type,
                        MinWidth = 70,
                        MinHeight = 28,
                        Margin = new Thickness(8, 0, 0, 0),
                        Style = (Style)Application.Current.FindResource("StandardButtonStyle")
                    };
                    var capturedAction = sub;
                    btn.Click += async (s, e) =>
                    {
                        dialog.Close();
                        await ExecuteAsync(capturedAction, context);
                    };
                    btnPanel.Children.Add(btn);
                }

                stack.Children.Add(btnPanel);
                root.Child = stack;
                dialog.Content = root;

                // 支持拖动
                root.MouseLeftButtonDown += (s, e) => { try { dialog.DragMove(); } catch { } };

                dialog.Show();
            });
        }

        /// <summary>执行系统命令（全程异步，不阻塞 UI）</summary>
        private static async Task RunCommandAsync(NotificationAction action)
        {
            var command = action.GetPayloadString("command");
            if (string.IsNullOrWhiteSpace(command)) return;

            var runInBackground = action.GetPayloadBool("runInBackground", true);

            await Task.Run(async () =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        CreateNoWindow = runInBackground,
                        RedirectStandardOutput = !runInBackground,
                        RedirectStandardError = !runInBackground
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        Debug.WriteLine($"[NotificationAction] command finished (exit={process.ExitCode}): {command}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NotificationAction] command execution failed: {ex.Message}");
                }
            });
        }

        /// <summary>快速输入框回复（类似 Slack 通知直接回复）</summary>
        private static void ShowInputReply(NotificationAction action, NotificationPayload? context)
        {
            var placeholder = action.GetPayloadString("placeholder", Framework.Lang.T("notification.typeReplyPlaceholder"));
            var replyActionId = action.GetPayloadString("replyActionId", action.Type);

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Window
                {
                    Title = Framework.Lang.T("settings.quickReply"),
                    Width = 320,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true
                };

                var root = new Border
                {
                    Background = (Brush)Application.Current.FindResource("BrushDialogBG"),
                    BorderBrush = (Brush)Application.Current.FindResource("BrushBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10)
                };

                var stack = new StackPanel { Margin = new Thickness(16) };

                if (!string.IsNullOrEmpty(context?.Title))
                {
                    stack.Children.Add(new Emoji.Wpf.TextBlock
                    {
                        Text = context.Title,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                        Margin = new Thickness(0, 0, 0, 10)
                    });
                }

                var inputBox = new TextBox
                {
                    FontSize = 12,
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = (Brush)Application.Current.FindResource("BrushInputBG"),
                    Foreground = (Brush)Application.Current.FindResource("BrushTextMain"),
                    BorderBrush = (Brush)Application.Current.FindResource("BrushBorder")
                };
                inputBox.ToolTip = placeholder;
                stack.Children.Add(inputBox);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var sendBtn = new Button
                {
                    Content = Framework.Lang.T("common.send"),
                    MinWidth = 60,
                    MinHeight = 26,
                    IsDefault = true,
                    Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
                };

                void DoSend()
                {
                    var text = inputBox.Text.Trim();
                    if (string.IsNullOrEmpty(text)) return;
                    dialog.Close();

                    // 异步回传回复到后端
                    _ = BackendWebSocketService.Instance.SendAsync(
                        Newtonsoft.Json.JsonConvert.SerializeObject(new
                        {
                            @event = "notification_reply",
                            data = new
                            {
                                reply_action_id = replyActionId,
                                text,
                                notification_id = context?.Id ?? ""
                            }
                        }));
                }

                sendBtn.Click += (s, e) => DoSend();
                inputBox.KeyDown += (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter) DoSend();
                };

                var cancelBtn = new Button
                {
                    Content = Framework.Lang.T("common.cancel"),
                    MinWidth = 60,
                    MinHeight = 26,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsCancel = true,
                    Style = (Style)Application.Current.FindResource("StandardButtonStyle")
                };
                cancelBtn.Click += (s, e) => dialog.Close();

                btnPanel.Children.Add(sendBtn);
                btnPanel.Children.Add(cancelBtn);
                stack.Children.Add(btnPanel);

                root.Child = stack;
                dialog.Content = root;
                root.MouseLeftButtonDown += (s, e) => { try { dialog.DragMove(); } catch { } };

                dialog.Show();
                inputBox.Focus();
            });
        }
    }
}
