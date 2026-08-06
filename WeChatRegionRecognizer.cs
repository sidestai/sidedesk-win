#if false // 已禁用：微信区域截图 + 特征串文字识别功能不再启用（整个文件被排除编译）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Newtonsoft.Json;

namespace SideDesk
{
    /// <summary>
    /// 微信（或其他特殊程序）窗口区域识别器：
    /// 通过固定区域截图 → 二值化特征串 → 用户首次标注 → 特征串→文字 mapping 缓存
    /// </summary>
    public static class WeChatRegionRecognizer
    {
        private static readonly string DataFilePath;
        private static readonly object _lock = new object();
        private static RecognizerConfig _config;
        private static bool _loaded = false;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, IntPtr lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int SetROP2(IntPtr hdc, int fnDrawMode);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool Rectangle(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int R2_NOT = 6; // 反转色画笔（在任何背景上都可见）
        private const int PS_SOLID = 0;

        private const uint SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        static WeChatRegionRecognizer()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            DataFilePath = Path.Combine(appData, "SideDesk", "wechat_region_config.json");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(DataFilePath))
                {
                    string json = File.ReadAllText(DataFilePath);
                    _config = JsonConvert.DeserializeObject<RecognizerConfig>(json) ?? new RecognizerConfig();
                }
                else
                {
                    _config = new RecognizerConfig();
                }
            }
            catch
            {
                _config = new RecognizerConfig();
            }
        }

        private static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(DataFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(DataFilePath, json);
            }
            catch { }
        }

        /// <summary>
        /// 尝试识别当前微信窗口的聊天对象名称（基于区域截图 + 特征串匹配）
        /// </summary>
        /// <param name="hwnd">微信窗口句柄</param>
        /// <returns>识别到的名称，失败返回 null</returns>
        public static string RecognizeContactName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;

            lock (_lock)
            {
                EnsureLoaded();

                // 1. 检查是否启用了微信识别
                if (!_config.Enabled)
                {
                    System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 识别未启用 (Enabled=false)");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 开始识别 hwnd={hwnd}, 区域=({_config.RegionX},{_config.RegionY},{_config.RegionW},{_config.RegionH})");

                // 2. 获取窗口区域截图
                Bitmap regionBmp = CaptureWindowRegion(hwnd, _config.RegionX, _config.RegionY, _config.RegionW, _config.RegionH);
                if (regionBmp == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 截图失败");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 截图成功 {regionBmp.Width}x{regionBmp.Height}");

                try
                {
                    // 3. 计算二值化特征串
                    string featureHash = ComputeBinaryFeatureHash(regionBmp, out int blackPixelDensity);
                    if (string.IsNullOrEmpty(featureHash))
                    {
                        System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 特征串计算失败");
                        return null;
                    }

                    // 3.1 检查黑像素密度，过低说明截图区域无文字内容
                    if (blackPixelDensity < 5) // 黑像素占比 < 5%
                    {
                        System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 黑像素密度过低 ({blackPixelDensity}%)，跳过识别");
                        return null;
                    }

                    System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 特征串长度={featureHash.Length}, 黑像素密度={blackPixelDensity}%, 前 40 位={featureHash.Substring(0, Math.Min(40, featureHash.Length))}");

                    // 4. 查 mapping 缓存
                    if (_config.FeatureMapping.TryGetValue(featureHash, out string cachedName))
                    {
                        System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 缓存命中: {cachedName}");
                        return cachedName;
                    }

                    System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 缓存未命中，弹出输入框");

                    // 5. 缓存未命中 → 弹窗让用户输入名称（带截图预览）
                    if (Application.Current != null && Application.Current.Dispatcher != null)
                    {
                        // 需要在 UI 线程上创建窗口
                        string userInput = null;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dialog = new Window
                            {
                                Title = "微信联系人识别",
                                Width = 650,
                                Height = 200,
                                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                                ResizeMode = ResizeMode.NoResize,
                                ShowInTaskbar = false,
                                Topmost = true
                            };

                            var mainGrid = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
                            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

                            // 截图预览
                            var imageControl = new System.Windows.Controls.Image
                            {
                                Width = 600,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.None,
                                Margin = new Thickness(0, 0, 0, 10)
                            };
                            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                regionBmp.GetHbitmap(),
                                IntPtr.Zero,
                                Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            imageControl.Source = bitmapSource;
                            System.Windows.Controls.Grid.SetRow(imageControl, 0);
                            mainGrid.Children.Add(imageControl);

                            // 提示文字
                            var hintText = new System.Windows.Controls.TextBlock
                            {
                                Text = "请识别截图中的联系人/群聊名称：",
                                FontSize = 13,
                                Margin = new Thickness(0, 0, 0, 5)
                            };
                            System.Windows.Controls.Grid.SetRow(hintText, 1);
                            mainGrid.Children.Add(hintText);

                            // 输入框
                            var textBox = new System.Windows.Controls.TextBox
                            {
                                FontSize = 14,
                                Padding = new Thickness(5),
                                Margin = new Thickness(0, 0, 0, 10)
                            };
                            System.Windows.Controls.Grid.SetRow(textBox, 2);
                            mainGrid.Children.Add(textBox);

                            // 按钮
                            var btnPanel = new System.Windows.Controls.StackPanel
                            {
                                Orientation = System.Windows.Controls.Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right
                            };

                            var okBtn = new System.Windows.Controls.Button
                            {
                                Content = "确定",
                                Width = 80,
                                Height = 28,
                                Margin = new Thickness(0, 0, 10, 0),
                                IsDefault = true
                            };
                            okBtn.Click += (s, e) => { userInput = textBox.Text.Trim(); dialog.Close(); };

                            var skipBtn = new System.Windows.Controls.Button
                            {
                                Content = "跳过",
                                Width = 80,
                                Height = 28
                            };
                            skipBtn.Click += (s, e) => { userInput = ""; dialog.Close(); };

                            btnPanel.Children.Add(okBtn);
                            btnPanel.Children.Add(skipBtn);
                            System.Windows.Controls.Grid.SetRow(btnPanel, 3);
                            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                            mainGrid.Children.Add(btnPanel);

                            dialog.Content = mainGrid;
                            textBox.SelectAll(); // 全选方便修改
                            textBox.Focus();
                            dialog.ShowDialog();
                        });

                        if (!string.IsNullOrWhiteSpace(userInput))
                        {
                            _config.FeatureMapping[featureHash] = userInput.Trim();
                            Save();
                            System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 用户输入: {userInput.Trim()}");
                            return userInput.Trim();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 用户跳过输入");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] Application.Current 为 null，无法弹窗");
                    }

                    // 清理过期特征串（保留最近 200 条）
                    if (_config.FeatureMapping.Count > 200)
                    {
                        var toRemove = _config.FeatureMapping.Keys.Take(_config.FeatureMapping.Count - 200).ToList();
                        foreach (var key in toRemove)
                            _config.FeatureMapping.Remove(key);
                        Save();
                    }
                }
                finally
                {
                    regionBmp.Dispose();
                }
            }

            return null;
        }

        /// <summary>截取窗口指定区域的位图</summary>
        private static Bitmap CaptureWindowRegion(IntPtr hwnd, int x, int y, int w, int h)
        {
            IntPtr screenDC = IntPtr.Zero;
            IntPtr memDC = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                // 获取窗口在屏幕上的位置
                if (!GetWindowRect(hwnd, out RECT rect))
                {
                    System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] GetWindowRect 失败");
                    return null;
                }

                // 计算屏幕绝对坐标
                int screenX = rect.Left + x;
                int screenY = rect.Top + y;

                System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 窗口 rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), 截图区域=({screenX},{screenY},{w},{h})");

                // 截图
                screenDC = CreateDC("DISPLAY", null, IntPtr.Zero, IntPtr.Zero);
                memDC = CreateCompatibleDC(screenDC);
                hBitmap = CreateCompatibleBitmap(screenDC, w, h);
                oldBitmap = SelectObject(memDC, hBitmap);

                BitBlt(memDC, 0, 0, w, h, screenDC, screenX, screenY, SRCCOPY);

                var bitmap = Image.FromHbitmap(hBitmap);

                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 截图异常: {ex.Message}");
                return null;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) DeleteObject(oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDC != IntPtr.Zero) DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) DeleteDC(screenDC);
            }
        }

        /// <summary>在屏幕上绘制红色矩形框，1.5 秒后自动清除 - 已禁用</summary>
        private static void DrawRegionRect(int x, int y, int w, int h)
        {
            // 整个方法已禁用，不再绘制矩形框
        }

        /// <summary>计算二值化特征串：4×4 区块二值化</summary>
        private static string ComputeBinaryFeatureHash(Bitmap bitmap, out int blackPixelDensity)
        {
            blackPixelDensity = 0;

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return string.Empty;

            System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 位图实际尺寸: {bitmap.Width}x{bitmap.Height}");

            var sb = new StringBuilder();
            int step = 4; // 每 4x4 像素取 1bit
            int totalBlocks = 0;
            int blackBlocks = 0;

            for (int y = 0; y < bitmap.Height; y += step)
            {
                for (int x = 0; x < bitmap.Width; x += step)
                {
                    int graySum = 0;
                    int count = 0;

                    for (int dy = 0; dy < step && y + dy < bitmap.Height; dy++)
                    {
                        for (int dx = 0; dx < step && x + dx < bitmap.Width; dx++)
                        {
                            Color c = bitmap.GetPixel(x + dx, y + dy);
                            graySum += (c.R + c.G + c.B) / 3;
                            count++;
                        }
                    }

                    int avg = graySum / count;
                    bool isBlack = avg < 128;
                    sb.Append(isBlack ? '1' : '0');

                    totalBlocks++;
                    if (isBlack) blackBlocks++;
                }
            }

            // 计算黑像素密度（百分比）
            blackPixelDensity = totalBlocks > 0 ? (blackBlocks * 100 / totalBlocks) : 0;
            System.Diagnostics.Debug.WriteLine($"[WeChatRecognizer] 区块数={totalBlocks}, 黑区块数={blackBlocks}, 密度={blackPixelDensity}%");

            return sb.ToString();
        }

        /// <summary>设置识别区域（保存后下次生效）</summary>
        public static void SetRegion(int x, int y, int w, int h)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _config.RegionX = x;
                _config.RegionY = y;
                _config.RegionW = w;
                _config.RegionH = h;
                Save();
            }
        }

        /// <summary>启用/禁用识别</summary>
        public static void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _config.Enabled = enabled;
                Save();
            }
        }

        /// <summary>清空特征串 mapping 缓存</summary>
        public static void ClearMapping()
        {
            lock (_lock)
            {
                EnsureLoaded();
                _config.FeatureMapping.Clear();
                Save();
            }
        }

        /// <summary>获取当前配置</summary>
        public static RecognizerConfig GetConfig()
        {
            lock (_lock)
            {
                EnsureLoaded();
                return new RecognizerConfig
                {
                    Enabled = _config.Enabled,
                    RegionX = _config.RegionX,
                    RegionY = _config.RegionY,
                    RegionW = _config.RegionW,
                    RegionH = _config.RegionH,
                    FeatureMapping = new Dictionary<string, string>(_config.FeatureMapping)
                };
            }
        }
    }

    /// <summary>
    /// 区域识别器配置
    /// </summary>
    public class RecognizerConfig
    {
        public bool Enabled { get; set; } = true; // 默认启用
        public int RegionX { get; set; } = 340;   // 相对窗口左上角 X 偏移
        public int RegionY { get; set; } = 35;    // 相对窗口左上角 Y 偏移
        public int RegionW { get; set; } = 600;   // 宽度
        public int RegionH { get; set; } = 40;    // 高度
        public Dictionary<string, string> FeatureMapping { get; set; } = new Dictionary<string, string>();
    }
}
#endif
