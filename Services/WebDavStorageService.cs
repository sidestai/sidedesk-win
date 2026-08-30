using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// WebDAV 存储服务：基于标准 WebDAV 协议（PROPFIND/PUT/GET/MKCOL/DELETE + Basic 认证）的直连封装。
    /// 目录层级为服务器真实目录；路径模型与 S3 后端一致：根 ""，文件夹以 / 结尾，文件为完整相对路径。
    /// </summary>
    public sealed class WebDavStorageService
    {
        private readonly WebDavSource _src;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

        private static readonly HttpMethod Propfind = new("PROPFIND");
        private static readonly HttpMethod Mkcol = new("MKCOL");

        private const string PropfindBody =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>";

        public WebDavStorageService(WebDavSource source)
        {
            _src = source;
        }

        #region 请求基础

        /// <summary>相对路径的 URL 编码（保留 / 分段）</summary>
        private static string EncodePath(string path)
            => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

        private HttpRequestMessage CreateRequest(HttpMethod method, string relPath, HttpContent? content = null)
        {
            string url = _src.BaseUrl + EncodePath(relPath);
            var req = new HttpRequestMessage(method, url) { Content = content };
            if (!string.IsNullOrEmpty(_src.Username))
            {
                string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_src.Username}:{_src.Password}"));
                req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic);
            }
            return req;
        }

        /// <summary>PROPFIND 列目录并解析 multistatus。返回的条目不含请求目录自身。</summary>
        private async Task<List<(string RelPath, bool IsCollection, long Size)>?> PropfindAsync(string relPath, int depth)
        {
            try
            {
                using var req = CreateRequest(Propfind, relPath, new StringContent(PropfindBody, Encoding.UTF8, "application/xml"));
                req.Headers.TryAddWithoutValidation("Depth", depth.ToString());

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                string xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 207)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebDAV] PROPFIND failed: HTTP {(int)resp.StatusCode}");
                    return null;
                }

                var doc = XDocument.Parse(xml);
                var responses = doc.Descendants().Where(e => e.Name.LocalName == "response").ToList();

                // 请求目录自身的判定基准：解码后的绝对路径
                string basePath = new Uri(_src.BaseUrl).AbsolutePath;
                string selfAbs = Uri.UnescapeDataString(new Uri(_src.BaseUrl + EncodePath(relPath)).AbsolutePath).TrimEnd('/') ;

                var result = new List<(string, bool, long)>();
                foreach (var r in responses)
                {
                    string? hrefRaw = r.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
                    if (string.IsNullOrEmpty(hrefRaw)) continue;

                    // href 可能是绝对 URL 或绝对路径，统一转为解码后的绝对路径
                    string hrefAbs;
                    try
                    {
                        var u = new Uri(hrefRaw.Trim(), UriKind.RelativeOrAbsolute);
                        hrefAbs = Uri.UnescapeDataString(u.IsAbsoluteUri ? u.AbsolutePath : hrefRaw.Trim());
                    }
                    catch { hrefAbs = Uri.UnescapeDataString(hrefRaw.Trim()); }
                    hrefAbs = hrefAbs.TrimEnd('/');

                    if (hrefAbs == selfAbs) continue; // 跳过自身

                    // 转为相对根的路径
                    if (!hrefAbs.StartsWith(basePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = hrefAbs.Substring(basePath.TrimEnd('/').Length).TrimStart('/');
                    if (string.IsNullOrEmpty(rel)) continue;

                    var prop = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "prop");
                    bool isCollection = prop?.Descendants().Any(e => e.Name.LocalName == "collection") == true;
                    long size = 0;
                    if (!isCollection)
                    {
                        string? sizeText = prop?.Elements().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value;
                        long.TryParse(sizeText, out size);
                    }
                    result.Add((rel + (isCollection ? "/" : ""), isCollection, size));
                }
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebDAV] PROPFIND error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 列目录 / 容量

        /// <summary>
        /// 列出指定目录（相对路径）下的直接子项：文件夹（以 / 结尾）+ 文件。
        /// 文件夹 Id = 完整相对路径（以 / 结尾），文件 Id = 完整相对路径。
        /// </summary>
        public async Task<List<CloudDriveFile>?> ListDirectoryAsync(string dirPath)
        {
            var entries = await PropfindAsync(dirPath, depth: 1).ConfigureAwait(false);
            if (entries == null) return null;

            var result = new List<CloudDriveFile>();
            foreach (var (rel, isCollection, size) in entries)
            {
                // 只取当前目录的直接子项（防御服务端返回更深层级）
                string rest = rel.StartsWith(dirPath, StringComparison.Ordinal) ? rel.Substring(dirPath.Length) : rel;
                rest = rest.TrimEnd('/');
                if (string.IsNullOrEmpty(rest) || rest.Contains('/')) continue;

                result.Add(new CloudDriveFile
                {
                    Id = rel,
                    Name = rest,
                    IsFolder = isCollection,
                    FileSize = isCollection ? null : size
                });
            }
            return result
                .OrderByDescending(f => f.IsFolder)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>递归统计整个存储空间已用容量（逐层 PROPFIND，兼容性优于 Depth: infinity）</summary>
        public async Task<long> GetTotalUsedBytesAsync()
        {
            long total = 0;
            var queue = new Queue<string>();
            queue.Enqueue("");
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                var files = await ListDirectoryAsync(dir).ConfigureAwait(false);
                if (files == null) continue;
                foreach (var f in files)
                {
                    if (f.IsFolder) queue.Enqueue(f.Id);
                    else total += f.FileSize ?? 0;
                }
            }
            return total;
        }

        #endregion

        #region 上传 / 下载 / 新建 / 删除

        /// <summary>上传本地文件（PUT + 进度回调）</summary>
        public async Task<bool> UploadFileAsync(string localPath, string parentDir, Action<double>? progress = null)
        {
            try
            {
                string relPath = parentDir + Path.GetFileName(localPath);
                var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                using var content = new ProgressStreamContent(fileStream, fileStream.Length, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var req = CreateRequest(HttpMethod.Put, relPath, content);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[WebDAV] upload failed: HTTP {(int)resp.StatusCode} - {err}");
                    return false;
                }
                await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                progress?.Invoke(100);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebDAV] upload error: {ex.Message}");
                return false;
            }
        }

        /// <summary>下载文件到本地（GET + 进度回调）</summary>
        public async Task<bool> DownloadToFileAsync(string relPath, string savePath, Action<double>? progress = null)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, relPath);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebDAV] download failed: HTTP {(int)resp.StatusCode}");
                    return false;
                }

                long total = resp.Content.Headers.ContentLength ?? -1;
                long received = 0;
                int lastPct = -1;
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var dst = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        int pct = (int)(received * 100 / total);
                        if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
                    }
                }
                progress?.Invoke(100);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebDAV] download error: {ex.Message}");
                return false;
            }
        }

        /// <summary>新建文件夹（MKCOL）；405 视为已存在返回失败</summary>
        public async Task<bool> CreateFolderAsync(string parentDir, string name)
        {
            try
            {
                string relPath = parentDir + name.Trim().TrimEnd('/') + "/";
                using var req = CreateRequest(Mkcol, relPath);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.StatusCode == HttpStatusCode.Created;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebDAV] mkdir error: {ex.Message}");
                return false;
            }
        }

        /// <summary>删除文件或文件夹（WebDAV 规范中 DELETE 集合即递归删除）</summary>
        public async Task<bool> DeleteAsync(string relPath)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Delete, relPath);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebDAV] delete error: {ex.Message}");
                return false;
            }
        }

        /// <summary>连接测试：PROPFIND 根目录（Depth 0）。返回 (成功, 错误信息)</summary>
        public async Task<(bool Ok, string Error)> TestConnectionAsync()
        {
            try
            {
                using var req = CreateRequest(Propfind, "", new StringContent(PropfindBody, Encoding.UTF8, "application/xml"));
                req.Headers.TryAddWithoutValidation("Depth", "0");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                await resp.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 207) return (true, "");
                string text = (int)resp.StatusCode == 401 ? "401 Unauthorized" : $"HTTP {(int)resp.StatusCode}";
                return (false, text);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #endregion

        /// <summary>带进度上报的 HTTP 内容（百分比变化才回调，避免高频刷新 UI）</summary>
        private sealed class ProgressStreamContent : HttpContent
        {
            private readonly Stream _stream;
            private readonly long _totalBytes;
            private readonly Action<double>? _progress;
            private int _lastPct = -1;

            public ProgressStreamContent(Stream stream, long totalBytes, Action<double>? progress)
            {
                _stream = stream;
                _totalBytes = totalBytes;
                _progress = progress;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                long uploaded = 0;
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    uploaded += read;
                    if (_totalBytes > 0)
                    {
                        int pct = (int)(uploaded * 100 / _totalBytes);
                        if (pct != _lastPct)
                        {
                            _lastPct = pct;
                            _progress?.Invoke(pct);
                        }
                    }
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _totalBytes;
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _stream.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
