using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
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
        private Microsoft.Web.WebView2.Wpf.WebView2 _webView = null;
        private UIElement _inputPanel = null;
        private Button _sendBtn = null;
        private bool _webModeReady = false;
        private const string WebModeTag = "__web__";
        private IntPtr _escHookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc? _escHookProc;
        private HttpClient _httpClient = new HttpClient();
        private List<Dictionary<string, string>> _messages = new List<Dictionary<string, string>>();
        private bool _isProcessing = false;
        private Window _owner = null;

        public bool IsOpen => _dialog != null && _dialog.IsLoaded;

        public void Show(Window owner, double left, double top, double width, double height, string submitText = null)
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
                WindowStyle = WindowStyle.None,   // 自定义标题栏（支持彩色 emoji）
                Owner = owner
            };
            // 无边框窗口保留原生边缘拖拽调整大小；CaptionHeight=0 由自定义标题栏自行接管拖动
            WindowChrome.SetWindowChrome(_dialog, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(5),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0, 0, 0, 1)
            });
            _dialog.Loaded += (s, e) => HideFromTaskbar(_dialog);
            _dialog.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(_dialog, App.IsCurrentThemeLight());

            _dialog.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain");

            // 根容器：自绘边框 + 背景（跟随主窗口主题）
            var root = new System.Windows.Controls.Border
            {
                BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBorder"),
                BorderThickness = new Thickness(1)
            };
            root.SetBinding(System.Windows.Controls.Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { Source = owner });

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题栏
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ===== 自定义标题栏：emoji 标题 + 拖动 + 关闭 =====
            var titleBar = new Grid { Background = System.Windows.Media.Brushes.Transparent };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button) return; // 点在关闭按钮上时不拖动
                if (e.ClickCount == 2) return;
                if (e.LeftButton == MouseButtonState.Pressed) _dialog.DragMove();
            };
            var titleText = new Emoji.Wpf.TextBlock
            {
                Text = "🤖 AI Chat",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 8, 0, 2)
            };
            var closeBtn = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                Cursor = Cursors.Hand,
                ToolTip = Framework.Lang.T("common.close"),
                // 用矢量 Path 画 ✕，不依赖字体字形渲染
                Content = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                    Stroke = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                    StrokeThickness = 1.4,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    Width = 8,
                    Height = 8
                }
            };
            closeBtn.Click += (s, e) => _dialog.Close();
            titleBar.Children.Add(titleText);
            titleBar.Children.Add(closeBtn);
            Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

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
            // 下拉仅显示已配置的服务商 + 网页版，未配置的隐藏
            bool startInWebMode = RefreshModelCombo(null);

            _modelCombo.SelectionChanged += (s, ev) =>
            {
                var selected = _modelCombo.SelectedItem as ComboBoxItem;
                if (selected == null) return;
                if (Equals(selected.Tag, WebModeTag))
                {
                    SwitchToWebMode();
                }
                else
                {
                    var cfg = AIConfigManager.GetConfig();
                    cfg.SelectedModelId = selected.Tag.ToString();
                    AIConfigManager.SaveConfig(cfg);
                    SwitchToClientMode();
                }
            };
            DockPanel.SetDock(_modelCombo, Dock.Left);
            headerPanel.Children.Add(_modelCombo);

            var settingsBtn = new Button
            {
                Content = Framework.Lang.T("common.settings"),
                Width = 64,
                Height = 28,
                // StandardButtonStyle 默认 Padding=16,8，窄按钮下会把文字裁掉，这里局部覆盖
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("StandardButtonStyle")
            };
            settingsBtn.Click += (s, ev) => ShowSettingsDialog();
            DockPanel.SetDock(settingsBtn, Dock.Right);
            headerPanel.Children.Add(settingsBtn);

            _statusText = new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("common.ready"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            headerPanel.Children.Add(_statusText);

            Grid.SetRow(headerPanel, 1);
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

            // 网页版承载控件：切换到“网页版”时显示，无需本地配置但需登录
            _webView = new Microsoft.Web.WebView2.Wpf.WebView2 { Visibility = Visibility.Collapsed };

            var contentHost = new Grid();
            contentHost.Children.Add(_chatScrollViewer);
            contentHost.Children.Add(_webView);
            Grid.SetRow(contentHost, 2);
            grid.Children.Add(contentHost);

            // Input area
            var inputPanel = new DockPanel { Margin = new Thickness(5) };
            _inputPanel = inputPanel;
            var sendBtn = new Button
            {
                Content = Framework.Lang.T("common.send"),
                Width = 60,
                MinHeight = 34,
                Margin = new Thickness(5, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            _sendBtn = sendBtn;
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
            Grid.SetRow(inputPanel, 3);
            grid.Children.Add(inputPanel);

            root.Child = grid;
            _dialog.Content = root;

            // Send message handler
            Action sendMessage = async () => await SendMessageCore();

            sendBtn.Click += (s, ev) => sendMessage();
            _inputBox.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter)
                {
                    sendMessage();
                    ev.Handled = true;
                }
                // ESC 由窗口级 PreviewKeyDown 统一处理（关闭对话框）
            };

            // ESC：无论焦点在输入框还是其它控件，统一关闭对话框（网页版焦点在 WebView2 内时由 AcceleratorKeyPressed 兜底）
            _dialog.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Escape)
                {
                    ev.Handled = true;
                    _dialog.Close();
                }
            };

            _dialog.Closed += (s, ev) =>
            {
                UninstallEscapeHook();
                _dialog = null;
                _chatStack = null;
                _chatScrollViewer = null;
                _inputBox = null;
                _modelCombo = null;
                _statusText = null;
                _webView = null;
                _inputPanel = null;
                _sendBtn = null;
                _webModeReady = false;
            };

            _dialog.Show();
            InstallEscapeHook();
            if (startInWebMode) SwitchToWebMode();

            // 外部提交内容：打开后直接作为用户消息发送（网页版无法注入，忽略）
            if (!startInWebMode && !string.IsNullOrWhiteSpace(submitText))
            {
                _inputBox.Text = submitText;
                _ = SendMessageCore();
            }
        }

        /// <summary>向已打开的对话框提交并发送一条消息（仅本地模式有效）</summary>
        public async Task<bool> SubmitText(string text)
        {
            if (!IsOpen || _inputBox == null) return false;
            _dialog.Activate();
            _inputBox.Text = text ?? "";
            return await SendMessageCore();
        }

        // ===== ESC 关闭：窗口级钩子兜底网页版（焦点在 WebView2 内时 WPF 收不到按键） =====
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>安装低级键盘钩子：本对话框为前台窗口且光标落在其区域内时，ESC 关闭对话框并吞掉按键</summary>
        private void InstallEscapeHook()
        {
            if (_escHookId != IntPtr.Zero) return;
            _escHookProc = EscapeHookCallback;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;
            _escHookId = NativeMethods.SetWindowsHookEx(
                WH_KEYBOARD_LL, _escHookProc,
                NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
        }

        private void UninstallEscapeHook()
        {
            if (_escHookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_escHookId);
                _escHookId = IntPtr.Zero;
            }
        }

        private IntPtr EscapeHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_ESCAPE && IsCursorOverDialog())
                {
                    Window? dlg = _dialog;
                    dlg?.Dispatcher.BeginInvoke(() =>
                    {
                        try { dlg.Close(); } catch { }
                    });
                    return (IntPtr)1; // 吞掉按键，不再传给网页/应用
                }
            }
            return NativeMethods.CallNextHookEx(_escHookId, nCode, wParam, lParam);
        }

        /// <summary>本对话框为前台窗口且光标落在其窗口区域内</summary>
        private bool IsCursorOverDialog()
        {
            Window? dlg = _dialog;
            if (dlg == null) return false;
            var helper = new System.Windows.Interop.WindowInteropHelper(dlg);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero || NativeMethods.GetForegroundWindow() != hwnd) return false;
            if (!NativeMethods.GetCursorPos(out var pt)) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
            return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        }

        /// <summary>发送输入框当前内容；返回是否真正发起请求</summary>
        private async Task<bool> SendMessageCore()
        {
            string text = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || _isProcessing)
                return false;

            var cfg = AIConfigManager.GetConfig();
            if (string.IsNullOrEmpty(AIConfigManager.GetApiKeyFor(AIConfigManager.GetSelectedModel().Id)))
            {
                AddMessage("system", Framework.Lang.T("aiChat.configureApiKeyFirst"));
                return false;
            }

            AddMessage("user", text);
            _inputBox.Text = "";
            _inputBox.IsEnabled = false;
            _sendBtn.IsEnabled = false;
            _isProcessing = true;
            _statusText.Text = Framework.Lang.T("aiChat.thinking");

            var assistantMsg = AddMessage("assistant", "");
            _messages.Add(new Dictionary<string, string> { { "role", "user" }, { "content", text } });

            try
            {
                string response = await CallApiWithToolsAsync(cfg, assistantMsg);
                _messages.Add(new Dictionary<string, string> { { "role", "assistant" }, { "content", response } });
                UpdateMessage(assistantMsg, response);
                _statusText.Text = Framework.Lang.T("common.ready");
            }
            catch (Exception ex)
            {
                UpdateMessage(assistantMsg, Framework.Lang.T("common.requestFailed") + ex.Message);
                _statusText.Text = Framework.Lang.T("common.error");
            }
            finally
            {
                _inputBox.IsEnabled = true;
                _sendBtn.IsEnabled = true;
                _isProcessing = false;
                _inputBox.Focus();
            }
            return true;
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
                    prefix = Framework.Lang.T("aiChat.youPrefix");
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
            string apiKey = AIConfigManager.GetApiKeyFor(modelInfo.Id);
            string modelId = config.CustomModelId;
            if (string.IsNullOrEmpty(modelId))
                modelId = modelInfo.DefaultModelId;

            string url = apiBase + "/v1/chat/completions";
            // 注册表生效时（已登录）用服务端清单，否则内置工具（隐私模式）
            var tools = McpToolRegistryService.GetActiveToolDefinitions();

            System.Diagnostics.Debug.WriteLine($"[AIChat] request: POST {url}");
            System.Diagnostics.Debug.WriteLine($"[AIChat] model: {modelId}, ApiKey: {(string.IsNullOrEmpty(apiKey) ? "(empty)" : apiKey.Substring(0, Math.Min(6, apiKey.Length)) + "***")}");

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
                request.Headers.Add("Authorization", "Bearer " + apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[AIChat] request failed: {(int)response.StatusCode} {response.StatusCode}");
                    System.Diagnostics.Debug.WriteLine($"[AIChat] response body: {responseJson}");
                    throw new Exception($"{(int)response.StatusCode} {response.StatusCode} - {TruncateForDisplay(responseJson)}");
                }

                System.Diagnostics.Debug.WriteLine($"[AIChat] response ok, length: {responseJson.Length}");
                var responseObj = JObject.Parse(responseJson);
                var choice = responseObj["choices"]?[0];
                var message = choice?["message"];

                if (message == null)
                    return Framework.Lang.T("aiChat.noResponseContent");

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
                        _statusText.Text = Framework.Lang.TF("aiChat.callingTool", funcName);
                    System.Diagnostics.Debug.WriteLine($"[AIChat] tool call: {funcName}({funcArgs})");

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
                    _statusText.Text = Framework.Lang.T("aiChat.thinking");
            }

            return Framework.Lang.T("aiChat.toolCallRoundsExceeded");
        }

        private async Task<string> CallApiAsync(AIConfig config, List<Dictionary<string, string>> messages)
        {
            var modelInfo = AIConfigManager.GetSelectedModel();
            string apiBase = modelInfo.ApiBaseUrl;
            string apiKey = AIConfigManager.GetApiKeyFor(modelInfo.Id);
            string modelId = config.CustomModelId;
            if (string.IsNullOrEmpty(modelId))
                modelId = modelInfo.DefaultModelId;

            string url = apiBase + "/v1/chat/completions";
            System.Diagnostics.Debug.WriteLine($"[AIChat] CallApiAsync request: POST {url}, model: {modelId}");

            var requestBody = new
            {
                model = modelId,
                messages = messages,
                temperature = 0.7,
                max_tokens = 2048
            };

            string json = JsonConvert.SerializeObject(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", "Bearer " + apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[AIChat] CallApiAsync request failed: {(int)response.StatusCode} {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[AIChat] response body: {responseJson}");
                throw new Exception($"{(int)response.StatusCode} {response.StatusCode} - {TruncateForDisplay(responseJson)}");
            }

            var responseObj = JsonConvert.DeserializeObject<dynamic>(responseJson);

            if (responseObj.choices != null && responseObj.choices.Count > 0)
            {
                return responseObj.choices[0].message.content.ToString();
            }

            return Framework.Lang.T("aiChat.noResponseContent");
        }

        /// <summary>
        /// 服务商展示名：仅展示层翻译（“自定义”→ 既有键 common.custom）；
        /// AIModelInfo.Name 同时用于本地 HTTP 出口等数据场景，不改其数据值。
        /// </summary>
        private static string GetModelDisplayName(Models.AIModelInfo model)
            => model.Id == "custom" ? Framework.Lang.T("common.custom") : model.Name;

        /// <summary>重建服务商下拉：仅已配置的 + 网页版；返回是否选中网页版</summary>
        private bool RefreshModelCombo(string preferTag)
        {
            if (_modelCombo == null) return false;
            if (string.IsNullOrEmpty(preferTag))
                preferTag = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                            ?? AIConfigManager.GetConfig().SelectedModelId;

            _modelCombo.Items.Clear();
            foreach (var model in AIConfigManager.GetConfiguredModels())
                _modelCombo.Items.Add(new ComboBoxItem { Content = GetModelDisplayName(model), Tag = model.Id });
            _modelCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("aiChat.webVersionEmoji"), Tag = WebModeTag });

            for (int i = 0; i < _modelCombo.Items.Count; i++)
            {
                if (((ComboBoxItem)_modelCombo.Items[i]).Tag.ToString() == preferTag)
                {
                    _modelCombo.SelectedIndex = i;
                    return preferTag == WebModeTag;
                }
            }
            // 偏好项不在列表（未配置过）→ 回退网页版
            _modelCombo.SelectedIndex = _modelCombo.Items.Count - 1;
            return true;
        }

        /// <summary>切换到网页版：内嵌 WebView2 加载前端聊天页（无需本地配置，需登录）</summary>
        private async void SwitchToWebMode()
        {
            if (_webView == null) return;
            _chatScrollViewer.Visibility = Visibility.Collapsed;
            if (_inputPanel != null) _inputPanel.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            if (_statusText != null) _statusText.Text = Framework.Lang.T("common.webVersion");

            if (_webModeReady) return;
            _webModeReady = true;
            try
            {
                await _webView.EnsureCoreWebView2Async();
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // 与 AIChatWebWindow 一致的参数：proxy 本机代理 / token 登录态 / theme 宿主主题
                var app = (App)Application.Current;
                var proxy = $"http://127.0.0.1:{Services.HttpServerService.Instance.CurrentPort}";
                var token = app.AppSettings.AccountToken ?? "";
                var url = $"{Constants.URLs.FrontendBaseUrl}/#/ai-chat?proxy={Uri.EscapeDataString(proxy)}&token={Uri.EscapeDataString(token)}&theme={App.FrontendThemeParam}";
                _webView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIChat] web mode WebView2 init failed: {ex.Message}");
            }
        }

        /// <summary>切换回客户端内置对话（本地已配置的服务商）</summary>
        private void SwitchToClientMode()
        {
            if (_webView == null) return;
            _webView.Visibility = Visibility.Collapsed;
            if (_chatScrollViewer != null) _chatScrollViewer.Visibility = Visibility.Visible;
            if (_inputPanel != null) _inputPanel.Visibility = Visibility.Visible;
            if (_statusText != null) _statusText.Text = Framework.Lang.T("common.ready");
        }

        private void ShowSettingsDialog()
        {
            var dialog = new Window
            {
                Title = Framework.Lang.T("aiChat.aiSettings"),
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
                Text = Framework.Lang.T("aiChat.aiChatConfiguration"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Model Selection
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("aiChat.stepChooseProvider"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("aiChat.chooseProviderHint"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var modelCombo = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 0, 15), Style = (Style)Application.Current.FindResource("GlobalComboBoxStyle") };
            foreach (var model in AIConfigManager.SupportedModels)
            {
                modelCombo.Items.Add(new ComboBoxItem { Content = GetModelDisplayName(model), Tag = model.Id });
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
                Text = Framework.Lang.T("aiChat.stepEnterApiKey"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("aiChat.apiKeyHelp"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextSec"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var keyBox = new TextBox
            {
                Height = 30,
                Text = AIConfigManager.GetApiKeyFor(cfg.SelectedModelId ?? "deepseek"),
                Margin = new Thickness(0, 0, 0, 15),
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            mainPanel.Children.Add(keyBox);

            // 切换服务商时加载该服务商已配置的 Key（未配置显示空）
            modelCombo.SelectionChanged += (s, e) =>
            {
                var sel = modelCombo.SelectedItem as ComboBoxItem;
                if (sel != null) keyBox.Text = AIConfigManager.GetApiKeyFor(sel.Tag.ToString());
            };

            // Custom Model ID
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("aiChat.stepCustomModelId"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 10, 0, 5)
            });
            mainPanel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("aiChat.customModelIdHint"),
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
            var saveBtn = new Button { Content = Framework.Lang.T("common.save"), Width = 80, MinHeight = 32, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("PrimaryButtonStyle") };
            var cancelBtn = new Button { Content = Framework.Lang.T("common.cancel"), Width = 80, MinHeight = 32, Style = (Style)Application.Current.FindResource("StandardButtonStyle") };
            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(cancelBtn);
            mainPanel.Children.Add(btnPanel);

            scrollViewer.Content = mainPanel;
            dialog.Content = scrollViewer;

            saveBtn.Click += (s, ev) =>
            {
                var selected = modelCombo.SelectedItem as ComboBoxItem;
                cfg.SelectedModelId = selected != null ? selected.Tag.ToString() : "deepseek";
                // Key 按服务商保存；清空视为未配置，主下拉中隐藏
                if (cfg.ApiKeys == null) cfg.ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string newKey = keyBox.Text.Trim();
                if (string.IsNullOrEmpty(newKey))
                    cfg.ApiKeys.Remove(cfg.SelectedModelId);
                else
                    cfg.ApiKeys[cfg.SelectedModelId] = newKey;
                cfg.ApiKey = newKey; // 同步旧版单 Key 字段
                cfg.CustomModelId = customBox.Text.Trim();
                AIConfigManager.SaveConfig(cfg);

                // 重建主下拉并切换到对应模式
                if (RefreshModelCombo(cfg.SelectedModelId))
                    SwitchToWebMode();
                else
                    SwitchToClientMode();

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
            if (string.IsNullOrEmpty(text)) return Framework.Lang.T("aiChat.noResponseContentPlaceholder");
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }
    }
}
