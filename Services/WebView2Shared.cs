using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace SideDesk.Services
{
    /// <summary>
    /// WebView2 共享环境：所有“默认” WebView2 控件统一使用 %LocalAppData%\SideDesk\WebView2.Shared
    /// 作为用户数据目录（缓存 / Cookie / localStorage / 网页登录态）。
    ///
    /// 为什么不用默认：不传 Environment 时 WebView2 会把用户数据目录建在 exe 同级目录，
    /// 而 Velopack 每次升级都替换版本目录 → 网页缓存/登录态被清空；安装目录只读时更会初始化失败。
    /// 放到 AppData 稳定目录后，与 sidedesk.db、配置、日志同处一地，升级不丢。
    ///
    /// 注意：插件市场页需注册自定义 scheme（desktxp://），必须使用独立的 WebView2.Market 目录
    /// （见 MarketWindow）——浏览器进程启动后 Environment 选项即锁定，不能复用此共享环境。
    /// </summary>
    public static class WebView2Shared
    {
        // 用信号量串行化环境创建：GetAsync 是 async，await CreateAsync 期间会释放 UI 线程，
        // 多个 WebView2 控件（登录窗、主窗网页面板、百度 widget、插件等）在启动时并发首次初始化，
        // 若不加锁会各自 CreateAsync 出独立的 CoreWebView2Environment 实例 → 多个浏览器进程
        // 争用同一 UserDataFolder，后到者报 E_ACCESSDENIED(0x80070005)。加锁后全局仅创建一个实例。
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static CoreWebView2Environment? _env;

        /// <summary>共享用户数据目录：%LocalAppData%\SideDesk\WebView2.Shared</summary>
        public static string UserDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDesk", "WebView2.Shared");

        /// <summary>兜底用户数据目录：主目录初始化失败（被占用 / 权限异常 / EBWebView 损坏）时降级到 %Temp%，
        /// 保证登录页与网页仍可加载（代价：该次会话缓存 / 登录态不持久）。</summary>
        private static string FallbackUserDataFolder => Path.Combine(
            Path.GetTempPath(), "SideDesk", "WebView2.Shared");

        /// <summary>
        /// 获取（并按需创建）共享 WebView2 环境。所有默认 WebView2 控件都应把它传给
        /// EnsureCoreWebView2Async，以共享同一浏览器进程与同一用户数据目录。
        /// 并发安全：全局仅创建一个实例；主目录失败时自动降级到临时目录。
        /// </summary>
        public static async Task<CoreWebView2Environment> GetAsync()
        {
            if (_env != null) return _env;
            await _gate.WaitAsync();
            try
            {
                if (_env != null) return _env;   // double-check：并发调用只创建一次
                try
                {
                    _env = await CreateEnvAsync(UserDataFolder);
                }
                catch (Exception ex)
                {
                    Log($"primary UDF '{UserDataFolder}' init failed: {ex.Message}; falling back to '{FallbackUserDataFolder}'");
                    _env = await CreateEnvAsync(FallbackUserDataFolder);
                }
                return _env;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<CoreWebView2Environment> CreateEnvAsync(string userDataFolder)
        {
            Directory.CreateDirectory(userDataFolder);
            return await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
        }

        /// <summary>把初始化异常写入 %LocalAppData%\SideDesk\Logs\webview2.log，便于事后诊断（写日志失败静默）。</summary>
        private static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk", "Logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "webview2.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
