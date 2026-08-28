using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// 网络请求日志：把发往后端的 request/response 全量写入本地日志文件，便于排查同步问题。
    /// 日志文件：%LocalAppData%\SideDesk\Logs\http.log（超过大小上限自动滚动为 http.old.log）。
    /// <see cref="Enabled"/> 为总开关（读取 Settings.HttpLogEnabled，默认开启）；
    /// 请求/响应体超长时截断，Authorization 头自动脱敏。
    /// </summary>
    public static class HttpLog
    {
        /// <summary>是否写日志（由启动时从 Settings 读取，运行时可随时切换）</summary>
        public static bool Enabled = true;

        /// <summary>单条请求/响应体的最大记录长度，超出截断</summary>
        private const int MaxBodyLen = 4000;

        /// <summary>日志文件最大字节数，超出滚动</summary>
        private const long MaxFileSize = 8 * 1024 * 1024;

        private static readonly object _lock = new();
        private static readonly string _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "Logs");
        private static readonly string _filePath = Path.Combine(_dir, "http.log");

        /// <summary>日志文件完整路径（供 UI 展示/打开）</summary>
        public static string FilePath => _filePath;

        /// <summary>
        /// 发送请求并记录日志。返回的响应内容已缓冲为字符串，调用方可照常 ReadAsStringAsync。
        /// 关闭日志时等价于直接 http.SendAsync。
        /// </summary>
        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage req)
        {
            if (!Enabled)
                return await http.SendAsync(req).ConfigureAwait(false);

            // 读取请求体（发送前读取不影响发送：StringContent 可重复读取）
            string? reqBody = null;
            try
            {
                if (req.Content != null)
                    reqBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch { }

            Log($"--> {req.Method} {req.RequestUri}{FormatAuth(req)}\n{Truncate(reqBody)}");

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"<-- !! {req.Method} {req.RequestUri} 请求异常: {ex.Message}");
                throw;
            }

            // 缓冲响应体后重建 Content，调用方可继续读取
            string respBody = "";
            try { respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
            resp.Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json");

            Log($"<-- {(int)resp.StatusCode} {req.Method} {req.RequestUri}\n{Truncate(respBody)}");
            return resp;
        }

        private static string FormatAuth(HttpRequestMessage req)
        {
            string? auth = null;
            if (req.Headers.TryGetValues("Authorization", out var vals))
            {
                foreach (var v in vals) { auth = v; break; }
            }
            if (string.IsNullOrEmpty(auth)) return "";
            // 脱敏：Bearer 后仅保留前 8 位
            if (auth!.StartsWith("Bearer ") && auth.Length > 15)
                auth = "Bearer " + auth.Substring(7, 8) + "****";
            return $" [Auth: {auth}]";
        }

        private static string Truncate(string? s)
        {
            if (string.IsNullOrEmpty(s)) return Framework.Lang.T("system.noContent");
            if (s!.Length <= MaxBodyLen) return s;
            return s.Substring(0, MaxBodyLen) + $"\n...[已截断，总长 {s.Length}]";
        }

        private static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                    var info = new FileInfo(_filePath);
                    if (info.Exists && info.Length > MaxFileSize)
                    {
                        string old = Path.Combine(_dir, "http.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_filePath, old);
                    }
                    File.AppendAllText(_filePath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n\n");
                }
            }
            catch
            {
                // 日志写入失败不影响网络请求本身
            }
        }
    }
}
