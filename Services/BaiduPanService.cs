using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SideDesk.Services
{
    #region 数据模型

    /// <summary>百度网盘文件/目录条目</summary>
    public class BaiduPanFile
    {
        [JsonProperty("fs_id")] public long FsId { get; set; }
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("server_filename")] public string Name { get; set; } = "";
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("isdir")] public int IsDir { get; set; }
        [JsonProperty("md5")] public string? Md5 { get; set; }
        [JsonProperty("server_mtime")] public long ServerMtime { get; set; }
        [JsonProperty("server_ctime")] public long ServerCtime { get; set; }
        [JsonProperty("local_mtime")] public long LocalMtime { get; set; }
        [JsonProperty("local_ctime")] public long LocalCtime { get; set; }
        [JsonProperty("category")] public int Category { get; set; }
        [JsonProperty("thumbs")] public JObject? Thumbs { get; set; }
        [JsonProperty("dlink")] public string? Dlink { get; set; }

        public bool IsFolder => IsDir == 1;

        /// <summary>格式化的文件大小</summary>
        public string SizeText => IsFolder ? "\u2014" : FormatSize(Size);

        /// <summary>右侧元信息</summary>
        public string MetaText => IsFolder ? Framework.Lang.T("文件夹") : Framework.Lang.T("文件");

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

    /// <summary>百度网盘容量信息</summary>
    public class BaiduPanQuota
    {
        [JsonProperty("total")] public long Total { get; set; }
        [JsonProperty("used")] public long Used { get; set; }
        [JsonProperty("free")] public long Free { get; set; }

        public string Summary => Framework.Lang.TF("已用 {0} / 共 {1}", BaiduPanFile.FormatSize(Used), BaiduPanFile.FormatSize(Total));
    }

    /// <summary>OAuth Token 响应</summary>
    public class BaiduTokenResponse
    {
        [JsonProperty("access_token")] public string AccessToken { get; set; } = "";
        [JsonProperty("expires_in")] public long ExpiresIn { get; set; }
        [JsonProperty("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonProperty("scope")] public string Scope { get; set; } = "";
        [JsonProperty("error")] public string? Error { get; set; }
        [JsonProperty("error_description")] public string? ErrorDescription { get; set; }
    }

    #endregion

    /// <summary>
    /// 百度网盘服务：封装 OAuth 2.0 授权 + 文件管理 API。
    /// 授权码模式：本地启动临时 HTTP 监听 → 浏览器授权 → 回调获取 code → 换取 token。
    /// </summary>
    public sealed class BaiduPanService
    {
        private static readonly Lazy<BaiduPanService> _lazy = new(() => new BaiduPanService());
        public static BaiduPanService Instance => _lazy.Value;

        // ===== 应用凭证（开发者预置） =====
        private const string RedirectUri = "http://localhost:19193/callback";
        private const int CallbackPort = 19193;

        private const string AppId = "124068982";
        private const string AppKey = "MHTGFiyNQFkkfmpxjWYTmljOAcTdNK2U";
        private const string SecretKey = "4LUcgsliGYHSZUE3Dn6z2fsFb9zYTA2L";

        // ===== API 地址 =====
        private const string AuthorizeUrl = "https://openapi.baidu.com/oauth/2.0/authorize";
        private const string TokenUrl = "https://openapi.baidu.com/oauth/2.0/token";
        private const string PanApiBase = "https://pan.baidu.com/rest/2.0/xpan";
        private const string QuotaUrl = "https://pan.baidu.com/api/quota";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly HttpClient _uploadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

        private BaiduPanService() { }

        #region Token 管理

        private static Settings AppSettings => ((App)System.Windows.Application.Current).AppSettings;

        /// <summary>当前是否已授权（有有效 access_token）</summary>
        public bool IsAuthorized
        {
            get
            {
                var s = AppSettings;
                return !string.IsNullOrEmpty(s.BaiduPanAccessToken)
                    && s.BaiduPanTokenExpiry > DateTime.Now;
            }
        }

        /// <summary>是否有 refresh_token（可用于刷新）</summary>
        public bool HasRefreshToken => !string.IsNullOrEmpty(AppSettings.BaiduPanRefreshToken);

        /// <summary>获取有效的 access_token（过期则自动刷新）</summary>
        public async Task<string?> GetValidTokenAsync()
        {
            var s = AppSettings;
            if (!string.IsNullOrEmpty(s.BaiduPanAccessToken) && s.BaiduPanTokenExpiry > DateTime.Now.AddMinutes(5))
                return s.BaiduPanAccessToken;

            // 尝试刷新
            if (!string.IsNullOrEmpty(s.BaiduPanRefreshToken))
            {
                bool refreshed = await RefreshTokenAsync();
                if (refreshed) return AppSettings.BaiduPanAccessToken;
            }
            return null;
        }

        /// <summary>刷新 access_token</summary>
        public async Task<bool> RefreshTokenAsync()
        {
            try
            {
                var s = AppSettings;
                string url = $"{TokenUrl}?grant_type=refresh_token" +
                             $"&refresh_token={Uri.EscapeDataString(s.BaiduPanRefreshToken ?? "")}" +
                             $"&client_id={AppKey}" +
                             $"&client_secret={SecretKey}";

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 刷新Token响应: {json}");

                var token = JsonConvert.DeserializeObject<BaiduTokenResponse>(json);
                if (token == null || !string.IsNullOrEmpty(token.Error) || string.IsNullOrEmpty(token.AccessToken))
                    return false;

                SaveTokens(token);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 刷新Token失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>保存 token 到设置</summary>
        private static void SaveTokens(BaiduTokenResponse token)
        {
            var s = AppSettings;
            s.BaiduPanAccessToken = token.AccessToken;
            s.BaiduPanRefreshToken = token.RefreshToken;
            s.BaiduPanTokenExpiry = DateTime.Now.AddSeconds(token.ExpiresIn);
            s.Save();
        }

        /// <summary>清除授权信息（退出登录）</summary>
        public void ClearAuth()
        {
            var s = AppSettings;
            s.BaiduPanAccessToken = null;
            s.BaiduPanRefreshToken = null;
            s.BaiduPanTokenExpiry = null;
            s.Save();
        }

        #endregion

        #region OAuth 授权流程

        /// <summary>
        /// 发起 OAuth 授权：启动本地回调监听 → 打开浏览器 → 等待用户授权 → 获取 token。
        /// 返回 true 表示授权成功。
        /// </summary>
        public async Task<bool> AuthorizeAsync()
        {
            string? code = await WaitForAuthCodeAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(code)) return false;

            return await ExchangeCodeForTokenAsync(code).ConfigureAwait(false);
        }

        /// <summary>启动本地 HTTP 监听，打开浏览器授权页，等待回调获取 code</summary>
        private async Task<string?> WaitForAuthCodeAsync()
        {
            string? authCode = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            // 启动本地 HTTP 监听
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 启动本地监听失败: {ex.Message}");
                return null;
            }

            // 构建授权 URL
            string authUrl = $"{AuthorizeUrl}?response_type=code" +
                             $"&client_id={AppKey}" +
                             $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                             $"&scope=basic,netdisk" +
                             $"&display=popup" +
                             $"&qrcode=1";

            // 打开默认浏览器
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            try
            {
                // 等待回调
                while (!cts.Token.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    var request = context.Request;
                    var response = context.Response;

                    string? code = request.QueryString["code"];
                    string? error = request.QueryString["error"];

                    // 返回一个友好的 HTML 页面
                    string html;
                    if (!string.IsNullOrEmpty(code))
                    {
                        authCode = code;
                        html = "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
                               "<h2>✅ 授权成功</h2><p>您可以关闭此页面，返回元桌继续使用百度网盘。</p></body></html>";
                    }
                    else
                    {
                        html = $"<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
                               $"<h2>❌ 授权失败</h2><p>{error ?? "未知错误"}</p></body></html>";
                    }

                    byte[] buffer = Encoding.UTF8.GetBytes(html);
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    response.Close();

                    if (!string.IsNullOrEmpty(code)) break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 等待授权回调异常: {ex.Message}");
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }

            return authCode;
        }

        /// <summary>用授权码换取 access_token</summary>
        private async Task<bool> ExchangeCodeForTokenAsync(string code)
        {
            try
            {
                string url = $"{TokenUrl}?grant_type=authorization_code" +
                             $"&code={Uri.EscapeDataString(code)}" +
                             $"&client_id={AppKey}" +
                             $"&client_secret={SecretKey}" +
                             $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 换取Token响应: {json}");

                var token = JsonConvert.DeserializeObject<BaiduTokenResponse>(json);
                if (token == null || !string.IsNullOrEmpty(token.Error) || string.IsNullOrEmpty(token.AccessToken))
                {
                    Debug.WriteLine($"[BaiduPan] 换取Token失败: {token?.Error} - {token?.ErrorDescription}");
                    return false;
                }

                SaveTokens(token);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 换取Token异常: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 文件管理 API

        /// <summary>获取指定目录下的文件列表</summary>
        public async Task<List<BaiduPanFile>?> ListFilesAsync(string dir = "/")
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return null;

                string url = $"{PanApiBase}/file?method=list" +
                             $"&access_token={token}" +
                             $"&dir={Uri.EscapeDataString(dir)}" +
                             $"&order=name&desc=0" +
                             $"&limit=1000";

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 文件列表响应({dir}): {(json.Length > 500 ? json[..500] + "..." : json)}");

                var obj = JObject.Parse(json);
                if (obj["errno"]?.Value<int>() != 0)
                {
                    Debug.WriteLine($"[BaiduPan] 文件列表错误: errno={obj["errno"]}");
                    return null;
                }

                var list = obj["list"]?.ToObject<List<BaiduPanFile>>();
                return list ?? new List<BaiduPanFile>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 获取文件列表失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>创建文件夹</summary>
        public async Task<BaiduPanFile?> MkdirAsync(string path)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return null;

                string url = $"{PanApiBase}/file?method=create&access_token={token}";
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("path", path),
                    new KeyValuePair<string, string>("isdir", "1"),
                    new KeyValuePair<string, string>("rtype", "0"),
                });

                using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 创建目录响应: {json}");

                var obj = JObject.Parse(json);
                if (obj["errno"]?.Value<int>() != 0) return null;

                return obj.ToObject<BaiduPanFile>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 创建目录失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>删除文件/目录（支持批量）</summary>
        public async Task<bool> DeleteAsync(params string[] paths)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return false;

                string url = $"{PanApiBase}/file?method=filemanager&opera=delete&access_token={token}";
                var fileList = new JArray(paths);
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("filelist", fileList.ToString(Formatting.None)),
                });

                using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 删除响应: {json}");

                var obj = JObject.Parse(json);
                return obj["errno"]?.Value<int>() == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 删除失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>获取文件下载链接（dlink）</summary>
        public async Task<string?> GetDownloadLinkAsync(long fsId)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return null;

                var ids = new JArray(fsId);
                string url = $"{PanApiBase}/multimedia?method=filemetas" +
                             $"&access_token={token}" +
                             $"&fs_ids={Uri.EscapeDataString(ids.ToString(Formatting.None))}" +
                             $"&dlink=1";

                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 下载链接响应: {(json.Length > 300 ? json[..300] + "..." : json)}");

                var obj = JObject.Parse(json);
                if (obj["errno"]?.Value<int>() != 0) return null;

                var list = obj["list"] as JArray;
                if (list == null || list.Count == 0) return null;

                return list[0]?["dlink"]?.Value<string>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 获取下载链接失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>下载文件到本地（使用 dlink + access_token）</summary>
        public async Task<bool> DownloadFileAsync(string dlink, string savePath, Action<double>? progress = null)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return false;

                // dlink 需要追加 access_token 参数
                string separator = dlink.Contains('?') ? "&" : "?";
                string url = $"{dlink}{separator}access_token={token}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("pan.baidu.com");

                using var resp = await _uploadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[BaiduPan] 下载失败: HTTP {(int)resp.StatusCode}");
                    return false;
                }

                long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                await using var contentStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int read;
                int lastPct = -1;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        int pct = (int)(downloaded * 100 / totalBytes);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Invoke(pct);
                        }
                    }
                }

                progress?.Invoke(100);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 下载文件异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>获取网盘容量信息</summary>
        public async Task<BaiduPanQuota?> GetQuotaAsync()
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return null;

                string url = $"{QuotaUrl}?access_token={token}&checkfree=1&checkexpire=1";
                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var obj = JObject.Parse(json);
                if (obj["errno"]?.Value<int>() != 0) return null;

                return obj.ToObject<BaiduPanQuota>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 获取容量失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 上传

        /// <summary>
        /// 上传本地文件到百度网盘（简单上传：≤4MB 用简单接口；>4MB 用分片上传）。
        /// </summary>
        public async Task<bool> UploadFileAsync(string localPath, string remoteDir, Action<double>? progress = null)
        {
            string fileName = Path.GetFileName(localPath);
            string remotePath = remoteDir.TrimEnd('/') + "/" + fileName;
            long fileSize = new FileInfo(localPath).Length;

            if (fileSize <= 4 * 1024 * 1024)
                return await SimpleUploadAsync(localPath, remotePath, progress).ConfigureAwait(false);
            else
                return await SliceUploadAsync(localPath, remotePath, fileSize, progress).ConfigureAwait(false);
        }

        /// <summary>简单上传（≤4MB）：使用 /file?method=upload 接口</summary>
        private async Task<bool> SimpleUploadAsync(string localPath, string remotePath, Action<double>? progress)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return false;

                string url = $"{PanApiBase}/file?method=upload&access_token={token}";

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(remotePath), "path");
                form.Add(new StringContent("0"), "rtype"); // 0=不覆盖

                byte[] fileBytes = await File.ReadAllBytesAsync(localPath).ConfigureAwait(false);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(fileContent, "file", Path.GetFileName(localPath));

                progress?.Invoke(50);
                using var resp = await _uploadHttp.PostAsync(url, form).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] 简单上传响应: {json}");

                var obj = JObject.Parse(json);
                bool ok = obj["errno"]?.Value<int>() == 0;
                if (ok) progress?.Invoke(100);
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 简单上传失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>分片上传（>4MB）：precreate → 分片上传 → create</summary>
        private async Task<bool> SliceUploadAsync(string localPath, string remotePath, long fileSize, Action<double>? progress)
        {
            try
            {
                string? token = await GetValidTokenAsync().ConfigureAwait(false);
                if (token == null) return false;

                // 计算文件 MD5（整体）和每个分片的 MD5
                var sliceMd5List = new List<string>();
                string contentMd5;
                using (var md5 = MD5.Create())
                {
                    await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    byte[] allBytes = new byte[fileSize];
                    int totalRead = 0;
                    while (totalRead < fileSize)
                    {
                        int n = await fs.ReadAsync(allBytes, totalRead, (int)Math.Min(81920, fileSize - totalRead)).ConfigureAwait(false);
                        if (n == 0) break;
                        totalRead += n;
                    }
                    contentMd5 = Convert.ToHexString(md5.ComputeHash(allBytes)).ToLowerInvariant();

                    // 分片 4MB
                    const int sliceSize = 4 * 1024 * 1024;
                    for (long offset = 0; offset < fileSize; offset += sliceSize)
                    {
                        int len = (int)Math.Min(sliceSize, fileSize - offset);
                        byte[] slice = new byte[len];
                        Array.Copy(allBytes, offset, slice, 0, len);
                        sliceMd5List.Add(Convert.ToHexString(md5.ComputeHash(slice)).ToLowerInvariant());
                    }
                }

                int blockCount = sliceMd5List.Count;

                // Step 1: precreate
                string precreateUrl = $"{PanApiBase}/file?method=precreate&access_token={token}";
                var precreateBody = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("path", remotePath),
                    new KeyValuePair<string, string>("size", fileSize.ToString()),
                    new KeyValuePair<string, string>("isdir", "0"),
                    new KeyValuePair<string, string>("autoinit", "1"),
                    new KeyValuePair<string, string>("rtype", "0"),
                    new KeyValuePair<string, string>("block_list", new JArray(sliceMd5List).ToString(Formatting.None)),
                    new KeyValuePair<string, string>("content-md5", contentMd5),
                });

                using var preResp = await _http.PostAsync(precreateUrl, precreateBody).ConfigureAwait(false);
                string preJson = await preResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] precreate响应: {preJson}");

                var preObj = JObject.Parse(preJson);
                if (preObj["errno"]?.Value<int>() != 0) return false;

                string uploadId = preObj["uploadid"]?.Value<string>() ?? "";

                // Step 2: 分片上传
                await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                const int sliceSizeUpload = 4 * 1024 * 1024;
                long uploaded = 0;

                for (int i = 0; i < blockCount; i++)
                {
                    int len = (int)Math.Min(sliceSizeUpload, fileSize - uploaded);
                    byte[] sliceData = new byte[len];
                    int read = 0;
                    while (read < len)
                    {
                        int n = await fileStream.ReadAsync(sliceData, read, len - read).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }

                    string sliceUrl = $"https://d.pcs.baidu.com/rest/2.0/pcs/superfile2" +
                                      $"?method=upload&access_token={token}" +
                                      $"&type=tmpfile&path={Uri.EscapeDataString(remotePath)}" +
                                      $"&uploadid={Uri.EscapeDataString(uploadId)}" +
                                      $"&partseq={i}";

                    using var sliceForm = new MultipartFormDataContent();
                    var sliceContent = new ByteArrayContent(sliceData);
                    sliceContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    sliceForm.Add(sliceContent, "file", "chunk");

                    using var sliceResp = await _uploadHttp.PostAsync(sliceUrl, sliceForm).ConfigureAwait(false);
                    string sliceJson = await sliceResp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var sliceObj = JObject.Parse(sliceJson);
                    if (sliceObj["error_code"] != null && sliceObj["error_code"].Value<int>() != 0)
                    {
                        Debug.WriteLine($"[BaiduPan] 分片{i}上传失败: {sliceJson}");
                        return false;
                    }

                    uploaded += len;
                    progress?.Invoke((int)(uploaded * 90 / fileSize)); // 上传占 90%
                }

                // Step 3: create（合并）
                string createUrl = $"{PanApiBase}/file?method=create&access_token={token}";
                var createBody = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("path", remotePath),
                    new KeyValuePair<string, string>("size", fileSize.ToString()),
                    new KeyValuePair<string, string>("isdir", "0"),
                    new KeyValuePair<string, string>("uploadid", uploadId),
                    new KeyValuePair<string, string>("rtype", "0"),
                });

                using var createResp = await _http.PostAsync(createUrl, createBody).ConfigureAwait(false);
                string createJson = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[BaiduPan] create响应: {createJson}");

                var createObj = JObject.Parse(createJson);
                bool ok = createObj["errno"]?.Value<int>() == 0;
                if (ok) progress?.Invoke(100);
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BaiduPan] 分片上传失败: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
