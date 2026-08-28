using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SideDesk.Services.Plugins;

/// <summary>
/// 插件市场本地桥：处理 desktxp://sidedesk/* 自定义协议请求（由 MarketWindow 的 WebView2 拦截转发），
/// 无需 WebSocket。安装/拉源码为 fire-and-forget：立即返回，完成后由宿主弹窗通知，前端轮询已装列表刷新状态。
/// 协议：desktxp://sidedesk/plugins 查询已装；/install?id=xx 安装；/edit?id=xx&amp;key=xx 拉源码。
/// </summary>
public static class PluginMarketBridge
{
    /// <summary>
    /// 分发自定义协议请求，返回 (HTTP 状态码, JSON 响应体)。
    /// 下载地址与鉴权由宿主自行拼接（基址 Constants.URLs.ServerBaseUrl，token 取本地登录态）。
    /// </summary>
    public static (int Status, string Json) HandleScheme(string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
            if (!uri.Host.Equals("sidedesk", StringComparison.OrdinalIgnoreCase))
                return (404, "{\"ok\":false,\"message\":\"unknown host\"}");

            var query = ParseQuery(uri.Query);
            var baseUrl = Constants.URLs.ServerBaseUrl;
            var token = (Application.Current as App)?.AppSettings.AccountToken ?? "";
            var authorization = string.IsNullOrEmpty(token) ? "" : "Bearer " + token;

            switch (uri.AbsolutePath.TrimEnd('/'))
            {
                case "/plugins":
                    return (200, GetInstalled());

                case "/install":
                {
                    var id = query.GetValueOrDefault("id") ?? "";
                    if (string.IsNullOrEmpty(id)) return (400, "{\"ok\":false,\"message\":\"missing id\"}");
                    Install($"{baseUrl}/api/plugin-market/download/{Uri.EscapeDataString(id)}", authorization);
                    return (200, "{\"ok\":true}");
                }

                case "/edit":
                {
                    var id = query.GetValueOrDefault("id") ?? "";
                    var key = query.GetValueOrDefault("key") ?? "";
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
                        return (400, "{\"ok\":false,\"message\":\"missing id or key\"}");
                    EditSource(key, $"{baseUrl}/api/plugin-market/download-source/{Uri.EscapeDataString(id)}");
                    return (200, "{\"ok\":true}");
                }

                default:
                    return (404, "{\"ok\":false,\"message\":\"unknown path\"}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginMarketBridge] HandleScheme error: {ex.Message}");
            return (500, "{\"ok\":false,\"message\":\"internal error\"}");
        }
    }

    /// <summary>解析 query 参数（手动解析，避免额外依赖）</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            dict[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return dict;
    }

    /// <summary>查询本地已安装插件（返回 JSON 字符串 [{id, version, name}]）</summary>
    public static string GetInstalled()
    {
        try
        {
            var configs = PluginLoaderService.Instance.ScanAllPluginConfigs();
            var list = configs.ConvertAll(c => new { id = c.Id, version = c.Version, name = c.Name });
            return Newtonsoft.Json.JsonConvert.SerializeObject(list);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginMarketBridge] GetInstalled failed: {ex.Message}");
            return "[]";
        }
    }

    /// <summary>安装/更新插件：下载 zip → 安全解压到 plugins 目录 → 热加载（fire-and-forget）</summary>
    public static void Install(string url, string authorization)
    {
        if (string.IsNullOrEmpty(url)) return;
        _ = Task.Run(() => InstallAsync(url, authorization));
    }

    /// <summary>导入本地插件 zip 包：安全解压 → 校验 plugin.json → 部署到 plugins 目录 → 热加载（fire-and-forget）</summary>
    public static void InstallFromLocalZip(string zipPath)
    {
        if (string.IsNullOrEmpty(zipPath) || !System.IO.File.Exists(zipPath)) return;
        _ = Task.Run(() => InstallFromZipFileAsync(zipPath, Framework.Lang.T("market.importPlugin")));
    }

    /// <summary>拉取开源插件源码到插件编辑器目录，并打开编辑器（fire-and-forget）</summary>
    public static void EditSource(string pluginKey, string url)
    {
        if (string.IsNullOrEmpty(pluginKey) || string.IsNullOrEmpty(url)) return;
        _ = Task.Run(() => EditSourceAsync(pluginKey, url));
    }

    // ==================== 内部实现 ====================

    private static async Task InstallAsync(string url, string authorization)
    {
        var notifyTitle = Framework.Lang.T("market.pluginMarket");
        var tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sd_market_{Guid.NewGuid():N}.zip");
        try
        {
            // 1. 下载插件包（付费插件需携带 Authorization 头）
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(authorization))
                    req.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    Notify(false, code == 403
                        ? Framework.Lang.T("market.downloadRequiresLoginOrPurchase")
                        : Framework.Lang.TF("market.pluginDownloadFailedHttp", code), notifyTitle);
                    return;
                }
                await using var fs = System.IO.File.Create(tempZip);
                await resp.Content.CopyToAsync(fs);
            }

            // 2. 复用本地 zip 安装通用流程（解压校验 → 部署 → 加载）
            await InstallFromZipFileAsync(tempZip, notifyTitle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginMarketBridge] install failed: {ex.Message}");
            Notify(false, Framework.Lang.TF("market.pluginInstallFailed", ex.Message), notifyTitle);
        }
        finally
        {
            try { if (System.IO.File.Exists(tempZip)) System.IO.File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// 本地 zip 插件包通用安装流程：安全解压到同卷暂存 → 定位并校验 plugin.json →
    /// 卸载同 ID 旧插件 → 移动到 plugins/&lt;插件ID&gt; → 热加载。不删除源 zip（由调用方决定）。
    /// </summary>
    private static async Task InstallFromZipFileAsync(string zipPath, string notifyTitle)
    {
        var pluginsDir = PluginLoaderService.Instance.PluginsDirectory;
        // 解压到 plugins 目录内的暂存目录（同卷）：目标目录名取决于 zip 内 plugin.json，
        // 需先解压校验再 Move 到位；同卷 Move 不会跨卷失败，也不会把未校验内容暴露给插件扫描
        var stagingDir = System.IO.Path.Combine(pluginsDir, $".staging_{Guid.NewGuid():N}");
        try
        {
            // 1. 安全解压到同卷暂存目录（拦截 zip slip 越界路径）
            SafeExtractZip(zipPath, stagingDir);

            // 2. 定位插件根目录（兼容带/不带根文件夹的 zip）
            var pluginRoot = FindPluginRoot(stagingDir);
            if (pluginRoot == null)
            {
                Notify(false, Framework.Lang.T("market.pluginPackageMissingManifest"), notifyTitle);
                return;
            }
            var configPath = System.IO.Path.Combine(pluginRoot, "plugin.json");
            var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfig>(
                System.IO.File.ReadAllText(configPath));
            if (cfg == null || string.IsNullOrEmpty(cfg.Id))
            {
                Notify(false, Framework.Lang.T("market.pluginManifestMissingId"), notifyTitle);
                return;
            }

            // 3. 目标目录：plugins/<插件ID>；已装同 ID 插件先卸载并清理旧目录
            var folderName = SanitizeDirName(cfg.Id);
            var targetDir = System.IO.Path.Combine(PluginLoaderService.Instance.PluginsDirectory, folderName);
            await DispatcherInvoke(() => PluginLoaderService.Instance.UnloadPlugin(cfg.Id));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(200);
            if (System.IO.Directory.Exists(targetDir))
                System.IO.Directory.Delete(targetDir, true);

            // 4. 同卷移动到最终目录并加载
            MoveDirectory(pluginRoot, targetDir);
            LoadedPlugin? loaded = null;
            await DispatcherInvoke(() => loaded = PluginLoaderService.Instance.LoadDevPlugin(targetDir));

            if (loaded != null)
                Notify(true, Framework.Lang.TF("market.pluginInstalledSuccess", loaded.Config.Name), notifyTitle);
            else
                Notify(false, Framework.Lang.T("market.pluginInstalledLoadFailedRetry"), notifyTitle);

            // 设置窗口若开着，同步刷新插件列表
            await DispatcherInvoke(() =>
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is SettingsWindow sw) sw.RefreshPluginList();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginMarketBridge] install failed: {ex.Message}");
            Notify(false, Framework.Lang.TF("market.pluginInstallFailed", ex.Message), notifyTitle);
        }
        finally
        {
            try { if (System.IO.Directory.Exists(stagingDir)) System.IO.Directory.Delete(stagingDir, true); } catch { }
        }
    }

    private static async Task EditSourceAsync(string pluginKey, string url)
    {
        var devBaseDir = PluginBuildService.Instance.PluginsOutputDir;
        var tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sd_market_src_{Guid.NewGuid():N}.zip");
        // 同卷暂存解压（理由同 InstallAsync）
        var stagingDir = System.IO.Path.Combine(devBaseDir, $".staging_{Guid.NewGuid():N}");
        try
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var bytes = await http.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(tempZip, bytes);
            }
            SafeExtractZip(tempZip, stagingDir);

            var sourceRoot = FindPluginRoot(stagingDir);
            if (sourceRoot == null)
            {
                Notify(false, Framework.Lang.T("market.sourcePackageMissingManifest"), Framework.Lang.T("market.pluginMarket"));
                return;
            }

            // 与编辑器编译约定一致：目录名 = 插件 ID 的连字符替换为下划线
            var dirName = pluginKey.Replace("-", "_").Replace(" ", "_");
            var devDir = System.IO.Path.Combine(PluginBuildService.Instance.PluginsOutputDir, dirName);
            if (System.IO.Directory.Exists(devDir))
                System.IO.Directory.Delete(devDir, true);
            MoveDirectory(sourceRoot, devDir);

            // 打开插件编辑器，用户在“开发项目列表”中选择该项目即可修改编译
            await DispatcherInvoke(() =>
            {
                var window = new Windows.PluginEditorWindow();
                window.Show();
            });

            Notify(true, Framework.Lang.TF("market.sourceDownloadedOpenInEditor", dirName), Framework.Lang.T("market.pluginMarket"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginMarketBridge] failed to pull source: {ex.Message}");
            Notify(false, Framework.Lang.TF("market.sourceFetchFailed", ex.Message), Framework.Lang.T("market.pluginMarket"));
        }
        finally
        {
            try { if (System.IO.File.Exists(tempZip)) System.IO.File.Delete(tempZip); } catch { }
            try { if (System.IO.Directory.Exists(stagingDir)) System.IO.Directory.Delete(stagingDir, true); } catch { }
        }
    }

    /// <summary>在 UI 线程执行动作</summary>
    private static Task DispatcherInvoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher != null ? dispatcher.InvokeAsync(action).Task : Task.Run(action);
    }

    /// <summary>安装结果宿主侧弹窗通知（fire-and-forget 模式的结果反馈）</summary>
    private static void Notify(bool success, string message, string title)
    {
        // 失败信息带 ❌ 前缀区分，ThemedMessageBox 无图标参数
        var text = success ? message : "❌ " + message;
        _ = DispatcherInvoke(() =>
            ThemedMessageBox.Show(Application.Current?.MainWindow, text, title));
    }

    /// <summary>安全解压 zip：拒绝越界条目（zip slip 防护）</summary>
    private static void SafeExtractZip(string zipPath, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        var destFull = System.IO.Path.GetFullPath(destDir);
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var destPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(destDir, entry.FullName));
            if (!destPath.StartsWith(destFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && destPath != destFull)
                throw new System.IO.IOException($"非法的插件包条目: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                System.IO.Directory.CreateDirectory(destPath);
                continue;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destPath)!);
            using (var entryStream = entry.Open())
            using (var fileStream = System.IO.File.Create(destPath))
                entryStream.CopyTo(fileStream);
        }
    }

    /// <summary>在解压目录中定位插件根（含 plugin.json 的目录），兼容单层根文件夹的 zip</summary>
    private static string? FindPluginRoot(string dir)
    {
        if (System.IO.File.Exists(System.IO.Path.Combine(dir, "plugin.json"))) return dir;
        foreach (var sub in System.IO.Directory.GetDirectories(dir))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(sub, "plugin.json"))) return sub;
        }
        return null;
    }

    /// <summary>移动目录：同卷直接 Move，跨卷（如 C: Temp → F: plugins）回退为复制+删除</summary>
    private static void MoveDirectory(string sourceDir, string destDir)
    {
        try
        {
            System.IO.Directory.Move(sourceDir, destDir);
        }
        catch (System.IO.IOException)
        {
            CopyDirectory(sourceDir, destDir);
            System.IO.Directory.Delete(sourceDir, true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        foreach (var file in System.IO.Directory.GetFiles(sourceDir))
            System.IO.File.Copy(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), true);
        foreach (var sub in System.IO.Directory.GetDirectories(sourceDir))
            CopyDirectory(sub, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(sub)));
    }

    /// <summary>目录名清洗：仅保留字母数字、连字符与下划线</summary>
    private static string SanitizeDirName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var result = new string(chars);
        return string.IsNullOrEmpty(result) ? "plugin" : result;
    }
}
