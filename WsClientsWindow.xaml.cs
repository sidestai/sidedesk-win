using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    public partial class WsClientsWindow : Window
    {
        private readonly App _app;

        public WsClientsWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this, App.IsCurrentThemeLight());
            Loaded += (s, e) =>
            {
                WebSocketServerService.Instance.ClientInfoUpdated += OnClientInfoUpdated;
                WebSocketServerService.Instance.ClientConnected += _ => Dispatcher.Invoke(RefreshList);
                WebSocketServerService.Instance.ClientDisconnected += _ => Dispatcher.Invoke(RefreshList);
                RefreshList();
            };
            Closed += (s, e) =>
            {
                WebSocketServerService.Instance.ClientInfoUpdated -= OnClientInfoUpdated;
            };
        }

        private void OnClientInfoUpdated()
        {
            Dispatcher.Invoke(RefreshList);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void RefreshList()
        {
            clientListPanel.Children.Clear();

            var clients = WebSocketServerService.Instance.GetConnectedClients();
            countText.Text = Framework.Lang.TF("共 {0} 个连接", clients.Count);

            if (clients.Count == 0)
            {
                clientListPanel.Children.Add(new TextBlock
                {
                    Text = Framework.Lang.T("暂无连接的客户端"),
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontSize = 13,
                    Margin = new Thickness(0, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            // 收集所有数据看板项的 TargetUrl 用于匹配
            var dashboardMap = BuildDashboardUrlMap();

            foreach (var client in clients)
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

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 左侧信息区
                var infoPanel = new StackPanel();

                // 标题行
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                var titleText = new Emoji.Wpf.TextBlock
                {
                    Text = string.IsNullOrEmpty(client.Title) ? Framework.Lang.T("(未注册标题)") : client.Title,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("BrushTextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 350
                };
                titleRow.Children.Add(titleText);

                // 关联状态标签
                var matchedDashboard = FindMatchingDashboard(client.Url, dashboardMap);
                if (matchedDashboard != null)
                {
                    var tag = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock
                        {
                            Text = matchedDashboard,
                            FontSize = 11,
                            Foreground = (Brush)FindResource("BrushAccentText")
                        }
                    };
                    titleRow.Children.Add(tag);
                }
                else
                {
                    var tag = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock
                        {
                            Text = Framework.Lang.T("未关联"),
                            FontSize = 11,
                            Foreground = (Brush)FindResource("BrushAccentText")
                        }
                    };
                    titleRow.Children.Add(tag);
                }

                infoPanel.Children.Add(titleRow);

                // URL
                infoPanel.Children.Add(new TextBlock
                {
                    Text = client.Url,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrushTextSec"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 450,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = client.Url
                });

                // 元数据行
                var metaText = $"UniqID: {client.UniqId}  |  Tab ID: {(client.TabId?.ToString() ?? "N/A")}  |  连接时间: {client.ConnectedAt:HH:mm:ss}";
                infoPanel.Children.Add(new TextBlock
                {
                    Text = metaText,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    Margin = new Thickness(0, 4, 0, 0)
                });

                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

                // 右侧操作区
                var actionPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                if (matchedDashboard == null)
                {
                    var closeBtn = new Button
                    {
                        Content = Framework.Lang.T("关闭标签"),
                        Style = (Style)FindResource("GhostButtonStyle"),
                        Padding = new Thickness(10, 5, 10, 5),
                        FontSize = 12,
                        Tag = client.Id
                    };
                    closeBtn.Click += CloseTab_Click;
                    actionPanel.Children.Add(closeBtn);
                }
                else
                {
                    actionPanel.Children.Add(new TextBlock
                    {
                        Text = Framework.Lang.T("已关联"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                        HorizontalAlignment = HorizontalAlignment.Right
                    });
                }

                Grid.SetColumn(actionPanel, 1);
                grid.Children.Add(actionPanel);

                card.Child = grid;
                clientListPanel.Children.Add(card);
            }
        }

        /// <summary>
        /// 构建 URL → 数据看板标签 的映射表
        /// </summary>
        private System.Collections.Generic.Dictionary<string, string> BuildDashboardUrlMap()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var configs = _app.WindowConfigs.Windows;

            foreach (var win in configs)
            {
                foreach (var panel in win.Panels)
                {
                    if (panel.DashboardItems == null) continue;
                    foreach (var item in panel.DashboardItems)
                    {
                        if (!string.IsNullOrEmpty(item.TargetUrl) && !map.ContainsKey(item.TargetUrl))
                        {
                            map[item.TargetUrl] = item.Label;
                        }
                    }
                    // 也检查分组内的子面板
                    if (panel.ChildPanels != null)
                    {
                        foreach (var child in panel.ChildPanels)
                        {
                            if (child.DashboardItems == null) continue;
                            foreach (var item in child.DashboardItems)
                            {
                                if (!string.IsNullOrEmpty(item.TargetUrl) && !map.ContainsKey(item.TargetUrl))
                                {
                                    map[item.TargetUrl] = item.Label;
                                }
                            }
                        }
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// 根据客户端 URL 查找匹配的数据看板标签
        /// </summary>
        private string? FindMatchingDashboard(string clientUrl, System.Collections.Generic.Dictionary<string, string> dashboardMap)
        {
            if (string.IsNullOrEmpty(clientUrl)) return null;

            foreach (var kvp in dashboardMap)
            {
                try
                {
                    var target = new Uri(kvp.Key, UriKind.RelativeOrAbsolute);
                    var client = new Uri(clientUrl, UriKind.RelativeOrAbsolute);
                    if (client.IsAbsoluteUri && target.IsAbsoluteUri)
                    {
                        if (client.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) &&
                            client.AbsolutePath.StartsWith(target.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                        {
                            return kvp.Value;
                        }
                    }
                }
                catch { }

                // 简单字符串前缀匹配
                if (clientUrl.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        private async void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid clientId)
            {
                _app.CloseWsClientTab(clientId);
                // 延迟刷新以等待关闭生效
                await System.Threading.Tasks.Task.Delay(1000);
                RefreshList();
            }
        }
    }
}
