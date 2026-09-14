using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 高级项（扩展）动作执行引擎：解析 QuickItem.AdvancedConfigJson 后按 ActionType 分派执行。
    /// </summary>
    public static class AdvancedActionService
    {
        /// <summary>执行高级项动作（须在 UI 线程或内部自行切回 UI 线程）</summary>
        public static async Task ExecuteAsync(QuickItem item)
        {
            try
            {
                var cfg = AdvancedItemConfig.TryParse(item.AdvancedConfigJson);
                if (cfg == null)
                {
                    Notify(Framework.Lang.T("advanced.configInvalid"));
                    return;
                }

                // url/script/search 内容含 {query} 占位符时，取当前前台窗口的选中文字替换；
                // 无选中（含复制失败）时 url/script 替换为空、search 回退到输入弹窗。
                string? selectedText = null;
                bool needQuery =
                    (cfg.ActionType == "url" && (cfg.Url?.Contains("{query}") == true))
                    || (cfg.ActionType == "script" && (cfg.ScriptCode?.Contains("{query}") == true))
                    || (cfg.ActionType == "search" && (cfg.SearchTemplate?.Contains("{query}") == true));
                if (needQuery) selectedText = await GetSelectedTextAsync();

                switch (cfg.ActionType)
                {
                    case "url": await ExecuteUrlAsync(cfg, selectedText); break;
                    case "exec": ExecuteExec(cfg); break;
                    case "keys": await ExecuteKeysAsync(cfg); break;
                    case "paste": await ExecutePasteAsync(cfg); break;
                    case "script": await ExecuteScriptAsync(cfg, selectedText); break;
                    case "clipboard": ExecuteClipboard(cfg); break;
                    case "search": await ExecuteSearchAsync(cfg, selectedText); break;
                    default: Notify(Framework.Lang.T("advanced.configInvalid")); break;
                }
            }
            catch (Exception ex)
            {
                Notify(Framework.Lang.TF("advanced.execError", ex.Message));
            }
        }

        // ===== 选中文字采集 =====

        /// <summary>获取前台窗口当前选中文字：备份剪贴板 → 哨兵值 + 模拟 Ctrl+C → 读取 → 还原剪贴板。无选中/复制失败返回 null</summary>
        private static async Task<string?> GetSelectedTextAsync()
        {
            string? backup = null;
            try { if (Clipboard.ContainsText()) backup = Clipboard.GetText(); }
            catch { /* 剪贴板占用时忽略 */ }
            bool hadText = backup != null;

            try
            {
                string sentinel = $"__sd_adv_{Guid.NewGuid():N}__";
                Clipboard.SetText(sentinel);

                // Ctrl+C
                NativeMethods.keybd_event(0x11, 0, 0, UIntPtr.Zero);   // VK_CONTROL
                NativeMethods.keybd_event(0x43, 0, 0, UIntPtr.Zero);   // 'C'
                NativeMethods.keybd_event(0x43, 0, 2, UIntPtr.Zero);
                NativeMethods.keybd_event(0x11, 0, 2, UIntPtr.Zero);

                await Task.Delay(300);

                string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                return text == sentinel ? null : text;
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    if (hadText) Clipboard.SetText(backup!);
                    else Clipboard.Clear();
                }
                catch { /* 还原失败忽略 */ }
            }
        }

        // ===== url =====

        private static Task ExecuteUrlAsync(AdvancedItemConfig cfg, string? selectedText)
        {
            string url = cfg.Url?.Trim() ?? "";
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;

            // {query} → 当前选中文字（URL 编码），无选中时替换为空串避免占位符外泄到地址
            if (url.Contains("{query}", StringComparison.OrdinalIgnoreCase))
                url = url.Replace("{query}", Uri.EscapeDataString(selectedText ?? ""), StringComparison.OrdinalIgnoreCase);

            // 解析 Profile 目录：指定浏览器取 ProgramRegistry；默认浏览器取 BrowserProfileManager（前缀区分命名空间）
            string? userDataDir = null;
            if (!string.IsNullOrEmpty(cfg.ProfileId))
            {
                if (!string.IsNullOrEmpty(cfg.BrowserId))
                {
                    var profile = ((App)Application.Current).ProgramRegistry.GetProfileById(cfg.BrowserId, cfg.ProfileId);
                    userDataDir = profile?.Directory;
                }
                else if (cfg.ProfileId.StartsWith("legacy:", StringComparison.Ordinal))
                {
                    var entry = BrowserProfileManager.GetProfileById(cfg.ProfileId.Substring("legacy:".Length));
                    userDataDir = entry?.UserDataDir;
                }
            }

            // 指定浏览器（含默认浏览器的 Edge 回退：仅在需要 APP 模式/Profile 时）
            string? exePath = null;
            if (!string.IsNullOrEmpty(cfg.BrowserId))
            {
                var browser = ((App)Application.Current).ProgramRegistry.GetBrowserById(cfg.BrowserId);
                if (browser != null && File.Exists(browser.ExePath)) exePath = browser.ExePath;
            }
            else if (cfg.UseAppMode || userDataDir != null)
            {
                exePath = FindEdgePath();
            }

            if (exePath != null)
            {
                string args = cfg.UseAppMode ? $"--app={url}" : url;
                if (!string.IsNullOrEmpty(userDataDir))
                    args += $" --user-data-dir=\"{userDataDir}\"";

                var process = new Process();
                process.StartInfo.FileName = exePath;
                process.StartInfo.Arguments = args;
                process.Start();
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.CompletedTask;
        }

        /// <summary>查找 Edge 路径（默认浏览器下需要 APP 模式/Profile 时的回退，与 OpenQuickItem 逻辑一致）</summary>
        private static string? FindEdgePath()
        {
            var paths = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
            };
            return paths.FirstOrDefault(File.Exists);
        }

        // ===== exec =====

        private static void ExecuteExec(AdvancedItemConfig cfg)
        {
            string path = cfg.ProgramPath?.Trim() ?? "";
            if (string.IsNullOrEmpty(path)) return;

            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            if (!string.IsNullOrEmpty(cfg.LaunchArgs)) psi.Arguments = cfg.LaunchArgs!;
            if (!string.IsNullOrEmpty(cfg.WorkingDir)) psi.WorkingDirectory = cfg.WorkingDir!;

            switch (cfg.LaunchMode)
            {
                case "admin": psi.Verb = "runas"; break;
                case "minimized": psi.WindowStyle = ProcessWindowStyle.Minimized; break;
                case "hidden": psi.WindowStyle = ProcessWindowStyle.Hidden; break;
            }

            Process.Start(psi);
        }

        // ===== keys =====

        /// <summary>常用按键别名 → WPF Key</summary>
        private static readonly Dictionary<string, Key> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = Key.LeftCtrl, ["control"] = Key.LeftCtrl,
            ["shift"] = Key.LeftShift,
            ["alt"] = Key.LeftAlt,
            ["win"] = Key.LWin, ["windows"] = Key.LWin,
            ["esc"] = Key.Escape, ["escape"] = Key.Escape,
            ["enter"] = Key.Enter, ["return"] = Key.Enter,
            ["space"] = Key.Space,
            ["tab"] = Key.Tab,
            ["backspace"] = Key.Back, ["back"] = Key.Back,
            ["del"] = Key.Delete, ["delete"] = Key.Delete,
            ["ins"] = Key.Insert, ["insert"] = Key.Insert,
            ["home"] = Key.Home, ["end"] = Key.End,
            ["pgup"] = Key.PageUp, ["pageup"] = Key.PageUp,
            ["pgdn"] = Key.PageDown, ["pagedown"] = Key.PageDown,
            ["up"] = Key.Up, ["down"] = Key.Down, ["left"] = Key.Left, ["right"] = Key.Right,
        };

        /// <summary>把 "Ctrl+Shift+P" 解析为 VK 码列表；无法解析返回 null</summary>
        private static List<byte>? ParseKeySequence(string seq)
        {
            var result = new List<byte>();
            foreach (var raw in seq.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                byte vk;
                if (KeyAliases.TryGetValue(raw, out var key))
                {
                    vk = (byte)KeyInterop.VirtualKeyFromKey(key);
                }
                else if (Enum.TryParse<Key>(raw, true, out var parsed))
                {
                    vk = (byte)KeyInterop.VirtualKeyFromKey(parsed);
                }
                else if (raw.Length == 1 && char.IsLetterOrDigit(raw[0]))
                {
                    vk = (byte)char.ToUpperInvariant(raw[0]);
                }
                else
                {
                    return null;
                }
                result.Add(vk);
            }
            return result.Count > 0 ? result : null;
        }

        private static async Task ExecuteKeysAsync(AdvancedItemConfig cfg)
        {
            if (cfg.KeySequences.Count == 0) return;

            // 触发前延迟：等待原窗口接管焦点
            if (cfg.TriggerDelayMs > 0)
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero) NativeMethods.SetForegroundWindow(fg);
                await Task.Delay(cfg.TriggerDelayMs);
            }

            int interval = Math.Max(0, cfg.StepIntervalMs);
            foreach (var seq in cfg.KeySequences)
            {
                var vks = ParseKeySequence(seq);
                if (vks == null || vks.Count == 0) continue;

                foreach (var vk in vks)
                    NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
                for (int i = vks.Count - 1; i >= 0; i--)
                    NativeMethods.keybd_event(vks[i], 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

                if (interval > 0) await Task.Delay(interval);
            }
        }

        // ===== paste =====

        private static async Task ExecutePasteAsync(AdvancedItemConfig cfg)
        {
            string text = cfg.PasteText ?? "";
            if (string.IsNullOrEmpty(text)) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();

            if (cfg.PasteMode == "clipboard")
            {
                try { Clipboard.SetText(text); } catch { }
                return;
            }

            string? prevClip = null;
            try { if (Clipboard.ContainsText()) prevClip = Clipboard.GetText(); } catch { }

            if (cfg.PasteMode == "type")
            {
                // 逐字符输入：不动剪贴板
                if (fg != IntPtr.Zero) NativeMethods.SetForegroundWindow(fg);
                await Task.Delay(50);
                TypeText(text);
                return;
            }

            // 默认：模拟 Ctrl+V
            try { Clipboard.SetText(text); } catch { }
            if (fg != IntPtr.Zero) NativeMethods.SetForegroundWindow(fg);
            await Task.Delay(50);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL_KEY, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V_KEY, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V_KEY, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL_KEY, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (cfg.RestoreClipboard && prevClip != null)
            {
                // 延迟恢复剪贴板，给前台应用完成粘贴留出时间
                _ = Task.Delay(800).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { Clipboard.SetText(prevClip); } catch { }
                    }));
                });
            }
        }

        /// <summary>SendInput 逐字符（Unicode）输入</summary>
        private static void TypeText(string text)
        {
            var inputs = new List<NativeMethods.INPUT>();
            foreach (char c in text)
            {
                if (c == '\r') continue;
                char ch = c == '\n' ? '\r' : c; // 换行用 Enter
                if (ch == '\r')
                {
                    inputs.Add(MakeKeyInput(0x0D, false));
                    inputs.Add(MakeKeyInput(0x0D, true));
                }
                else
                {
                    inputs.Add(MakeUnicodeInput(ch, false));
                    inputs.Add(MakeUnicodeInput(ch, true));
                }
            }
            if (inputs.Count == 0) return;
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        }

        private static NativeMethods.INPUT MakeUnicodeInput(char c, bool keyUp)
        {
            var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
            input.U.ki.wVk = 0;
            input.U.ki.wScan = (ushort)c;
            input.U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
            return input;
        }

        private static NativeMethods.INPUT MakeKeyInput(byte vk, bool keyUp)
        {
            var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
            input.U.ki.wVk = vk;
            input.U.ki.dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
            return input;
        }

        // ===== script =====

        private static async Task ExecuteScriptAsync(AdvancedItemConfig cfg, string? selectedText)
        {
            string code = cfg.ScriptCode ?? "";
            if (string.IsNullOrWhiteSpace(code)) return;

            // {query} → 当前选中文字（原样注入），无选中时替换为空串；
            // PowerShell 走 EncodedCommand、CMD 写临时 bat，无需命令行转义（引用安全由脚本自行处理）
            if (code.Contains("{query}", StringComparison.OrdinalIgnoreCase))
                code = code.Replace("{query}", selectedText ?? "", StringComparison.OrdinalIgnoreCase);

            string output = "";
            bool isCmd = cfg.ScriptRuntime == "cmd";
            string? tempBat = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (isCmd)
                {
                    // CMD 多行脚本写入临时 bat 执行
                    tempBat = Path.Combine(Path.GetTempPath(), $"sd_adv_{Guid.NewGuid():N}.bat");
                    File.WriteAllText(tempBat, code, Encoding.UTF8);
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"{tempBat}\"";
                }
                else
                {
                    // PowerShell 用 EncodedCommand 避免引号转义问题
                    psi.FileName = "powershell.exe";
                    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(code));
                    psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
                }

                using var proc = Process.Start(psi);
                if (proc == null) return;

                using var cts = new CancellationTokenSource();
                int timeoutSec = cfg.ScriptTimeoutSec > 0 ? cfg.ScriptTimeoutSec : 10;
                cts.CancelAfter(timeoutSec * 1000);

                try
                {
                    string stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                    string stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                    await proc.WaitForExitAsync(cts.Token);
                    output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                }
                catch (OperationCanceledException)
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    Notify(Framework.Lang.T("advanced.scriptTimeout"));
                    return;
                }

                output = output.Trim();
            }
            finally
            {
                if (tempBat != null) { try { File.Delete(tempBat); } catch { } }
            }

            if (string.IsNullOrEmpty(output)) return;

            switch (cfg.ScriptOutput)
            {
                case "copy":
                    try { Clipboard.SetText(output); } catch { }
                    break;
                case "paste":
                    await ExecutePasteAsync(new AdvancedItemConfig { PasteText = output, PasteMode = "ctrlv", RestoreClipboard = true });
                    break;
                case "none":
                    break;
                default: // notify
                    Notify(output.Length > 200 ? output[..200] + "…" : output);
                    break;
            }
        }

        // ===== clipboard =====

        private static void ExecuteClipboard(AdvancedItemConfig cfg)
        {
            try { Clipboard.SetText(cfg.ClipboardText ?? ""); } catch { }
            if (cfg.ClipboardNotify) Notify(Framework.Lang.T("advanced.clipboardDone"));
        }

        // ===== search =====

        private static async Task ExecuteSearchAsync(AdvancedItemConfig cfg, string? selectedText)
        {
            string template = cfg.SearchTemplate?.Trim() ?? "";
            if (string.IsNullOrEmpty(template)) return;

            // 优先使用当前选中文字；无选中时回退到轻量输入小窗收集关键词
            string? query = !string.IsNullOrWhiteSpace(selectedText) ? selectedText : await ShowSearchInputDialog();
            if (string.IsNullOrWhiteSpace(query)) return;

            string url = template.Contains("{query}", StringComparison.OrdinalIgnoreCase)
                ? template.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.OrdinalIgnoreCase)
                : template + Uri.EscapeDataString(query.Trim());

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>搜索关键词输入小窗；取消返回 null</summary>
        private static Task<string?> ShowSearchInputDialog()
        {
            var tcs = new TaskCompletionSource<string?>();

            var win = new Window
            {
                Title = Framework.Lang.T("common.search"),
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushDialogBG"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain")
            };
            win.SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(win);

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = Framework.Lang.T("advanced.searchInputPrompt"),
                FontWeight = FontWeights.Bold,
                FontSize = (double)Application.Current.FindResource("FontSizeSubtitle"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMain"),
                Margin = new Thickness(0, 0, 0, 12)
            });
            var inputBox = new System.Windows.Controls.TextBox
            {
                Style = (Style)Application.Current.FindResource("GlobalTextBoxStyle")
            };
            panel.Children.Add(inputBox);

            var btnRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var okBtn = new System.Windows.Controls.Button
            {
                Content = Framework.Lang.T("common.ok"),
                Width = 75,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
            };
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = Framework.Lang.T("common.cancel"),
                Width = 75,
                Style = (Style)Application.Current.FindResource("StandardButtonStyle")
            };
            okBtn.Click += (s, e) => { tcs.TrySetResult(inputBox.Text.Trim()); win.Close(); };
            cancelBtn.Click += (s, e) => { tcs.TrySetResult(null); win.Close(); };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);

            win.Content = panel;
            win.Loaded += (s, e) => inputBox.Focus();
            win.Closed += (s, e) => tcs.TrySetResult(null);
            win.Show();
            inputBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { tcs.TrySetResult(inputBox.Text.Trim()); win.Close(); }
            };

            return tcs.Task;
        }

        // ===== 公共辅助 =====

        /// <summary>气泡通知（线程安全）</summary>
        private static void Notify(string text)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    (Application.Current.MainWindow as MainWindow)?.ShowBubbleNotification(text);
                }
                catch { }
            }));
        }
    }
}
