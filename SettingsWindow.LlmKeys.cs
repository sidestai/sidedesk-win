using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>
    /// 设置-大模型页：管理多组大模型 API Key（本地保存；登录且开启云同步时同步到云端）。
    /// UI 全部代码构建：首次进入懒构建，之后只刷新列表与同步状态提示。
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _llmPageBuilt;
        private StackPanel _llmListBox = null!;
        private TextBlock _llmEmptyText = null!;
        private TextBlock _llmSyncHint = null!;
        private Border _llmFormBox = null!;
        private ComboBox _llmProviderCombo = null!;
        private TextBox _llmNameBox = null!;
        private TextBox _llmKeyBox = null!;
        private TextBox _llmBaseUrlBox = null!;
        private TextBox _llmModelIdBox = null!;
        private TextBlock _llmKeyUrlLink = null!;
        private TextBlock _llmFormStatus = null!;
        private Button _llmSaveBtn = null!;
        private bool _llmComboSuppress;
        /// <summary>当前编辑的服务商 id；null 表示新增</summary>
        private string? _llmEditingProvider;
        private static readonly HttpClient _llmHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>登录且开启云同步（此时 Key 会备份到云端）</summary>
        private bool LlmSyncEnabled => _app.AppSettings.EnableCloudSync && !string.IsNullOrEmpty(_app.AppSettings.AccountToken);

        /// <summary>当前语言为英文时隐藏中国厂商（下拉与列表均不展示）</summary>
        private static bool LlmHideCn => Framework.Lang.Current == Framework.Lang.En;

        // ==================== 页面构建与刷新 ====================

        /// <summary>切换到「大模型」页时调用：首次懒构建，之后刷新列表与同步提示</summary>
        private void RefreshLlmKeysPage()
        {
            if (!_llmPageBuilt) BuildLlmKeysPage();
            _llmSyncHint.Text = LlmSyncEnabled
                ? Framework.Lang.T("settings.llm.pageHint")
                : Framework.Lang.T("settings.llm.localOnlyHint");
            RebuildLlmKeyList();
        }

        private void BuildLlmKeysPage()
        {
            _llmPageBuilt = true;
            pageLlmKeys.Children.Clear();

            // 标题与说明
            pageLlmKeys.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "\U0001F916 " + Framework.Lang.T("settings.llm.models"),
                FontSize = (double)FindResource("FontSizeLarge"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain")
            });
            _llmSyncHint = new TextBlock
            {
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            pageLlmKeys.Children.Add(_llmSyncHint);

            // 已配置列表区
            pageLlmKeys.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("settings.llm.configured"),
                FontSize = (double)FindResource("FontSizeNormal"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrushTextMain"),
                Margin = new Thickness(0, 16, 0, 0)
            });
            _llmListBox = new StackPanel();
            pageLlmKeys.Children.Add(_llmListBox);
            _llmEmptyText = new TextBlock
            {
                Text = Framework.Lang.T("settings.llm.emptyHint"),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = (double)FindResource("FontSizeSmall"),
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            pageLlmKeys.Children.Add(_llmEmptyText);

            var addBtn = new Button
            {
                Content = Framework.Lang.T("settings.llm.addKey"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand
            };
            addBtn.Click += (s, e) => ShowLlmKeyForm(null);
            pageLlmKeys.Children.Add(addBtn);

            // 添加/编辑表单（默认隐藏）
            pageLlmKeys.Children.Add(BuildLlmKeyForm());
        }

        /// <summary>重建已配置 Key 列表（保持预设顺序；英文语言下隐藏中国厂商）</summary>
        private void RebuildLlmKeyList()
        {
            _llmListBox.Children.Clear();
            var configured = AIConfigManager.GetConfiguredModels();
            int shown = 0;
            foreach (var m in configured)
            {
                if (LlmHideCn && m.Region == "cn") continue;
                _llmListBox.Children.Add(BuildLlmKeyRow(m));
                shown++;
            }
            _llmEmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>单个 Key 行：厂商名 + 掩码 + 同步/使用中标记，右侧删除/使用/编辑按钮</summary>
        private Border BuildLlmKeyRow(AIModelInfo m)
        {
            var cfg = AIConfigManager.GetConfig();
            string key = AIConfigManager.GetApiKeyFor(m.Id);
            string prefix = key.Length >= 8 ? key[..8] : key;

            var title = new Emoji.Wpf.TextBlock
            {
                Text = m.Name,
                Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 13,
                FontWeight = FontWeights.Medium
            };

            var subParts = new List<string> { prefix + "••••••••" };
            var serverId = AIConfigManager.GetServerKeyId(m.Id);
            if (serverId != null)
                subParts.Add("☁ " + Framework.Lang.T("settings.llm.cloudOnly"));
            else if (m.Id != "custom" && LlmSyncEnabled)
                subParts.Add(Framework.Lang.T("settings.llm.notSynced"));
            if (cfg.SelectedModelId == m.Id)
                subParts.Add("✅ " + Framework.Lang.T("settings.llm.inUse"));
            var sub = new TextBlock
            {
                Text = string.Join(" · ", subParts),
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var deleteBtn = new Button
            {
                Content = Framework.Lang.T("common.delete"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                Tag = m.Id
            };
            deleteBtn.Click += LlmKeyDelete_Click;

            var useBtn = new Button
            {
                Content = Framework.Lang.T("settings.llm.useIt"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Tag = m.Id,
                Visibility = cfg.SelectedModelId == m.Id ? Visibility.Collapsed : Visibility.Visible
            };
            useBtn.Click += LlmKeyUse_Click;

            var editBtn = new Button
            {
                Content = Framework.Lang.T("common.edit"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Tag = m.Id
            };
            editBtn.Click += LlmKeyEdit_Click;

            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(deleteBtn, Dock.Right);
            DockPanel.SetDock(useBtn, Dock.Right);
            DockPanel.SetDock(editBtn, Dock.Right);
            panel.Children.Add(deleteBtn);
            panel.Children.Add(useBtn);
            panel.Children.Add(editBtn);
            panel.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { title, sub }
            });

            return new Border
            {
                Child = panel,
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 8)
            };
        }

        // ==================== 添加/编辑表单 ====================

        private Border BuildLlmKeyForm()
        {
            var form = new StackPanel();

            form.Children.Add(BuildLlmFormLabel(Framework.Lang.T("settings.llm.provider")));
            _llmProviderCombo = new ComboBox
            {
                Style = (Style)FindResource("GlobalComboBoxStyle"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            _llmProviderCombo.SelectionChanged += (s, e) => { if (!_llmComboSuppress) OnLlmProviderChanged(); };
            form.Children.Add(_llmProviderCombo);

            form.Children.Add(BuildLlmFormLabel(Framework.Lang.T("common.name")));
            _llmNameBox = new TextBox
            {
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            form.Children.Add(_llmNameBox);

            form.Children.Add(BuildLlmFormLabel(Framework.Lang.T("settings.llm.apiKey")));
            _llmKeyBox = new TextBox
            {
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            form.Children.Add(_llmKeyBox);

            // 官方 Key 申请地址（可点击）
            _llmKeyUrlLink = new TextBlock
            {
                Foreground = (Brush)FindResource("BrushAccent"),
                FontSize = 11,
                Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed
            };
            _llmKeyUrlLink.MouseLeftButtonUp += (s, e) =>
            {
                var url = _llmKeyUrlLink.Tag as string;
                if (!string.IsNullOrEmpty(url))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                }
            };
            form.Children.Add(_llmKeyUrlLink);

            form.Children.Add(BuildLlmFormLabel(Framework.Lang.T("settings.llm.baseUrl")));
            _llmBaseUrlBox = new TextBox
            {
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            form.Children.Add(_llmBaseUrlBox);

            form.Children.Add(BuildLlmFormLabel(Framework.Lang.T("settings.llm.modelId")));
            _llmModelIdBox = new TextBox
            {
                Style = (Style)FindResource("GlobalTextBoxStyle"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            form.Children.Add(_llmModelIdBox);

            _llmFormStatus = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            form.Children.Add(_llmFormStatus);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            _llmSaveBtn = new Button
            {
                Content = Framework.Lang.T("common.save"),
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Padding = new Thickness(16, 5, 16, 5),
                Cursor = Cursors.Hand
            };
            _llmSaveBtn.Click += async (s, e) => await SaveLlmKeyAsync();
            btnRow.Children.Add(_llmSaveBtn);
            var cancelBtn = new Button
            {
                Content = Framework.Lang.T("common.cancel"),
                Style = (Style)FindResource("StandardButtonStyle"),
                Padding = new Thickness(16, 5, 16, 5),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (s, e) => _llmFormBox.Visibility = Visibility.Collapsed;
            btnRow.Children.Add(cancelBtn);
            form.Children.Add(btnRow);

            _llmFormBox = new Border
            {
                Child = form,
                Background = (Brush)FindResource("BrushInputBG"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 14, 0, 0),
                Visibility = Visibility.Collapsed
            };
            return _llmFormBox;
        }

        private TextBlock BuildLlmFormLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("BrushTextSec"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };

        /// <summary>打开表单：providerId=null 为新增，否则编辑预填</summary>
        private void ShowLlmKeyForm(string? providerId)
        {
            _llmEditingProvider = providerId;
            SetLlmFormStatus(null, false);
            _llmNameBox.Text = "";
            _llmKeyBox.Text = "";

            // 重建厂商下拉（分组：中国 / 国际 / 自定义；英文语言下隐藏中国厂商组）
            _llmComboSuppress = true;
            _llmProviderCombo.Items.Clear();
            if (!LlmHideCn)
            {
                AddLlmProviderGroupHeader(Framework.Lang.T("settings.llm.regionCn"));
                foreach (var m in AIConfigManager.SupportedModels)
                    if (m.Region == "cn") _llmProviderCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
            }
            AddLlmProviderGroupHeader(Framework.Lang.T("settings.llm.regionIntl"));
            foreach (var m in AIConfigManager.SupportedModels)
                if (m.Region == "intl") _llmProviderCombo.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
            AddLlmProviderGroupHeader(Framework.Lang.T("common.custom"));
            _llmProviderCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("common.custom"), Tag = "custom" });

            // 选中目标厂商（编辑时预填；新增默认选中第一个可见厂商）
            string target = providerId ?? (LlmHideCn ? "openai" : "deepseek");
            for (int i = 0; i < _llmProviderCombo.Items.Count; i++)
            {
                if (_llmProviderCombo.Items[i] is ComboBoxItem ci && ci.Tag as string == target)
                {
                    _llmProviderCombo.SelectedIndex = i;
                    break;
                }
            }
            _llmComboSuppress = false;
            OnLlmProviderChanged();

            if (providerId != null)
            {
                var model = AIConfigManager.SupportedModels.Find(m => m.Id == providerId);
                if (model != null)
                {
                    if (providerId == "custom") _llmNameBox.Text = model.Name == Framework.Lang.T("common.custom") ? "" : model.Name;
                    _llmKeyBox.Text = AIConfigManager.GetApiKeyFor(providerId);
                    _llmModelIdBox.Text = model.DefaultModelId ?? "";
                }
            }

            _llmFormBox.Visibility = Visibility.Visible;
            _llmKeyBox.Focus();
        }

        private void AddLlmProviderGroupHeader(string text)
        {
            _llmProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = "── " + text + " ──",
                IsEnabled = false,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                FontSize = 11
            });
        }

        /// <summary>厂商切换：带出默认接入地址/模型 ID/申请地址；自定义时开放接入地址编辑</summary>
        private void OnLlmProviderChanged()
        {
            var sel = _llmProviderCombo.SelectedItem as ComboBoxItem;
            string? id = sel?.Tag as string;
            var model = id == null ? null : AIConfigManager.SupportedModels.Find(m => m.Id == id);
            bool isCustom = id == "custom";

            // 编辑模式下切换厂商视为重新填写该厂商
            if (_llmEditingProvider != null && id != _llmEditingProvider)
            {
                _llmNameBox.Text = "";
                _llmKeyBox.Text = "";
            }

            _llmBaseUrlBox.Text = model?.ApiBaseUrl ?? "";
            _llmBaseUrlBox.IsReadOnly = !isCustom;
            if (!isCustom || _llmEditingProvider == null || string.IsNullOrEmpty(_llmModelIdBox.Text))
                _llmModelIdBox.Text = model?.DefaultModelId ?? "";

            var keyUrl = model?.ApiKeyUrl;
            if (!string.IsNullOrEmpty(keyUrl))
            {
                _llmKeyUrlLink.Text = Framework.Lang.T("settings.llm.getKeyAt") + ": " + keyUrl;
                _llmKeyUrlLink.Tag = keyUrl;
                _llmKeyUrlLink.Visibility = Visibility.Visible;
            }
            else
            {
                _llmKeyUrlLink.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>表单状态行（成功/失败提示，数秒后自动隐藏）</summary>
        private async void SetLlmFormStatus(string? text, bool isError)
        {
            if (string.IsNullOrEmpty(text))
            {
                _llmFormStatus.Visibility = Visibility.Collapsed;
                return;
            }
            _llmFormStatus.Text = text;
            _llmFormStatus.Foreground = isError ? Brushes.IndianRed : (Brush)FindResource("BrushAccent");
            _llmFormStatus.Visibility = Visibility.Visible;
            await Task.Delay(4000);
            if (_llmFormStatus.Text == text)
                _llmFormStatus.Visibility = Visibility.Collapsed;
        }

        // ==================== 保存/删除/使用 ====================

        /// <summary>保存：本地落库；登录且开启云同步时同步到云端（编辑时先删旧云端记录再保存）</summary>
        private async Task SaveLlmKeyAsync()
        {
            var sel = _llmProviderCombo.SelectedItem as ComboBoxItem;
            string? providerId = sel?.Tag as string;
            if (string.IsNullOrEmpty(providerId)) return;

            string apiKey = _llmKeyBox.Text.Trim();
            string baseUrl = _llmBaseUrlBox.Text.Trim();
            string modelId = _llmModelIdBox.Text.Trim();
            string name = _llmNameBox.Text.Trim();
            bool isCustom = providerId == "custom";
            if (string.IsNullOrEmpty(apiKey))
            {
                SetLlmFormStatus(Framework.Lang.T("settings.llm.apiKeyPlaceholder"), true);
                return;
            }
            if (isCustom && (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(modelId)))
            {
                SetLlmFormStatus(Framework.Lang.T("settings.llm.baseUrl") + " / " + Framework.Lang.T("settings.llm.modelId"), true);
                return;
            }

            _llmSaveBtn.IsEnabled = false;
            try
            {
                var preset = AIConfigManager.SupportedModels.Find(m => m.Id == providerId);
                if (isCustom)
                {
                    // 自定义厂商：覆盖 custom 定义（本机单条自定义）
                    AIConfigManager.AddOrUpdateModel(new AIModelInfo
                    {
                        Id = "custom",
                        Name = string.IsNullOrEmpty(name) ? Framework.Lang.T("common.custom") : name,
                        ApiBaseUrl = baseUrl,
                        DefaultModelId = modelId,
                        Region = "custom"
                    });
                }
                else if (preset != null && (!string.IsNullOrEmpty(modelId) && modelId != preset.DefaultModelId))
                {
                    // 用户改了默认模型 ID：更新定义（本机生效）
                    AIConfigManager.AddOrUpdateModel(new AIModelInfo
                    {
                        Id = preset.Id,
                        Name = preset.Name,
                        ApiBaseUrl = preset.ApiBaseUrl,
                        DefaultModelId = modelId,
                        Region = preset.Region,
                        ApiKeyUrl = preset.ApiKeyUrl
                    });
                }

                // 本地保存 Key
                var cfg = AIConfigManager.GetConfig();
                if (cfg.ApiKeys == null) cfg.ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                cfg.ApiKeys[providerId] = apiKey;
                if (string.IsNullOrEmpty(cfg.SelectedModelId)) cfg.SelectedModelId = providerId;
                AIConfigManager.SaveConfig(cfg);

                // 云同步（自定义厂商服务端不支持，仅存本机）
                bool synced = false;
                if (LlmSyncEnabled && !isCustom)
                {
                    string? oldId = AIConfigManager.GetServerKeyId(providerId);
                    if (oldId != null) await DeleteLlmTokenAsync(oldId);
                    var (ok, newId) = await SaveLlmTokenAsync(providerId, name, apiKey);
                    if (ok && !string.IsNullOrEmpty(newId))
                    {
                        AIConfigManager.SetServerKeyId(providerId, newId);
                        synced = true;
                    }
                }

                _llmFormBox.Visibility = Visibility.Collapsed;
                RebuildLlmKeyList();
                ThemedMessageBox.Show(this,
                    synced ? Framework.Lang.T("settings.llm.syncedToCloud") : Framework.Lang.T(LlmSyncEnabled && !isCustom ? "settings.llm.syncFailed" : "settings.llm.savedLocal"),
                    Framework.Lang.T("common.notice"));
            }
            finally
            {
                _llmSaveBtn.IsEnabled = true;
            }
        }

        /// <summary>编辑：预填现有配置打开表单</summary>
        private void LlmKeyEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            ShowLlmKeyForm(id);
        }

        /// <summary>用于 AI 对话：设为当前选中服务商；云端有记录时同时激活（供服务端代理使用）</summary>
        private async void LlmKeyUse_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            var cfg = AIConfigManager.GetConfig();
            cfg.SelectedModelId = id;
            AIConfigManager.SaveConfig(cfg);
            var serverId = AIConfigManager.GetServerKeyId(id);
            if (serverId != null) await ActivateLlmTokenAsync(serverId);
            RebuildLlmKeyList();
        }

        /// <summary>删除：确认后移除本地定义与 Key；有云端记录时同步删除</summary>
        private async void LlmKeyDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string id) return;
            if (!ThemedMessageBox.Confirm(this,
                    Framework.Lang.T("settings.llm.deleteConfirm"),
                    Framework.Lang.T("common.notice"))) return;

            btn.IsEnabled = false;
            var serverId = AIConfigManager.GetServerKeyId(id);
            AIConfigManager.RemoveModel(id);
            bool cloudDeleted = serverId != null && await DeleteLlmTokenAsync(serverId);
            RebuildLlmKeyList();
            if (cloudDeleted)
                ThemedMessageBox.Show(this, Framework.Lang.T("settings.llm.deletedFromCloud"), Framework.Lang.T("common.notice"));
        }

        // ==================== 云端接口 ====================

        private async Task<(bool ok, JObject? data)> LlmTokenPostAsync(string path, JObject body)
        {
            try
            {
                string url = $"{Constants.URLs.ServerBaseUrl}/api/sidebar/ai-token/{path}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (_app.AppSettings.AccountToken ?? ""));
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await HttpLog.SendAsync(_llmHttp, req);
                string json = await resp.Content.ReadAsStringAsync();
                if (JObject.Parse(json) is JObject obj && (int?)obj["code"] == 200)
                    return (true, obj["data"] as JObject);
                return (false, null);
            }
            catch
            {
                return (false, null);
            }
        }

        /// <summary>保存 token 到云端，返回服务端记录 id</summary>
        private async Task<(bool ok, string? id)> SaveLlmTokenAsync(string provider, string? name, string apiKey)
        {
            var body = new JObject
            {
                ["provider"] = provider,
                ["apiKey"] = apiKey
            };
            if (!string.IsNullOrEmpty(name)) body["name"] = name;
            var (ok, data) = await LlmTokenPostAsync("save", body);
            string? id = data?["id"]?.ToString();
            return (ok && !string.IsNullOrEmpty(id), id);
        }

        private async Task<bool> DeleteLlmTokenAsync(string serverId)
        {
            var (ok, _) = await LlmTokenPostAsync("delete", new JObject { ["id"] = serverId });
            return ok;
        }

        private async Task<bool> ActivateLlmTokenAsync(string serverId)
        {
            var (ok, _) = await LlmTokenPostAsync("activate", new JObject { ["id"] = serverId });
            return ok;
        }
    }
}
