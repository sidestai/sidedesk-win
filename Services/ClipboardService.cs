using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SideDesk.Services
{
    public enum ClipboardContentType
    {
        Text,       // 纯文本
        Image,      // 图片
        File,       // 文件/文件夹
        Html,       // HTML/文档
        Video,      // 视频
        Unknown     // 未知
    }

    public class ClipboardEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Time { get; set; }
        public ClipboardContentType ContentType { get; set; }
        public List<string>? FilePaths { get; set; } // 用于文件类型，保存完整路径
    }

    /// <summary>
    /// 剪贴板监控服务：监听系统剪贴板变化，维护历史记录。
    /// 使用独立的消息窗口接收 WM_CLIPBOARDUPDATE，不依赖主窗口消息泵。
    /// </summary>
    public class ClipboardService
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int MAX_CLIPBOARD_ITEMS = 10;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private readonly List<ClipboardEntry> _clipboardHistory = new();
        private string? _lastClipboardText = null;
        private HwndSource? _messageWindowSource;
        private bool _isRunning = false;

        /// <summary>剪贴板收藏记录（只读访问）</summary>
        public IReadOnlyList<ClipboardEntry> ClipboardHistory => _clipboardHistory;

        /// <summary>剪贴板内容变化事件</summary>
        public event Action? ClipboardChanged;

        /// <summary>启动剪贴板监控（创建独立消息窗口）</summary>
        public void Start()
        {
            if (_isRunning) return;

            try
            {
                // 创建隐藏消息窗口，专门接收剪贴板通知
                var param = new HwndSourceParameters("ClipboardListener")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0x800000, // WS_POPUP
                    ExtendedWindowStyle = 0x00000008, // WS_EX_TOOLWINDOW
                    ParentWindow = HWND_MESSAGE
                };

                _messageWindowSource = new HwndSource(param);
                _messageWindowSource.AddHook(MessageWndProc);

                var hwnd = _messageWindowSource.Handle;
                bool success = NativeMethods.AddClipboardFormatListener(hwnd);
                if (!success)
                {
                    System.Diagnostics.Debug.WriteLine($"[ClipboardService] AddClipboardFormatListener 失败, 错误码: {Marshal.GetLastWin32Error()}");
                    _messageWindowSource.Dispose();
                    _messageWindowSource = null;
                    return;
                }

                _isRunning = true;
                System.Diagnostics.Debug.WriteLine("[ClipboardService] 剪贴板监控已启动");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Start 异常: {ex.Message}");
            }
        }

        /// <summary>停止剪贴板监控</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                if (_messageWindowSource != null)
                {
                    NativeMethods.RemoveClipboardFormatListener(_messageWindowSource.Handle);
                    _messageWindowSource.Dispose();
                    _messageWindowSource = null;
                }
            }
            catch { }

            _isRunning = false;
        }

        /// <summary>消息窗口的窗口过程</summary>
        private IntPtr MessageWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                OnClipboardChanged();
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        /// <summary>清空历史记录</summary>
        public void ClearHistory()
        {
            _clipboardHistory.Clear();
            _lastClipboardText = null;
            ClipboardChanged?.Invoke();
        }

        private void OnClipboardChanged()
        {
            try
            {
                ClipboardContentType contentType = ClipboardContentType.Unknown;
                string? contentText = null;

                // 检测剪贴板内容类型
                if (Clipboard.ContainsFileDropList())
                {
                    contentType = ClipboardContentType.File;
                    var files = Clipboard.GetFileDropList();
                    var filePaths = new List<string>();
                    if (files.Count > 0)
                    {
                        foreach (string? file in files)
                        {
                            if (file != null) filePaths.Add(file);
                        }
                        var fileNames = filePaths.Select(f => System.IO.Path.GetFileName(f));
                        contentText = string.Join(", ", fileNames);
                    }

                    _clipboardHistory.Insert(0, new ClipboardEntry
                    {
                        Text = contentText ?? "",
                        Time = DateTime.Now,
                        ContentType = contentType,
                        FilePaths = filePaths
                    });

                    if (_clipboardHistory.Count > MAX_CLIPBOARD_ITEMS)
                        _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);

                    ClipboardChanged?.Invoke();
                    return;
                }
                else if (Clipboard.ContainsImage())
                {
                    contentType = ClipboardContentType.Image;
                    var image = Clipboard.GetImage();
                    if (image != null)
                        contentText = $"图片 ({image.PixelWidth}x{image.PixelHeight})";
                    else
                        contentText = "图片";
                }
                else if (Clipboard.ContainsText())
                {
                    contentType = ClipboardContentType.Text;
                    contentText = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(contentText)) return;
                    contentText = contentText.Trim();

                    // 检测是否可能是视频链接
                    if (contentText.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        if (contentText.Contains("youtube.com") || contentText.Contains("bilibili.com") ||
                            contentText.Contains("vimeo.com") || contentText.Contains("douyin.com") ||
                            contentText.Contains("tiktok.com"))
                        {
                            contentType = ClipboardContentType.Video;
                        }
                    }
                }
                else if (Clipboard.ContainsText(TextDataFormat.Html))
                {
                    contentType = ClipboardContentType.Html;
                    string htmlContent = Clipboard.GetText(TextDataFormat.Html);

                    // 从 HTML 中提取纯文本
                    contentText = System.Text.RegularExpressions.Regex.Replace(htmlContent, "<[^>]+>", "");
                    contentText = contentText.Replace("&nbsp;", " ")
                                            .Replace("&amp;", "&")
                                            .Replace("&lt;", "<")
                                            .Replace("&gt;", ">")
                                            .Replace("&quot;", "\"")
                                            .Replace("&#39;", "'");
                    contentText = contentText.Trim();

                    if (string.IsNullOrWhiteSpace(contentText)) return;
                    if (contentText.Length > 100)
                        contentText = contentText.Substring(0, 100) + "...";
                }
                else
                {
                    return; // 不支持的类型
                }

                // 去重（相同文本内容跳过）
                if (contentType == ClipboardContentType.Text || contentType == ClipboardContentType.Html)
                {
                    if (contentText == _lastClipboardText) return;
                    _lastClipboardText = contentText;
                }

                // 移除重复项
                _clipboardHistory.RemoveAll(e => e.Text == contentText);

                // 插入最前面
                _clipboardHistory.Insert(0, new ClipboardEntry
                {
                    Text = contentText ?? "",
                    Time = DateTime.Now,
                    ContentType = contentType,
                    FilePaths = null
                });

                // 保持最多10条
                if (_clipboardHistory.Count > MAX_CLIPBOARD_ITEMS)
                    _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);

                ClipboardChanged?.Invoke();
            }
            catch { }
        }

        /// <summary>获取内容类型对应的图标</summary>
        public static string GetContentTypeIcon(ClipboardContentType type)
        {
            return type switch
            {
                ClipboardContentType.Text => "📝",
                ClipboardContentType.Image => "🖼️",
                ClipboardContentType.File => "📁",
                ClipboardContentType.Html => "📄",
                ClipboardContentType.Video => "🎬",
                _ => "📋"
            };
        }

        /// <summary>获取内容类型对应的颜色</summary>
        public static System.Windows.Media.Brush GetContentTypeColor(ClipboardContentType type)
        {
            return type switch
            {
                ClipboardContentType.Text => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 180, 255)),
                ClipboardContentType.Image => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(100, 220, 150)),
                ClipboardContentType.File => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 180, 80)),
                ClipboardContentType.Html => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(180, 140, 255)),
                ClipboardContentType.Video => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 100, 120)),
                _ => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(140, 140, 140))
            };
        }
    }
}
