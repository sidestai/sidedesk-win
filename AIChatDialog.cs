using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    public class AIChatDialog
    {
        private Window _dialog = null;
        private StackPanel _chatStack = null;
        private ScrollViewer _chatScrollViewer = null;
        private TextBox _inputBox = null;
        private ComboBox _modelCombo = null;
        private TextBlock _statusText = null;
        private HttpClient _httpClient = new HttpClient();
        private List<Dictionary<string, string>> _messages = new List<Dictionary<string, string>>();
        private bool _isProcessing = false;
        private Window _owner = null;

        public bool IsOpen => _dialog != null && _dialog.IsLoaded;

        public void Show(Window owner, double left, double top, double width, double height)
        {
            if (IsOpen)
            {
                _dialog.Activate();
                return;
            }

            _owner = owner;
            _messages.Clear();

            _dialog = new Window
            {
                Title = "AI Chat",
                Width = 500,
                Height = height,
                Left = left,  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                Top = top,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.CanResize,
                Owner = owner
            };
            _dialog.Loaded += (s, e) => HideFromTaskbar(_dialog);
            _dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(_dialog, App.IsCurrentThemeLight());

            _dialog.SetBinding(Window.BackgroundProperty, new System.Windows.Data.Binding("Background") { Source = owner });
            _dialog.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header: model selection + settings
            var headerPanel = new DockPanel { Margin = new Thickness(10, 8, 10, 4) };

            _modelCombo = new ComboBox
            {
                Width = 120,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle")
            };
            foreach (var model in AIConfigManager.SupportedModels)
            {
                _modelCombo.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
            }
            var config = AIConfigManager.GetConfig();
            for (int i = 0; i < _modelCombo.Items.Count; i++)
            {
                if (((ComboBoxItem)_modelCombo.Items[i]).Tag.ToString() == config.SelectedModelId)
                {
                    _modelCombo.SelectedIndex = i;
                    break;
                }
            }
            if (_modelCombo.SelectedIndex < 0)
                _modelCombo.SelectedIndex = 0;

            _modelCombo.SelectionChanged += (s, ev) =>
            {
                var selected = _modelCombo.SelectedItem as ComboBoxItem;
                if (selected != null)
                {
                    var cfg = AIConfigManager.GetConfig();
                    cfg.SelectedModelId = selected.Tag.ToString();
                    AIConfigManager.SaveConfig(cfg);
                }
            };
            DockPanel.SetDock(_modelCombo, Dock.Left);
            headerPanel.Children.Add(_modelCombo);

            var settingsBtn = new Button
            {
                Content = "设置",
                Width = 50,
                MinHeight = 26,
                Margin = new Thickness(0, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("StandardButtonStyle")
            };
            settingsBtn.Click += (s, ev) => ShowSettingsDialog();
            DockPanel.SetDock(settingsBtn, Dock.Right);
            headerPanel.Children.Add(settingsBtn);

            _statusText = new Emoji.Wpf.TextBlock
            {
                Text = "就绪",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            headerPanel.Children.Add(_statusText);

            Grid.SetRow(headerPanel, 0);
            grid.Children.Add(headerPanel);

            // Chat message area
            _chatScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain")
            };
            _chatStack = new StackPanel();
            _chatScrollViewer.Content = _chatStack;
            Grid.SetRow(_chatScrollViewer, 1);
            grid.Children.Add(_chatScrollViewer);

            // Input area
            var inputPanel = new DockPanel { Margin = new Thickness(5) };
            var sendBtn = new Button
            {
                Content = "发送",
                Width = 60,
                MinHeight = 34,
                Margin = new Thickness(5, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            DockPanel.SetDock(sendBtn, Dock.Right);
            inputPanel.Children.Add(sendBtn);

            _inputBox = new TextBox
            {
                Height = 34,
                FontSize = 14,
                Padding = new Thickness(5, 3, 5, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            inputPanel.Children.Add(_inputBox);
            Grid.SetRow(inputPanel, 2);
            grid.Children.Add(inputPanel);

            _dialog.Content = grid;

            // Send message handler
            Action sendMessage = async () =>
            {
                string text = _inputBox.Text.Trim();
                if (string.IsNullOrEmpty(text) || _isProcessing)
                    return;

                var cfg = AIConfigManager.GetConfig();
                if (string.IsNullOrEmpty(cfg.ApiKey))
                {
                    AddMessage("system", "请先点击【设置】配置 API Key");
                    return;
                }

                AddMessage("user", text);
                _inputBox.Text = "";
                _inputBox.IsEnabled = false;
                sendBtn.IsEnabled = false;
                _isProcessing = true;
                _statusText.Text = "思考中...";

                var assistantMsg = AddMessage("assistant", "");
                _messages.Add(new Dictionary<string, string> { { "role", "user" }, { "content", text } });

                try
                {
                    string response = await CallApiWithToolsAsync(cfg, assistantMsg);
                    _messages.Add(new Dictionary<string, string> { { "role", "assistant" }, { "content", response } });
                    UpdateMessage(assistantMsg, response);
                    _statusText.Text = "就绪";
                }
                catch (Exception ex)
                {
                    UpdateMessage(assistantMsg, "请求失败: " + ex.Message);
                    _statusText.Text = "错误";
                }
                finally
                {
                    _inputBox.IsEnabled = true;
                    sendBtn.IsEnabled = true;
                    _isProcessing = false;
                    _inputBox.Focus();
                }
            };

            sendBtn.Click += (s, ev) => sendMessage();
            _inputBox.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter)
                {
                    sendMessage();
                    ev.Handled = true;
                }
                else if (ev.Key == Key.Escape)
                {
                    _dialog.Close();
                }
            };

            _dialog.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Escape && _inputBox != null && !_inputBox.IsFocused)
                    _dialog.Close();
            };

            _dialog.Closed += (s, ev) =>
            {
                _dialog = null;
                _chatStack = null;
                _chatScrollViewer = null;
                _inputBox = null;
                _modelCombo = null;
                _statusText = null;
            };

            _dialog.Show();
        }

        public void Activate()
        {
            if (IsOpen)
                _dialog.Activate();
        }

        private void HideFromTaskbar(Window window)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var taskbarList = (ITaskbarList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090")));
                taskbarList.HrInit();
                taskbarList.DeleteTab(hwnd);
                Marshal.ReleaseComObject(taskbarList);
            }
            catch { }
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        }

        public void Close()
        {
            if (IsOpen)
                _dialog.Close();
        }

        public void SyncPosition(double left, double top, double width, double height)
        {
            if (IsOpen)
            {
                _dialog.Left = left;  // left 已经是 sidebar 右侧边缘的 X 坐标，直接使用
                _dialog.Top = top;
                _dialog.Height = height;
            }
        }

        public TextBlock AddMessage(string role, string message)
        {
            if (_chatStack == null)
                return null;

            var container = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            System.Windows.Media.Brush bgBrush;
            System.Windows.Media.Brush fgBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain");
            HorizontalAlignment hAlign;
            string prefix = "";

            switch (role)
            {
                case "user":
                    bgBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushAccent");
                    fgBrush = System.Windows.Media.Brushes.White;
                    hAlign = HorizontalAlignment.Right;
                    prefix = "你: ";
                    break;
                case "assistant":
                    bgBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushInputBG");
                    hAlign = HorizontalAlignment.Left;
                    prefix = "AI: ";
                    break;
                default:
                    bgBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushAccent");
                    fgBrush = System.Windows.Media.Brushes.White;
                    hAlign = HorizontalAlignment.Center;
                    break;
            }

            var msgBorder = new System.Windows.Controls.Border
            {
                Background = bgBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0),
                HorizontalAlignment = hAlign,
                MaxWidth = 380
            };

            var msgText = new Emoji.Wpf.TextBlock
            {
                Text = string.IsNullOrEmpty(message) ? "" : (prefix + message),
                Foreground = fgBrush,
                TextWrapping = TextWrapping.Wrap
            };

            msgBorder.Child = msgText;
            container.Children.Add(msgBorder);
            _chatStack.Children.Add(container);

            _chatScrollViewer?.ScrollToBottom();
            return msgText;
        }

        private void UpdateMessage(TextBlock msgText, string message)
        {
            if (msgText != null)
            {
                string text = msgText.Text;
                int colonIndex = text.IndexOf(':');
                if (colonIndex > 0)
                {
                    msgText.Text = text.Substring(0, colonIndex + 2) + message;
                }
                else
                {
                    msgText.Text = message;
                }
            }
            _chatScrollViewer?.ScrollToBottom();
        }

        private async Task<string> CallApiWithToolsAsync(AIConfig config, TextBlock statusBlock)
        {
            const int MaxToolRounds = 8;
            var modelInfo = AIConfigManager.GetSelectedModel();
            string apiBase = modelInfo.ApiBaseUrl;
            string modelId = config.CustomModelId;
            if (string.IsNullOrEmpty(modelId))
                modelId = modelInfo.DefaultModelId;

            string url = apiBase + "/v1/chat/completions";
            var tools = AIToolService.GetToolDefinitions();

            System.Diagnostics.Debug.WriteLine($"[AIChat] 请求: POST {url}");
            System.Diagnostics.Debug.WriteLine($"[AIChat] 模型: {modelId}, ApiKey: {(string.IsNullOrEmpty(config.ApiKey) ? "(空)" : config.ApiKey.Substring(0, Math.Min(6, config.ApiKey.Length)) + "***")}");

            // 构建带 system prompt 的消息列表
            var apiMessages = new List<Dictionary<string, object>>();
            apiMessages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", "你是元桌小屏(SideDesk)的内置AI助手。你可以通过工具调用来操作小屏应用，包括查看状态、切换主题、管理便签等。当用户请求操作应用时，请主动调用对应工具。删除便签时必须先向用户展示便签内容并获得确认，确认后才用 confirm=\"true\" 执行删除。回复请用中文。" }
            });
            foreach (var msg in _messages)
                apiMessages.Add(new Dictionary<string, object> { { "role", msg["role"] }, { "content", msg["content"] } });

            for (int round = 0; round < MaxToolRounds; round++)
            {
                var requestBody = new Dictionary<string, object>
                {
                    { "model", modelId },
                    { "messages", apiMessages },
                    { "temperature", 0.7 },
                    { "max_tokens", 2048 },
                    { "tools", tools }
                };

                string json = JsonConvert.SerializeObject(requestBody);
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", "Bearer " + config.ApiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[AIChat] 请求失败: {(int)response.StatusCode} {response.StatusCode}");
                    System.Diagnostics.Debug.WriteLine($"[AIChat] 响应体: {responseJson}");
                    throw new Exception($"{(int)response.StatusCode} {response.StatusCode} - {TruncateForDisplay(responseJson)}");
                }

                System.Diagnostics.Debug.WriteLine($"[AIChat] 响应成功, 长度: {responseJson.Length}");
                var responseObj = JObject.Parse(responseJson);
                var choice = responseObj["choices"]?[0];
                var message = choice?["message"];

                if (message == null)
                    return "无响应内容";

                var toolCalls = message["tool_calls"] as JArray;

                // 无工具调用 → 返回最终文本
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    return message["content"]?.ToString() ?? "";
                }

                // 有工具调用：把 assistant 消息（含 tool_calls）加入上下文
                apiMessages.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", message["content"]?.ToString() ?? "" },
                    { "tool_calls", toolCalls }
                });

                // 逐个执行工具
                foreach (var tc in toolCalls)
                {
                    var toolCallId = tc["id"]?.ToString() ?? "";
                    var funcName = tc["function"]?["name"]?.ToString() ?? "";
                    var funcArgs = tc["function"]?["arguments"]?.ToString() ?? "{}";

                    // UI 反馈
                    if (_statusText != null)
                        _statusText.Text = $"调用工具: {funcName}";
                    System.Diagnostics.Debug.WriteLine($"[AIChat] 工具调用: {funcName}({funcArgs})");

                    // 执行工具
                    string toolResult = AIToolService.ExecuteTool(funcName, funcArgs);

                    // 把工具结果加入上下文
                    apiMessages.Add(new Dictionary<string, object>
                    {
                        { "role", "tool" },
                        { "tool_call_id", toolCallId },
                        { "content", toolResult }
                    });
                }

                if (_statusText != null)
                    _statusText.Text = "思考中...";
            }

            return "工具调用轮次超过上限，已停止。";
        }

        private async Task<string> CallApiAsync(AIConfig config, List<Dictionary<string, string>> messages)
        {
            var modelInfo = AIConfigManager.GetSelectedModel();
            string apiBase = modelInfo.ApiBaseUrl;
            string modelId = config.CustomModelId;
            if (string.IsNullOrEmpty(modelId))
                modelId = modelInfo.DefaultModelId;

            string url = apiBase + "/v1/chat/completions";
            System.Diagnostics.Debug.WriteLine($"[AIChat] CallApiAsync 请求: POST {url}, 模型: {modelId}");

            var requestBody = new
            {
                model = modelId,
                messages = messages,
                temperature = 0.7,
                max_tokens = 2048
            };

            string json = JsonConvert.SerializeObject(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", "Bearer " + config.ApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[AIChat] CallApiAsync 请求失败: {(int)response.StatusCode} {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[AIChat] 响应体: {responseJson}");
                throw new Exception($"{(int)response.StatusCode} {response.StatusCode} - {TruncateForDisplay(responseJson)}");
            }

            var responseObj = JsonConvert.DeserializeObject<dynamic>(responseJson);

            if (responseObj.choices != null && responseObj.choices.Count > 0)
            {
                return responseObj.choices[0].message.content.ToString();
            }

            return "无响应内容";
        }

        private void ShowSettingsDialog()
        {
            var dialog = new Window
            {
                Title = "AI 设置",
                Width = 460,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = _dialog ?? _owner,
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain")
            };
            dialog.Loaded += (s, e) => HideFromTaskbar(dialog);
            dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(dialog, App.IsCurrentThemeLight());

            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var mainPanel = new StackPanel { Margin = new Thickness(20) };

            var cfg = AIConfigManager.GetConfig();

            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "AI 对话配置",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Model Selection
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "1. 选择模型服务商",
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "选择你要使用的 AI 服务商，DeepSeek 和 OpenAI 使用官方接口。",
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var modelCombo = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 0, 15), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            foreach (var model in AIConfigManager.SupportedModels)
            {
                modelCombo.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
            }
            for (int i = 0; i < modelCombo.Items.Count; i++)
            {
                if (((ComboBoxItem)modelCombo.Items[i]).Tag.ToString() == cfg.SelectedModelId)
                {
                    modelCombo.SelectedIndex = i;
                    break;
                }
            }
            mainPanel.Children.Add(modelCombo);

            // API Key
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "2. 填写 API Key",
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "从对应服务商官网获取 API Key 并粘贴到下方：\n  DeepSeek: https://platform.deepseek.com/api_keys\n  OpenAI: https://platform.openai.com/api-keys\n  通义千问: https://dashscope.console.aliyun.com/apiKey",
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var keyBox = new TextBox
            {
                Height = 30,
                Text = cfg.ApiKey ?? "",
                Margin = new Thickness(0, 0, 0, 15),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            mainPanel.Children.Add(keyBox);

            // Custom Model ID
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "3. 自定义模型 ID（可选）",
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = "只有选择【自定义】模型时才需要填写。例如：gpt-4、deepseek-chat、claude-3-opus 等。",
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var customBox = new TextBox
            {
                Height = 30,
                Text = cfg.CustomModelId ?? "",
                Margin = new Thickness(0, 0, 0, 20),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            mainPanel.Children.Add(customBox);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var saveBtn = new Button { Content = "保存", Width = 80, MinHeight = 32, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = "取消", Width = 80, MinHeight = 32, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(cancelBtn);
            mainPanel.Children.Add(btnPanel);

            scrollViewer.Content = mainPanel;
            dialog.Content = scrollViewer;

            saveBtn.Click += (s, ev) =>
            {
                var selected = modelCombo.SelectedItem as ComboBoxItem;
                cfg.SelectedModelId = selected != null ? selected.Tag.ToString() : "deepseek";
                cfg.ApiKey = keyBox.Text.Trim();
                cfg.CustomModelId = customBox.Text.Trim();
                AIConfigManager.SaveConfig(cfg);

                if (_modelCombo != null)
                {
                    for (int i = 0; i < _modelCombo.Items.Count; i++)
                    {
                        if (((ComboBoxItem)_modelCombo.Items[i]).Tag.ToString() == cfg.SelectedModelId)
                        {
                            _modelCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                dialog.Close();
            };

            cancelBtn.Click += (s, ev) => dialog.Close();

            dialog.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Escape)
                    dialog.Close();
            };

            dialog.ShowDialog();
        }

        /// <summary>截断过长的错误信息用于UI显示</summary>
        private static string TruncateForDisplay(string text, int maxLen = 200)
        {
            if (string.IsNullOrEmpty(text)) return "(无响应内容)";
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }
    }
}
