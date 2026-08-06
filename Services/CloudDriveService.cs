using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    #region API 数据模型

    /// <summary>STS 临时凭证（用于 OSS 直传）</summary>
    public class StsTokenResult
    {
        [JsonProperty("accessKeyId")] public string AccessKeyId { get; set; } = "";
        [JsonProperty("accessKeySecret")] public string AccessKeySecret { get; set; } = "";
        [JsonProperty("securityToken")] public string SecurityToken { get; set; } = "";
        [JsonProperty("bucket")] public string Bucket { get; set; } = "";
        [JsonProperty("region")] public string Region { get; set; } = "";
        [JsonProperty("expiration")] public string? Expiration { get; set; }
    }

    /// <summary>云盘文件/目录条目</summary>
    public class CloudDriveFile
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("userId")] public long UserId { get; set; }
        [JsonProperty("corpId")] public long CorpId { get; set; }
        [JsonProperty("parentId")] public string ParentId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("path")] public string? Path { get; set; }
        [JsonProperty("isFolder")] public bool IsFolder { get; set; }
        [JsonProperty("ossKey")] public string? OssKey { get; set; }
        [JsonProperty("fileSize")] public long? FileSize { get; set; }
        [JsonProperty("md5")] public string? Md5 { get; set; }
        [JsonProperty("version")] public int Version { get; set; }

        /// <summary>格式化的文件大小</summary>
        public string SizeText => IsFolder ? "\u2014" : FormatSize(FileSize ?? 0);

        /// <summary>版本标记</summary>
        public string VersionText => Version > 1 ? $"v{Version}" : "";

        /// <summary>右侧元信息：文件夹显示类型，文件显示版本号</summary>
        public string MetaText => IsFolder ? "文件夹" : (Version > 1 ? $"v{Version}" : "文件");

        /// <summary>文件类型图标</summary>
        public string TypeIcon => IsFolder ? "\U0001F4C1" : GetFileIcon(Name);

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static string GetFileIcon(string name)
        {
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "\U0001F5BC\uFE0F",
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" => "\U0001F3AC",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "\U0001F3B5",
                ".doc" or ".docx" => "\U0001F4DD",
                ".xls" or ".xlsx" or ".csv" => "\U0001F4CA",
                ".ppt" or ".pptx" => "\U0001F4FD\uFE0F",
                ".pdf" => "\U0001F4D5",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\U0001F4E6",
                ".txt" or ".md" or ".log" => "\U0001F4C4",
                ".exe" or ".msi" => "\u2699\uFE0F",
                ".json" or ".xml" or ".html" or ".css" or ".js" or ".cs" => "\U0001F4BB",
                _ => "\U0001F4C4"
            };
        }
    }

    #endregion

    /// <summary>
    /// 元桌云盘服务：封装云盘文件管理 API。
    /// 上传流程：获取 STS 临时凭证 → OSS V1 签名直传 → POST /cloud-drive/register 入库。
    /// </summary>
    public sealed class CloudDriveService
    {
        private static readonly Lazy<CloudDriveService> _lazy = new(() => new CloudDriveService());
        public static CloudDriveService Instance => _lazy.Value;

        private const string BaseUrl = Constants.URLs.ServerBaseUrl;
        /// <summary>企业 ID（动态读取：选中团队时用团队 ID，否则用默认个人 ID）</summary>
        private static string CorpId
        {
            get
            {
                try
                {
                    var app = (SideDesk.App)System.Windows.Application.Current;
                    var selected = app?.AppSettings?.SelectedCorpId;
                    return string.IsNullOrEmpty(selected) ? "123" : selected;
                }
                catch { return "123"; }
            }
        }
        /// <summary>Agent ID（可选）</summary>
        private const string AgentId = "";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient _uploadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

        private CloudDriveService() { }

        private static string GetToken()
        {
            var app = (SideDesk.App)System.Windows.Application.Current;
            return app.AppSettings.AccountToken ?? "";
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + GetToken());
            return req;
        }

        /// <summary>解析通用响应 { code: 200, data: ... }，返回 data 的 JToken；失败返回 null</summary>
        private static async Task<JToken?> ParseResponseTokenAsync(HttpResponseMessage resp)
        {
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[CloudDrive] {(int)resp.StatusCode}: {json}");
            if (!resp.IsSuccessStatusCode) return null;

            var root = JObject.Parse(json);
            if (root["code"]?.Value<int>() != 200) return null;
            return root["data"];
        }

        #region 1. 获取 STS Token

        /// <summary>POST /common/upload/request-sts-token?corpid=xxx&agentid=xxx</summary>
        public async Task<StsTokenResult?> RequestStsTokenAsync()
        {
            try
            {
                string url = $"{BaseUrl}/api/common/upload/request-sts-token?corpid={CorpId}";
                if (!string.IsNullOrEmpty(AgentId)) url += $"&agentid={AgentId}";

                string token = GetToken();
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] STS请求: POST {url}");
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] STS Token: Bearer {token[..Math.Min(token.Length, 30)]}...");

                using var req = CreateRequest(HttpMethod.Post, url);
                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;

                return obj.ToObject<StsTokenResult>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 获取STS凭证失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 2. 上传文件入库

        /// <summary>POST /cloud-drive/register — 直传OSS后调用，同名文件自动版本+1</summary>
        public async Task<CloudDriveFile?> RegisterFileAsync(string parentId, string name, string ossKey, long fileSize, string? md5)
        {
            try
            {
                var body = new JObject
                {
                    ["parentId"] = parentId,
                    ["name"] = name,
                    ["ossKey"] = ossKey,
                    ["fileSize"] = fileSize,
                    ["corpId"] = long.Parse(CorpId)
                };
                if (!string.IsNullOrEmpty(md5)) body["md5"] = md5;

                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/cloud-drive/register");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;

                return obj.ToObject<CloudDriveFile>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 文件入库失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 3. 新建目录

        /// <summary>POST /cloud-drive/mkdir</summary>
        public async Task<CloudDriveFile?> MkdirAsync(string parentId, string name)
        {
            try
            {
                var body = new JObject
                {
                    ["parentId"] = parentId,
                    ["name"] = name,
                    ["corpId"] = long.Parse(CorpId)
                };

                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/cloud-drive/mkdir");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JObject obj) return null;

                return obj.ToObject<CloudDriveFile>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 新建目录失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 4. 删除文件/目录

        /// <summary>POST /cloud-drive/delete — 删除目录会递归删除子内容</summary>
        public async Task<bool> DeleteAsync(string id)
        {
            try
            {
                var body = new JObject { ["id"] = id };
                using var req = CreateRequest(HttpMethod.Post, $"{BaseUrl}/api/cloud-drive/delete");
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 删除响应: {json}");
                if (!resp.IsSuccessStatusCode) return false;
                var root = JObject.Parse(json);
                return root["code"]?.Value<int>() == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 删除失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 5. 列出目录内容

        /// <summary>GET /cloud-drive/list?parentId=0&corpId=1 — 返回数组（文件夹排前，名称升序）</summary>
        public async Task<List<CloudDriveFile>?> ListFilesAsync(string parentId = "0")
        {
            try
            {
                string url = $"{BaseUrl}/api/cloud-drive/list?parentId={parentId}&corpId={CorpId}";
                using var req = CreateRequest(HttpMethod.Get, url);
                using var resp = await HttpLog.SendAsync(_http, req).ConfigureAwait(false);
                var data = await ParseResponseTokenAsync(resp).ConfigureAwait(false);
                if (data is not JArray arr) return null;

                return arr.ToObject<List<CloudDriveFile>>() ?? new List<CloudDriveFile>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] 获取文件列表失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 6. 统计已用容量

        /// <summary>递归统计整个云盘已用容量（字节）：广度优先遍历所有目录累加文件大小</summary>
        public async Task<long> GetTotalUsedBytesAsync()
        {
            long total = 0;
            var queue = new Queue<string>();
            queue.Enqueue("0");
            while (queue.Count > 0)
            {
                string pid = queue.Dequeue();
                var files = await ListFilesAsync(pid).ConfigureAwait(false);
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

        #region 上传完整流程

        /// <summary>
        /// 上传本地文件到云盘（完整流程：STS凭证 → OSS直传 → register入库）。
        /// ossKey 格式: cloud-drive/{corpid}/{fileName}
        /// </summary>
        public async Task<bool> UploadFileAsync(string localPath, string parentId, Action<double>? progress = null)
        {
            string fileName = Path.GetFileName(localPath);
            long fileSize = new FileInfo(localPath).Length;

            // 1. 获取 STS 临时凭证
            var sts = await RequestStsTokenAsync().ConfigureAwait(false);
            if (sts == null || string.IsNullOrEmpty(sts.AccessKeyId))
                return false;

            // 2. 构建 ossKey
            string ossKey = $"cloud-drive/{CorpId}/{fileName}";

            // 3. 计算文件 MD5
            string md5 = await ComputeFileMd5Async(localPath).ConfigureAwait(false);

            // 4. OSS V1 签名直传
            bool uploaded = await PutFileToOssAsync(sts, ossKey, localPath, progress).ConfigureAwait(false);
            if (!uploaded) return false;

            // 5. 调 register 入库
            var result = await RegisterFileAsync(parentId, fileName, ossKey, fileSize, md5).ConfigureAwait(false);
            return result != null;
        }

        /// <summary>OSS V1 签名直传（HMAC-SHA1）</summary>
        private static async Task<bool> PutFileToOssAsync(StsTokenResult sts, string ossKey, string localPath, Action<double>? progress)
        {
            try
            {
                // https://{bucket}.{region}.aliyuncs.com/{ossKey}
                string url = $"https://{sts.Bucket}.{sts.Region}.aliyuncs.com/{ossKey}";
                string date = DateTime.UtcNow.ToString("R");
                string contentType = "application/octet-stream";

                // OSS V1 签名
                string canonicalizedOssHeaders = $"x-oss-security-token:{sts.SecurityToken}\n";
                string canonicalizedResource = $"/{sts.Bucket}/{ossKey}";
                string stringToSign = $"PUT\n\n{contentType}\n{date}\n{canonicalizedOssHeaders}{canonicalizedResource}";
                string signature = ComputeOssSignature(sts.AccessKeySecret, stringToSign);

                var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                using var content = new ProgressStreamContent(fileStream, fileStream.Length, progress);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Date", date);
                req.Headers.TryAddWithoutValidation("Authorization", $"OSS {sts.AccessKeyId}:{signature}");
                req.Headers.TryAddWithoutValidation("x-oss-security-token", sts.SecurityToken);

                using var resp = await _uploadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[CloudDrive] OSS上传失败: HTTP {(int)resp.StatusCode} - {errBody}");
                    return false;
                }

                await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                progress?.Invoke(100);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudDrive] OSS上传异常: {ex.Message}");
                return false;
            }
        }

        private static string ComputeOssSignature(string accessKeySecret, string stringToSign)
        {
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            return Convert.ToBase64String(hash);
        }

        /// <summary>带进度上报的 HTTP 内容：按块写入请求流，百分比变化时才回调（避免高频刷新 UI）</summary>
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

        private static async Task<string> ComputeFileMd5Async(string filePath)
        {
            using var md5 = MD5.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            byte[] hash = await md5.ComputeHashAsync(stream).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        #endregion

        #region 下载

        /// <summary>根据 ossKey 构建 OSS 下载地址（需 bucket 公读或后续改为签名URL）</summary>
        public string? BuildDownloadUrl(CloudDriveFile file)
        {
            if (file.IsFolder || string.IsNullOrEmpty(file.OssKey)) return null;
            // 暂时使用公读地址；若 bucket 为私有则需后端提供签名下载接口
            return $"https://oneshop-disk.oss-cn-shanghai.aliyuncs.com/{file.OssKey}";
        }

        #endregion
    }
}
