using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// S3 存储服务：基于 AWS Signature V4 的 S3（及兼容协议）操作封装。
    /// 支持列目录（前缀 + 分隔符模拟文件夹）、上传、下载、删除、新建文件夹、容量统计与连接测试。
    /// </summary>
    public sealed class S3StorageService
    {
        private readonly S3Source _src;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
        private const string Service = "s3";

        /// <summary>最近一次上传失败的原因（已本地化，成功或从未失败为 null）</summary>
        public string? LastUploadError { get; private set; }

        public S3StorageService(S3Source source)
        {
            _src = source;
        }

        #region 寻址与签名基础

        /// <summary>服务端点主机名（不含 scheme，如 s3.us-east-1.amazonaws.com 或自定义端点）</summary>
        private string EndpointHost
        {
            get
            {
                string ep = (_src.Endpoint ?? "").Trim();
                if (string.IsNullOrEmpty(ep))
                    return $"s3.{_src.Region}.amazonaws.com";
                ep = ep.Replace("https://", "").Replace("http://", "");
                return ep.TrimEnd('/');
            }
        }

        /// <summary>请求实际访问的 host（虚拟主机风格带桶前缀）与 URL 前缀</summary>
        private (string Host, string UrlBase, string CanonicalBase) GetAddressing()
        {
            string host = EndpointHost;
            if (_src.UsePathStyle)
                return (host, $"https://{host}/{_src.Bucket}", $"/{_src.Bucket}");
            return ($"{_src.Bucket}.{host}", $"https://{_src.Bucket}.{host}", "");
        }

        /// <summary>对象 key 的 URL 路径编码（保留 / 分段，各段 RFC3986 转义）</summary>
        private static string EncodeKey(string key)
            => string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        /// <summary>构建 canonical query string（按键排序，键值均做 RFC3986 转义）</summary>
        private static string CanonicalQuery(IReadOnlyList<KeyValuePair<string, string>> query)
        {
            if (query.Count == 0) return "";
            var sorted = query.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            return string.Join("&", sorted.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        }

        /// <summary>发送 SigV4 签名请求（上传用 UNSIGNED-PAYLOAD 流式，其余计算载荷哈希）</summary>
        private async Task<HttpResponseMessage> SendSignedAsync(
            HttpMethod method, string key, IReadOnlyList<KeyValuePair<string, string>> query,
            HttpContent? content, bool unsignedPayload)
        {
            var (_, urlBase, canonicalBase) = GetAddressing();
            string encodedKey = EncodeKey(key);
            string url = $"{urlBase}/{encodedKey}";
            string canonicalQuery = CanonicalQuery(query);
            if (canonicalQuery.Length > 0) url += "?" + canonicalQuery;

            string amzDate = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string dateStamp = DateTime.UtcNow.ToString("yyyyMMdd");
            string payloadHash = unsignedPayload ? "UNSIGNED-PAYLOAD" : Sha256Hex(Array.Empty<byte>());

            var req = new HttpRequestMessage(method, url) { Content = content };
            req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            string? sessionToken = _src.SessionToken;
            if (!string.IsNullOrEmpty(sessionToken))
                req.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);

            // canonical headers（按名称排序，host 取请求实际 host）
            string host = new Uri(url).Host;
            var headerPairs = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = host,
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"] = amzDate
            };
            if (!string.IsNullOrEmpty(sessionToken))
                headerPairs["x-amz-security-token"] = sessionToken;

            string canonicalHeaders = string.Concat(headerPairs.Select(p => $"{p.Key}:{p.Value}\n"));
            string signedHeaders = string.Join(";", headerPairs.Keys);
            string canonicalUri = $"{canonicalBase}/{encodedKey}";

            string canonicalRequest = $"{method.Method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

            string scope = $"{dateStamp}/{_src.Region}/{Service}/aws4_request";
            string stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

            byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _src.SecretAccessKey), dateStamp);
            byte[] kRegion = HmacSha256(kDate, _src.Region);
            byte[] kService = HmacSha256(kRegion, Service);
            byte[] kSigning = HmacSha256(kService, "aws4_request");
            string signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();

            req.Headers.TryAddWithoutValidation("Authorization",
                $"AWS4-HMAC-SHA256 Credential={_src.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

            System.Diagnostics.Debug.WriteLine($"[S3] {method.Method} {url}");
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }

        #endregion

        #region 列目录

        /// <summary>
        /// 列出指定前缀下的"目录"内容：子文件夹（CommonPrefixes）+ 文件（Contents）。
        /// 返回的文件夹 Id 为完整前缀（以 / 结尾），文件 Id 为完整对象 key。
        /// </summary>
        public async Task<List<CloudDriveFile>?> ListDirectoryAsync(string prefix)
        {
            try
            {
                var result = new List<CloudDriveFile>();
                string? token = null;
                do
                {
                    var query = new List<KeyValuePair<string, string>>
                    {
                        new("list-type", "2"),
                        new("delimiter", "/"),
                        new("prefix", prefix)
                    };
                    if (!string.IsNullOrEmpty(token)) query.Add(new("continuation-token", token));

                    using var resp = await SendSignedAsync(HttpMethod.Get, "", query, null, unsignedPayload: false).ConfigureAwait(false);
                    string xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"[S3] list failed: HTTP {(int)resp.StatusCode} - {xml}");
                        return null;
                    }

                    var doc = XDocument.Parse(xml);
                    var nodes = doc.Descendants().ToList();

                    // 子文件夹
                    foreach (var cp in nodes.Where(e => e.Name.LocalName == "CommonPrefixes"))
                    {
                        string? full = cp.Elements().FirstOrDefault(e => e.Name.LocalName == "Prefix")?.Value;
                        if (string.IsNullOrEmpty(full) || full == prefix) continue;
                        string name = full.Substring(prefix.Length).TrimEnd('/');
                        if (string.IsNullOrEmpty(name)) continue;
                        result.Add(new CloudDriveFile { Id = full, Name = name, IsFolder = true });
                    }

                    // 文件（跳过目录标记对象与自身占位）
                    foreach (var c in nodes.Where(e => e.Name.LocalName == "Contents"))
                    {
                        string? key = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
                        if (string.IsNullOrEmpty(key) || key == prefix || key.EndsWith('/')) continue;
                        long size = long.TryParse(c.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value, out var s) ? s : 0;
                        result.Add(new CloudDriveFile { Id = key, Name = key.Substring(prefix.Length), IsFolder = false, FileSize = size });
                    }

                    token = nodes.FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value == "true"
                        ? nodes.FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value
                        : null;
                } while (!string.IsNullOrEmpty(token));

                // 文件夹在前，名称升序
                return result
                    .OrderByDescending(f => f.IsFolder)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[S3] list error: {ex.Message}");
                return null;
            }
        }

        /// <summary>遍历指定前缀下的所有对象（递归，自动翻页），回调 (key, size)</summary>
        private async Task<bool> ForEachObjectAsync(string prefix, Func<string, long, Task> onObject)
        {
            string? token = null;
            do
            {
                var query = new List<KeyValuePair<string, string>>
                {
                    new("list-type", "2"),
                    new("prefix", prefix)
                };
                if (!string.IsNullOrEmpty(token)) query.Add(new("continuation-token", token));

                using var resp = await SendSignedAsync(HttpMethod.Get, "", query, null, unsignedPayload: false).ConfigureAwait(false);
                string xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;

                var nodes = XDocument.Parse(xml).Descendants().ToList();
                foreach (var c in nodes.Where(e => e.Name.LocalName == "Contents"))
                {
                    string? key = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
                    if (string.IsNullOrEmpty(key)) continue;
                    long size = long.TryParse(c.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value, out var s) ? s : 0;
                    await onObject(key, size).ConfigureAwait(false);
                }

                token = nodes.FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value == "true"
                    ? nodes.FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value
                    : null;
            } while (!string.IsNullOrEmpty(token));
            return true;
        }

        /// <summary>递归统计前缀下所有对象总大小（字节）</summary>
        public async Task<long> GetTotalUsedBytesAsync(string rootPrefix)
        {
            long total = 0;
            await ForEachObjectAsync(rootPrefix, (_, size) => { total += size; return Task.CompletedTask; }).ConfigureAwait(false);
            return total;
        }

        #endregion

        #region 上传 / 下载 / 删除 / 新建文件夹

        /// <summary>上传本地文件到指定前缀下（UNSIGNED-PAYLOAD 流式 + 进度回调）</summary>
        public async Task<bool> UploadFileAsync(string localPath, string parentPrefix, Action<double>? progress = null)
        {
            LastUploadError = null;
            try
            {
                string key = parentPrefix + Path.GetFileName(localPath);
                var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                using var content = new ProgressStreamContent(fileStream, fileStream.Length, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var resp = await SendSignedAsync(HttpMethod.Put, key, Array.Empty<KeyValuePair<string, string>>(), content, unsignedPayload: true).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[S3] upload failed: HTTP {(int)resp.StatusCode} - {err}");
                    LastUploadError = resp.StatusCode == HttpStatusCode.Forbidden
                        ? Framework.Lang.T("cloudDrive.uploadNoWritePermission")
                        : Framework.Lang.T("cloudDrive.uploadFailedReason");
                    return false;
                }
                await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                progress?.Invoke(100);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[S3] upload error: {ex.Message}");
                LastUploadError = Framework.Lang.T("cloudDrive.uploadFailedReason");
                return false;
            }
        }

        /// <summary>
        /// 上传前预检：试列目标前缀以验证连接与凭证（能列≠一定能写，只读凭证仍靠上传失败兜底）。
        /// 返回 null=放行；非 null=拦截原因（已本地化）。
        /// </summary>
        public async Task<string?> ProbeWritableAsync(string parentPrefix)
        {
            var probe = await ListDirectoryAsync(parentPrefix).ConfigureAwait(false);
            return probe == null ? Framework.Lang.T("cloudDrive.uploadProbeFailed") : null;
        }

        /// <summary>下载对象到本地文件（进度回调，百分比基于 Content-Length）</summary>
        public async Task<bool> DownloadToFileAsync(string key, string savePath, Action<double>? progress = null)
        {
            try
            {
                using var resp = await SendSignedAsync(HttpMethod.Get, key, Array.Empty<KeyValuePair<string, string>>(), null, unsignedPayload: false).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[S3] download failed: HTTP {(int)resp.StatusCode} - {err}");
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
                System.Diagnostics.Debug.WriteLine($"[S3] download error: {ex.Message}");
                return false;
            }
        }

        /// <summary>新建文件夹（写入 0 字节的 "目录标记" 对象）</summary>
        public async Task<bool> CreateFolderAsync(string parentPrefix, string name)
        {
            try
            {
                string key = parentPrefix + name.Trim().TrimEnd('/') + "/";
                using var content = new ByteArrayContent(Array.Empty<byte>());
                using var resp = await SendSignedAsync(HttpMethod.Put, key, Array.Empty<KeyValuePair<string, string>>(), content, unsignedPayload: false).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[S3] mkdir error: {ex.Message}");
                return false;
            }
        }

        /// <summary>删除单个对象</summary>
        public async Task<bool> DeleteObjectAsync(string key)
        {
            try
            {
                using var resp = await SendSignedAsync(HttpMethod.Delete, key, Array.Empty<KeyValuePair<string, string>>(), null, unsignedPayload: false).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[S3] delete error: {ex.Message}");
                return false;
            }
        }

        /// <summary>递归删除前缀下所有对象（含目录标记），单个失败不中断整体</summary>
        public async Task<bool> DeletePrefixAsync(string prefix)
        {
            var keys = new List<string>();
            bool listed = await ForEachObjectAsync(prefix, (key, _) => { keys.Add(key); return Task.CompletedTask; }).ConfigureAwait(false);
            if (!listed) return false;
            bool allOk = true;
            foreach (string key in keys)
            {
                if (!await DeleteObjectAsync(key).ConfigureAwait(false)) allOk = false;
            }
            return allOk;
        }

        /// <summary>连接测试：列出根前缀首个对象。返回 (成功, 错误信息)</summary>
        public async Task<(bool Ok, string Error)> TestConnectionAsync()
        {
            try
            {
                var query = new List<KeyValuePair<string, string>>
                {
                    new("list-type", "2"),
                    new("max-keys", "1"),
                    new("prefix", _src.NormalizedPrefix)
                };
                using var resp = await SendSignedAsync(HttpMethod.Get, "", query, null, unsignedPayload: false).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (true, "");

                // 提取 S3 错误码便于用户定位
                string code = "";
                try { code = XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? ""; }
                catch { }
                return (false, $"HTTP {(int)resp.StatusCode} {code}".Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #endregion

        /// <summary>带进度上报的 HTTP 内容（与元桌云盘上传相同策略：百分比变化才回调）</summary>
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
