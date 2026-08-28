using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>
    /// 设置-开发者页-API 密钥管理：创建/列表/删除。
    /// 完整 Key 仅创建成功时展示一次（后端只存哈希），列表只显示前缀。
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _apiKeyLoading;

        private bool IsLoggedInForApiKey => !string.IsNullOrEmpty(_app.AppSettings.AccountToken);

        /// <summary>切换到开发者页时调用：未登录显示提示，已登录拉取密钥列表</summary>
        private void RefreshApiKeyPage()
        {
            var loggedIn = IsLoggedInForApiKey;
            apiKeyLoginHint.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
            apiKeyCreateRow.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            if (!loggedIn)
            {
                apiKeyListBox.Children.Clear();
                apiKeyResultBox.Visibility = Visibility.Collapsed;
                apiKeyEmptyText.Visibility = Visibility.Collapsed;
                return;
            }
            _ = LoadApiKeysAsync();
        }

        /// <summary>拉取密钥列表并重建行（失败/空态用提示文本区分）</summary>
        private async Task LoadApiKeysAsync()
        {
            if (_apiKeyLoading) return;
            _apiKeyLoading = true;
            try
            {
                var keys = await ApiKeyService.Instance.ListAsync();
                apiKeyListBox.Children.Clear();
                if (keys == null)
                {
                    apiKeyEmptyText.Text = Framework.Lang.T("settings.apiKey.loadFailedRetry");
                    apiKeyEmptyText.Visibility = Visibility.Visible;
                    return;
                }
                if (keys.Count == 0)
                {
                    apiKeyEmptyText.Text = Framework.Lang.T("settings.apiKey.noKeysHint");
                    apiKeyEmptyText.Visibility = Visibility.Visible;
                    return;
                }
                apiKeyEmptyText.Visibility = Visibility.Collapsed;
                // 团队名称由后端 list 接口直接带出（客户端侧团队 ID 为 encrypt2 密文，本地无法解析数字 ID）
                foreach (var k in keys) apiKeyListBox.Children.Add(BuildApiKeyRow(k));
            }
            finally
            {
                _apiKeyLoading = false;
            }
        }

        /// <summary>构建单个密钥行：备注 + 前缀，副标题为归属/范围/有效期/最近使用，右侧删除按钮</summary>
        private Border BuildApiKeyRow(ApiKeyInfo key)
        {
            var title = new TextBlock
            {
                Text = $"{key.Name}  {key.Prefix}••••••••",
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
            };

            var parts = new List<string>();
            parts.Add(key.CorpId != null
                ? (!string.IsNullOrEmpty(key.CorpName)
                    ? Framework.Lang.T("common.team") + "：" + key.CorpName
                    : Framework.Lang.T("common.team") + " #" + key.CorpId)
                : Framework.Lang.T("common.personal"));
            if (!string.IsNullOrEmpty(key.Scope)) parts.Add(key.Scope);
            parts.Add(key.ExpiresAt != null
                ? $"{Framework.Lang.T("settings.apiKey.expires")} {key.ExpiresAt:yyyy-MM-dd}"
                : Framework.Lang.T("settings.apiKey.neverExpires"));
            parts.Add(key.LastUsedAt != null
                ? $"{Framework.Lang.T("settings.apiKey.lastUsed")} {key.LastUsedAt:yyyy-MM-dd HH:mm}"
                : Framework.Lang.T("settings.apiKey.neverUsed"));
            var sub = new TextBlock
            {
                Text = string.Join(" · ", parts),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            };

            // 团队密钥：显示 corpId 密文（外部程序调接口时传的就是它），点击复制
            TextBlock? corpCodeText = null;
            if (!string.IsNullOrEmpty(key.CorpCode))
            {
                corpCodeText = new TextBlock
                {
                    Text = "corpId: " + key.CorpCode,
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = Framework.Lang.T("settings.apiKey.copyCorpIdHint"),
                    Tag = key.CorpCode,
                };
                corpCodeText.MouseLeftButtonUp += ApiKeyCorpCode_Click;
            }

            var deleteBtn = new Button
            {
                Content = Framework.Lang.T("common.delete"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                Tag = key.Id,
            };
            deleteBtn.Click += ApiKeyDelete_Click;

            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(deleteBtn, Dock.Right);
            panel.Children.Add(deleteBtn);
            var textCol = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { title, sub },
            };
            if (corpCodeText != null) textCol.Children.Add(corpCodeText);
            panel.Children.Add(textCol);

            return new Border
            {
                Child = panel,
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 8),
            };
        }

        /// <summary>添加密钥：弹对话框输入名称并选择归属（个人/团队），成功后展示完整 Key（仅此一次）并刷新列表</summary>
        private async void ApiKeyCreate_Click(object sender, RoutedEventArgs e)
        {
            var teams = await TeamService.Instance.ListTeamsAsync() ?? new List<Team>();
            var dlg = new ApiKeyCreateDialog(teams) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var name = dlg.KeyName;
            if (name == "") name = Framework.Lang.T("settings.apiKey.title"); // 留空时用默认备注
            var corpId = dlg.CorpId;

            apiKeyCreateBtn.IsEnabled = false;
            try
            {
                var (ok, msg, key) = await ApiKeyService.Instance.CreateAsync(name, corpId);
                if (!ok)
                {
                    ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("settings.apiKey.createFailed") : msg, Framework.Lang.T("common.notice"));
                    return;
                }
                apiKeyResultText.Text = key;
                apiKeyResultBox.Visibility = Visibility.Visible;
                _ = LoadApiKeysAsync();
            }
            finally
            {
                apiKeyCreateBtn.IsEnabled = true;
            }
        }

        /// <summary>点击团队密钥行的 corpId 密文：复制到剪贴板（外部程序调接口时传此值）</summary>
        private async void ApiKeyCorpCode_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBlock tb || tb.Tag is not string code || string.IsNullOrEmpty(code)) return;
            try { Clipboard.SetText(code); } catch { return; }
            var origin = tb.Text;
            tb.Text = Framework.Lang.T("settings.copiedCheck");
            await Task.Delay(1200);
            tb.Text = origin;
        }

        /// <summary>复制完整密钥到剪贴板</summary>
        private async void ApiKeyCopy_Click(object sender, RoutedEventArgs e)
        {
            var key = apiKeyResultText.Text;
            if (string.IsNullOrEmpty(key)) return;
            try { Clipboard.SetText(key); } catch { return; }
            var origin = apiKeyCopyBtn.Content;
            apiKeyCopyBtn.Content = Framework.Lang.T("common.copied");
            await Task.Delay(1500);
            apiKeyCopyBtn.Content = origin;
        }

        /// <summary>删除密钥：确认后调用后端接口，立即失效</summary>
        private async void ApiKeyDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int id) return;
            if (!ThemedMessageBox.Confirm(this,
                    Framework.Lang.T("settings.apiKey.deleteConfirm"),
                    Framework.Lang.T("settings.apiKey.deleteKey"))) return;

            btn.IsEnabled = false;
            var (ok, msg) = await ApiKeyService.Instance.DeleteAsync(id);
            if (!ok)
            {
                ThemedMessageBox.Show(this, string.IsNullOrEmpty(msg) ? Framework.Lang.T("common.deletionFailed") : msg, Framework.Lang.T("common.notice"));
                btn.IsEnabled = true;
                return;
            }
            _ = LoadApiKeysAsync();
        }
    }
}
