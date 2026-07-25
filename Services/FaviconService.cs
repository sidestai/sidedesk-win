using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace SideDesk.Services
{
    /// <summary>
    /// 网站 favicon 查询/下载/缓存服务，兼作本地文件图标（exe/图片）的加载工具。
    /// 下载的 favicon 持久化到本地，网址→本地图标路径建立索引，避免重复拉取远端图标。
    /// </summary>
    public static class FaviconService
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
        private static readonly string CacheDir = Path.Combine(DataDir, "favicons");
        private static readonly string IndexFile = Path.Combine(DataDir, "favicon-cache.json");

        private static readonly HttpClient _http = CreateHttp();
        private static readonly Dictionary<string, string> _index = LoadIndex();
        private static readonly Dictionary<string, BitmapSource?> _iconCache = new();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient();
            h.Timeout = TimeSpan.FromSeconds(8);
            try { h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
            return h;
        }

        private static Dictionary<string, string> LoadIndex()
        {
            try
            {
                if (File.Exists(IndexFile))
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(IndexFile)) ?? new();
            }
            catch { }
            return new();
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(IndexFile, JsonConvert.SerializeObject(_index, Formatting.Indented));
            }
            catch { }
        }

        /// <summary>把网址归一化为源（scheme://host）作为缓存键。</summary>
        private static string? NormalizeKey(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme != "http" && u.Scheme != "https") return null;
                return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            }
            catch { return null; }
        }

        /// <summary>尝试直接命中缓存，返回本地 favicon 文件路径（未命中返回 null）。</summary>
        public static string? TryGetCached(string url)
        {
            var key = NormalizeKey(url);
            if (key == null) return null;
            lock (_index)
            {
                if (_index.TryGetValue(key, out var path) && File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// 异步获取网站 favicon：先查缓存；未命中则抓取页面解析 link[rel=icon]，
        /// 回退 /favicon.ico；下载到本地缓存并返回本地文件路径。失败返回 null。
        /// </summary>
        public static async Task<string?> FetchFaviconAsync(string url)
        {
            var key = NormalizeKey(url);
            if (key == null) return null;

            var cached = TryGetCached(url);
            if (cached != null) return cached;

            // 候选列表：页面解析出的图标链接在前，/favicon.ico 兜底
            var candidates = new List<string>();
            try
            {
                string html = await _http.GetStringAsync(url);
                var parsed = ParseFaviconLink(html, url);
                if (!string.IsNullOrEmpty(parsed)) candidates.Add(parsed);
            }
            catch { }
            candidates.Add(key.TrimEnd('/') + "/favicon.ico");

            foreach (var favUrl in candidates)
            {
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(favUrl);
                    if (bytes.Length == 0) continue;
                    string ext = GuessExt(favUrl, bytes);
                    if (ext == ".svg") continue; // WPF 无法直接渲染 svg，尝试下一个候选
                    Directory.CreateDirectory(CacheDir);
                    string localPath = Path.Combine(CacheDir, SafeFileName(key) + ext);
                    await File.WriteAllBytesAsync(localPath, bytes);
                    lock (_index)
                    {
                        _index[key] = localPath;
                        SaveIndex();
                    }
                    return localPath;
                }
                catch { }
            }
            return null;
        }

        /// <summary>从页面 HTML 解析 &lt;link rel="icon"&gt; 的 href（支持相对路径/协议相对路径）。</summary>
        private static string? ParseFaviconLink(string html, string baseUrl)
        {
            string? best = null;
            foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var relM = Regex.Match(tag.Value, @"rel\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                if (!relM.Success) continue;
                string rel = relM.Groups[1].Value.Trim().ToLowerInvariant();
                bool isIcon = rel == "icon" || rel == "shortcut icon" || rel.Split(' ').Contains("icon");
                if (!isIcon) continue;
                var hrefM = Regex.Match(tag.Value, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!hrefM.Success) continue;
                string hrefVal = hrefM.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(hrefVal) || hrefVal.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                try { best = new Uri(new Uri(baseUrl), hrefVal).AbsoluteUri; } catch { continue; }
                if (rel == "icon" || rel == "shortcut icon") break; // 精确匹配优先
            }
            return best;
        }

        /// <summary>按文件魔数/扩展名推断图片格式。</summary>
        private static string GuessExt(string url, byte[] bytes)
        {
            if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50) return ".png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
            if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F') return ".gif";
            string path = url.Split('?')[0].ToLowerInvariant();
            if (path.EndsWith(".png")) return ".png";
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return ".jpg";
            if (path.EndsWith(".gif")) return ".gif";
            if (path.EndsWith(".svg")) return ".svg";
            return ".ico";
        }

        private static string SafeFileName(string key)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            return key.Replace("://", "_").Replace(":", "_").Replace(".", "_");
        }

        // ---------- 本地文件图标加载（供按钮渲染/预览复用） ----------

        /// <summary>判断图标字符串是否为路径形式（含路径分隔符，或为实际存在的文件）。</summary>
        public static bool IsPathIcon(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.IndexOfAny(new[] { '\\', '/' }) >= 0) return true;
            try { return File.Exists(s); } catch { return false; }
        }

        /// <summary>
        /// 从本地文件加载图标（带缓存）：exe 等提取关联图标，
        /// png/jpg/ico 等图片直接解码。失败返回 null。
        /// </summary>
        public static BitmapSource? LoadFileIcon(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_iconCache)
            {
                if (_iconCache.TryGetValue(path, out var cached)) return cached;
            }
            BitmapSource? result = null;
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico")
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    if (bi.CanFreeze) bi.Freeze();
                    result = bi;
                }
                else
                {
                    using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        if (icon != null)
                        {
                            var src = Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            if (src.CanFreeze) src.Freeze();
                            result = src;
                        }
                    }
                }
            }
            catch { }
            lock (_iconCache) { _iconCache[path] = result; }
            return result;
        }
    }
}
