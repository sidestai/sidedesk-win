using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace SideDesk.Services;

public sealed class VelopackUpdateService
{
    private static readonly Lazy<VelopackUpdateService> _instance = new(() => new VelopackUpdateService());
    public static VelopackUpdateService Instance => _instance.Value;

    private readonly string _repoUrl = "https://github.com/nicenightcc/SideDesk";
    private readonly string _proxyUrlPrefix = "https://ghfast.top/";
    private UpdateManager? _updateManager;
    private bool _isDownloading;
    private string _resolvedUpdateUrl = "";

    public event Action<int>? DownloadProgressChanged;
    public event Action<string>? UpdateStatusChanged;
    public UpdateInfo? ReadyUpdateInfo { get; private set; }
    public bool IsUpdateReady { get; private set; }
    public event Action? UpdateReadyChanged;

    private int _silentCheckFailureCount = 0;
    private const int MaxSilentCheckFailures = 3;

    private VelopackUpdateService()
    {
        InitializeManager();
    }

    /// <summary>
    /// 初始化更新管理器，配置代理加速的 GitHub Release 源
    /// </summary>
    public void InitializeManager()
    {
        try
        {
            _resolvedUpdateUrl = _proxyUrlPrefix + _repoUrl;
            var source = new GithubSource(_resolvedUpdateUrl, null, false);
            _updateManager = new UpdateManager(source);
            Debug.WriteLine($"[VelopackUpdate] 更新管理器已初始化，源地址: {_resolvedUpdateUrl}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VelopackUpdate] 初始化更新管理器失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检测是否有新版本可用
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (_updateManager == null)
        {
            Debug.WriteLine("[VelopackUpdate] 更新管理器未初始化");
            UpdateStatusChanged?.Invoke(Framework.Lang.T("更新管理器未初始化"));
            return null;
        }

        try
        {
            UpdateStatusChanged?.Invoke(Framework.Lang.T("正在检查更新..."));
            Debug.WriteLine("[VelopackUpdate] 开始检查更新...");

            // 预诊断网络连通性
            var networkOk = await DiagnoseNetworkAsync();
            if (!networkOk)
            {
                UpdateStatusChanged?.Invoke(Framework.Lang.T("网络连接失败，无法检查更新"));
                Debug.WriteLine("[VelopackUpdate] 网络诊断失败，跳过更新检查");
                return null;
            }

            var updateInfo = await _updateManager.CheckForUpdatesAsync();

            if (updateInfo != null)
            {
                UpdateStatusChanged?.Invoke(Framework.Lang.TF("发现新版本: {0}", updateInfo.TargetFullRelease.Version));
                Debug.WriteLine($"[VelopackUpdate] 发现新版本: {updateInfo.TargetFullRelease.Version}");
            }
            else
            {
                UpdateStatusChanged?.Invoke(Framework.Lang.T("当前已是最新版本"));
                Debug.WriteLine("[VelopackUpdate] 当前已是最新版本");
            }

            return updateInfo;
        }
        catch (Exception ex)
        {
            var friendlyMsg = BuildFriendlyErrorMessage(ex);
            UpdateStatusChanged?.Invoke(friendlyMsg);
            Debug.WriteLine($"[VelopackUpdate] 检查更新失败: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 后台下载增量更新包，带进度回调
    /// </summary>
    public async Task<bool> DownloadUpdatesAsync(UpdateInfo updateInfo)
    {
        if (_updateManager == null || _isDownloading)
        {
            Debug.WriteLine("[VelopackUpdate] 更新管理器未初始化或正在下载中");
            return false;
        }

        try
        {
            _isDownloading = true;
            UpdateStatusChanged?.Invoke(Framework.Lang.T("正在下载更新..."));
            Debug.WriteLine($"[VelopackUpdate] 开始下载更新: {updateInfo.TargetFullRelease.Version}");

            await _updateManager.DownloadUpdatesAsync(updateInfo, progress =>
            {
                DownloadProgressChanged?.Invoke(progress);
                if (progress % 20 == 0)
                {
                    Debug.WriteLine($"[VelopackUpdate] 下载进度: {progress}%");
                }
            });

            ReadyUpdateInfo = updateInfo;
            IsUpdateReady = true;
            UpdateReadyChanged?.Invoke();
            UpdateStatusChanged?.Invoke(Framework.Lang.TF("更新已就绪: {0}，重启后生效", updateInfo.TargetFullRelease.Version));
            Debug.WriteLine($"[VelopackUpdate] 下载完成，更新已就绪");
            return true;
        }
        catch (Exception ex)
        {
            var friendlyMsg = BuildFriendlyErrorMessage(ex);
            UpdateStatusChanged?.Invoke(Framework.Lang.TF("下载更新失败: {0}", friendlyMsg));
            Debug.WriteLine($"[VelopackUpdate] 下载更新失败: {ex}");
            return false;
        }
        finally
        {
            _isDownloading = false;
        }
    }

    /// <summary>
    /// 应用更新并重启应用程序
    /// </summary>
    public void ApplyAndRestart(UpdateInfo updateInfo)
    {
        if (_updateManager == null)
        {
            Debug.WriteLine("[VelopackUpdate] 更新管理器未初始化，无法应用更新");
            return;
        }

        try
        {
            UpdateStatusChanged?.Invoke(Framework.Lang.T("正在应用更新，即将重启..."));
            Debug.WriteLine("[VelopackUpdate] 正在应用更新并重启...");
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            UpdateStatusChanged?.Invoke(Framework.Lang.TF("应用更新失败: {0}", ex.Message));
            Debug.WriteLine($"[VelopackUpdate] 应用更新失败: {ex}");
        }
    }

    /// <summary>
    /// 启动后静默检测+下载（带失败重试3次，最大失败3次保护）
    /// </summary>
    public async Task StartSilentUpdateCheckAndDownloadAsync()
    {
        // 检查是否启用自动更新
        bool enableAutoUpdate = false;
        try
        {
            enableAutoUpdate = App.Current.Dispatcher.Invoke(() => ((App)App.Current).AppSettings.EnableAutoUpdate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VelopackUpdate] 获取自动更新设置失败: {ex.Message}");
            return;
        }

        if (!enableAutoUpdate)
        {
            Debug.WriteLine("[VelopackUpdate] 自动更新已禁用，跳过静默检查");
            return;
        }

        // 超过最大静默检查失败次数，不再尝试
        if (_silentCheckFailureCount >= MaxSilentCheckFailures)
        {
            Debug.WriteLine($"[VelopackUpdate] 静默检查失败次数已达上限({MaxSilentCheckFailures})，停止自动检查");
            return;
        }

        Debug.WriteLine("[VelopackUpdate] 开始静默更新检查...");

        try
        {
            var updateInfo = await CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                return;
            }

            // 带重试的下载（最多重试3次，间隔5秒）
            const int maxRetries = 3;
            const int retryDelayMs = 5000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Debug.WriteLine($"[VelopackUpdate] 静默下载尝试 {attempt}/{maxRetries}");
                    await DownloadUpdatesAsync(updateInfo);

                    if (IsUpdateReady)
                    {
                        Debug.WriteLine("[VelopackUpdate] 静默更新下载成功");
                        _silentCheckFailureCount = 0; // 成功后重置失败计数
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VelopackUpdate] 静默下载第 {attempt} 次尝试失败: {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    Debug.WriteLine($"[VelopackUpdate] 等待 {retryDelayMs}ms 后重试...");
                    await Task.Delay(retryDelayMs);
                }
            }

            // 所有重试都失败
            _silentCheckFailureCount++;
            Debug.WriteLine($"[VelopackUpdate] 静默更新失败，累计失败次数: {_silentCheckFailureCount}/{MaxSilentCheckFailures}");
        }
        catch (Exception ex)
        {
            _silentCheckFailureCount++;
            Debug.WriteLine($"[VelopackUpdate] 静默更新检查异常: {ex.Message}，累计失败次数: {_silentCheckFailureCount}/{MaxSilentCheckFailures}");
        }
    }

    /// <summary>
    /// 网络连通性诊断：尝试 HTTP GET 访问 GitHub Release 文件
    /// </summary>
    public async Task<bool> DiagnoseNetworkAsync()
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var testUrl = $"{_resolvedUpdateUrl}/releases/latest";
            Debug.WriteLine($"[VelopackUpdate] 网络诊断: GET {testUrl}");

            var response = await httpClient.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead);
            var isSuccess = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Found;

            Debug.WriteLine($"[VelopackUpdate] 网络诊断结果: {response.StatusCode} (连通={isSuccess})");
            return isSuccess;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine("[VelopackUpdate] 网络诊断超时");
            return false;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[VelopackUpdate] 网络诊断请求失败: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VelopackUpdate] 网络诊断异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 将异常翻译为用户友好的中文提示
    /// </summary>
    public static string BuildFriendlyErrorMessage(Exception ex)
    {
        if (ex == null)
            return Framework.Lang.T("未知错误");

        var message = ex.Message ?? "";
        var innerMessage = ex.InnerException?.Message ?? "";

        // 网络相关错误
        if (ex is HttpRequestException || message.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Contains("SSL") || message.Contains("TLS") || innerMessage.Contains("SSL"))
                return Framework.Lang.T("网络安全连接失败（SSL/TLS 错误），请检查系统时间和网络代理设置");

            if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return Framework.Lang.T("网络连接超时，请检查网络连接后重试");

            if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase))
                return Framework.Lang.T("无法解析服务器地址（DNS 解析失败），请检查网络连接");

            return Framework.Lang.TF("网络连接错误，请检查网络设置。详情: {0}", message);
        }

        // 超时
        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return Framework.Lang.T("操作超时，请检查网络连接后重试");

        // IO 错误
        if (ex is System.IO.IOException)
            return Framework.Lang.TF("文件读写错误: {0}", message);

        // 权限错误
        if (ex is UnauthorizedAccessException)
            return Framework.Lang.T("权限不足，请尝试以管理员身份运行");

        // 通用错误
        return Framework.Lang.TF("检查更新时发生错误: {0}", message);
    }
}
