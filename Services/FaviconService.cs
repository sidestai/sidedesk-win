using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
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

        // 后台图标提取节流：选择弹窗会对上百个程序并发提取图标（且搜索每次按键都重建重提），
        // System.Drawing.Icon.ExtractAssociatedIcon 在高并发下会瞬时失败返回 null（GDI 句柄压力），
        // 形成“提取风暴”→ 刚添加的快捷项图标空白、需重启才恢复。限制后台并发数即可消除风暴。
        // 仅用于后台异步加载；UI 线程的同步 LoadFileIcon（按钮渲染/预览）不走限流，避免阻塞界面。
        private static readonly SemaphoreSlim _extractThrottle = new(4, 4);

        // 网址 favicon 重抓节流 + 失败负缓存：分组内多个网址项缓存图标同时失效时，
        // 避免并发发起大量 HTTP 抓取；对确实抓不到 favicon 的网址记录失败时刻，TTL 内不再重试，
        // 防止每次渲染都对同一失败网址重复抓取（呼应“缓存图标丢失时重新获取一次”的语义）。
        private static readonly SemaphoreSlim _fetchThrottle = new(3, 3);
        private static readonly Dictionary<string, DateTime> _fetchFailed = new();
        private static readonly TimeSpan _fetchRetryInterval = TimeSpan.FromMinutes(10);

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

        /// <summary>
        /// 后台节流抓取网站 favicon：命中本地缓存立即返回（不占限流额度），否则排队等信号量后再抓取。
        /// 供“分组内多个网址项缓存图标同时失效需批量重抓”的场景使用，避免并发 HTTP 风暴；
        /// 对抓取失败的网址在 _fetchRetryInterval 内不再重试，避免对无图标网址每次渲染重复请求。
        /// </summary>
        public static async Task<string?> FetchFaviconThrottledAsync(string url)
        {
            var cached = TryGetCached(url);
            if (cached != null) return cached;

            var key = NormalizeKey(url);
            if (key == null) return null;

            // TTL 内已失败过则跳过，避免对无 favicon 网址的重复抓取风暴
            lock (_fetchFailed)
            {
                if (_fetchFailed.TryGetValue(key, out var ts) && DateTime.UtcNow - ts < _fetchRetryInterval)
                    return null;
            }

            await _fetchThrottle.WaitAsync().ConfigureAwait(false);
            string? path;
            try { path = await FetchFaviconAsync(url).ConfigureAwait(false); }
            finally { _fetchThrottle.Release(); }

            lock (_fetchFailed)
            {
                if (string.IsNullOrEmpty(path)) _fetchFailed[key] = DateTime.UtcNow;
                else _fetchFailed.Remove(key);
            }
            return path;
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
        /// png/jpg/ico 等图片直接解码；.lnk 解析到目标/自定义图标提取，避免快捷方式箭头角标。失败返回 null。
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
                if (ext == ".lnk")
                {
                    // .lnk 的关联图标会被 shell 烘入快捷方式箭头角标，解析到自定义图标位置或目标文件提取纯图标
                    var resolved = ResolveShortcutIcon(path);
                    if (resolved != null)
                        result = LoadResolvedIcon(resolved.Value.File, resolved.Value.Index);
                }
                if (result == null)
                {
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico")
                        result = DecodeImageFile(path);
                    else
                        result = ExtractAssociatedIconSource(path);
                }
            }
            catch { }
            // 只缓存成功结果：提取失败（如选择弹窗并发风暴中的瞬时失败）若缓存 null 会永久毒化该路径，
            // 导致刚添加的快捷项无图标、重启后才恢复；不缓存失败让下次调用可重试
            if (result != null)
            {
                lock (_iconCache) { _iconCache[path] = result; }
            }
            return result;
        }

        /// <summary>
        /// 后台节流加载文件图标：命中缓存立即返回（不占限流额度），否则排队等信号量后在线程池提取。
        /// 供选择弹窗等“对大量程序并发提图标”的场景使用，避免无上限并发引发 ExtractAssociatedIcon 瞬时失败风暴；
        /// 缓存优先也让搜索重建列表时对已加载图标的重复请求瞬间返回，不再重复提取。
        /// </summary>
        public static async Task<BitmapSource?> LoadFileIconThrottledAsync(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_iconCache) { if (_iconCache.TryGetValue(path, out var cached)) return cached; }
            await _extractThrottle.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(() => LoadFileIcon(path)).ConfigureAwait(false); }
            finally { _extractThrottle.Release(); }
        }

        /// <summary>解码 png/jpg/ico 等图片文件，失败抛异常由调用方兜底。</summary>
        private static BitmapSource DecodeImageFile(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            return bi;
        }

        /// <summary>提取文件关联图标并转 BitmapSource，失败返回 null。</summary>
        private static BitmapSource? ExtractAssociatedIconSource(string path)
        {
            using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
            {
                if (icon == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (src.CanFreeze) src.Freeze();
                return src;
            }
        }

        /// <summary>
        /// 解析 .lnk 快捷方式的图标来源：优先自定义图标位置（IconLocation，含索引），否则目标文件（TargetPath）。
        /// 解析失败返回 null（调用方回退默认提取）。
        /// </summary>
        private static (string File, int Index)? ResolveShortcutIcon(string lnkPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType)!;
                try
                {
                    object lnk = shellType.InvokeMember("CreateShortcut",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath })!;
                    string iconLocation = lnk.GetType().InvokeMember("IconLocation",
                        System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string ?? "";
                    string targetPath = lnk.GetType().InvokeMember("TargetPath",
                        System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string ?? "";
                    Marshal.ReleaseComObject(lnk);

                    // IconLocation 形如 "C:\app.ico, 2"，为空表示未设自定义图标
                    string file = iconLocation;
                    int index = 0;
                    int comma = iconLocation.LastIndexOf(',');
                    if (comma >= 0)
                    {
                        file = iconLocation.Substring(0, comma).Trim();
                        int.TryParse(iconLocation.Substring(comma + 1).Trim(), out index);
                    }
                    file = file.Trim().Trim('"');
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        file = targetPath;
                        index = 0;
                    }
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                    return (file, index);
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
            catch { return null; }
        }

        /// <summary>从解析后的快捷方式图标来源加载：图片文件直接解码，exe/dll 按索引提取。</summary>
        private static BitmapSource? LoadResolvedIcon(string file, int index)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico")
                return DecodeImageFile(file);
            if (index <= 0)
                return ExtractAssociatedIconSource(file) ?? ExtractIconByIndex(file, 0);
            return ExtractIconByIndex(file, index) ?? ExtractAssociatedIconSource(file);
        }

        /// <summary>按图标索引从 exe/dll 提取图标（PrivateExtractIcons），失败返回 null。</summary>
        private static BitmapSource? ExtractIconByIndex(string file, int index)
        {
            var icons = new IntPtr[1];
            var ids = new int[1];
            int n = PrivateExtractIcons(file, index, 48, 48, icons, ids, 1, 0);
            if (n <= 0 || icons[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(
                icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (src.CanFreeze) src.Freeze();
            DestroyIcon(icons[0]);
            return src;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(
            string szFileName, int nIconIndex, int cxIcon, int cyIcon,
            [Out] IntPtr[] phicon, [Out] int[] piconid, int nIcons, int flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
