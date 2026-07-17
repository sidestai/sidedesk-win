using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SideDesk.Services
{
    /// <summary>云同步状态</summary>
    public enum CloudSyncState
    {
        Idle,
        Syncing,
        Success,
        Failed,
        NotLoggedIn
    }

    /// <summary>
    /// 云同步服务：开启后每 30 分钟把本地全部配置打包上传到服务器进行备份，退出时再同步一次。
    /// 说明：后端备份接口尚未确定，<see cref="SyncEndpoint"/> 为占位地址，接入真实接口时替换即可。
    /// </summary>
    public sealed class CloudSyncService
    {
        private static readonly Lazy<CloudSyncService> _lazy = new(() => new CloudSyncService());
        public static CloudSyncService Instance => _lazy.Value;

        // TODO: 替换为真实的备份上传接口地址（当前为占位）
        private const string SyncEndpoint = "https://v3.api.yzllq.com/api/client/backup/upload";

        // 自动同步间隔（分钟）
        private const int IntervalMinutes = 30;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly DispatcherTimer _timer;
        private bool _syncing;

        /// <summary>同步状态变化通知（状态, 说明文本），供 UI 显示</summary>
        public event Action<CloudSyncState, string>? StatusChanged;

        private CloudSyncService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(IntervalMinutes) };
            _timer.Tick += async (_, _) => await SyncNowAsync(silent: true);
        }

        public bool IsRunning => _timer.IsEnabled;

        /// <summary>开启定时同步（若已开启则忽略），并立即触发一次同步。</summary>
        public void Start()
        {
            if (_timer.IsEnabled) return;
            _timer.Start();
            _ = SyncNowAsync(silent: true);
        }

        /// <summary>停止定时同步。</summary>
        public void Stop() => _timer.Stop();

        /// <summary>立即执行一次同步。返回是否成功。</summary>
        public async Task<bool> SyncNowAsync(bool silent = false)
        {
            if (_syncing) return false;

            var app = (SideDesk.App)System.Windows.Application.Current;
            var settings = app.AppSettings;

            if (string.IsNullOrEmpty(settings.AccountToken))
            {
                StatusChanged?.Invoke(CloudSyncState.NotLoggedIn, "未登录，无法同步");
                return false;
            }

            _syncing = true;
            StatusChanged?.Invoke(CloudSyncState.Syncing, "正在同步...");
            try
            {
                string bundle = BuildConfigBundleJson();
                using var content = new StringContent(bundle, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, SyncEndpoint) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.AccountToken);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    settings.LastCloudSyncTime = DateTime.Now;
                    settings.Save();
                    StatusChanged?.Invoke(CloudSyncState.Success, $"已同步 · {settings.LastCloudSyncTime:yyyy-MM-dd HH:mm}");
                    return true;
                }

                StatusChanged?.Invoke(CloudSyncState.Failed, $"同步失败（HTTP {(int)resp.StatusCode}）");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudSync] 同步失败: {ex.Message}");
                StatusChanged?.Invoke(CloudSyncState.Failed, "同步失败：" + ex.Message);
                return false;
            }
            finally
            {
                _syncing = false;
            }
        }

        #region 本地备份 / 恢复 / 导出

        // 本地备份目录与文件（覆盖式，仅保留最后一个）
        private static string BackupDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk", "backup");
        private static string LastBackupFile => Path.Combine(BackupDir, "last-backup.json");

        /// <summary>是否存在本地备份。</summary>
        public static bool HasLocalBackup() => File.Exists(LastBackupFile);

        /// <summary>获取最后一次本地备份的时间（无则返回 null）。</summary>
        public static DateTime? GetLocalBackupTime()
            => File.Exists(LastBackupFile) ? File.GetLastWriteTime(LastBackupFile) : null;

        /// <summary>创建本地备份（覆盖式，只保留最后一个）。返回是否成功。</summary>
        public static bool CreateLocalBackup()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                File.WriteAllText(LastBackupFile, BuildConfigBundleJson(indented: true));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>从最后一次本地备份恢复所有配置。返回恢复的文件数（-1 表示失败/无备份）。</summary>
        public static int RestoreLocalBackup()
        {
            try
            {
                if (!File.Exists(LastBackupFile)) return -1;
                return RestoreFromBundleJson(File.ReadAllText(LastBackupFile));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 恢复失败: {ex.Message}");
                return -1;
            }
        }

        /// <summary>把打包 JSON 中的文件写回各自的原始位置。返回写回的文件数。</summary>
        public static int RestoreFromBundleJson(string json)
        {
            int count = 0;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
                return 0;

            foreach (var prop in filesEl.EnumerateObject())
            {
                string? fileContent = prop.Value.GetString();
                if (fileContent == null) continue;
                string target = MapBackupKeyToPath(prop.Name);
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(target, fileContent);
                    count++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Backup] 写回 {prop.Name} 失败: {ex.Message}");
                }
            }
            return count;
        }

        /// <summary>将备份键映射回原始文件路径（与 BuildConfigBundleJson 的键命名一致）。</summary>
        private static string MapBackupKeyToPath(string key)
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
            return key switch
            {
                "app-settings.json" => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"),
                "user-templates.json" => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json"),
                _ => Path.Combine(local, key)
            };
        }

        #endregion

        /// <summary>收集本地全部配置文件，打包成一个 JSON 负载。</summary>
        public static string BuildConfigBundleJson(bool indented = false)
        {
            var files = new Dictionary<string, string>();

            // 1) %LocalAppData%\SideDesk\ 下的全部 *.json（windows.json、quickgroups.json、
            //    bookmarks.json、aitools.json、programs.json、browser_profiles.json、
            //    title-lock-rules.json、digital-employees.json、settings.json 等）
            string localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");
            AddJsonFilesFromFolder(files, localFolder);

            // 2) 程序目录下的 settings.json（应用停靠/主题/账户等设置）
            TryAddFile(files,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"),
                "app-settings.json");

            // 3) 自定义数据模版
            TryAddFile(files,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-templates", "user-templates.json"),
                "user-templates.json");

            var payload = new
            {
                app = "SideDesk",
                machine = Environment.MachineName,
                syncedAt = DateTime.Now.ToString("o"),
                files
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = indented });
        }

        private static void AddJsonFilesFromFolder(Dictionary<string, string> files, string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var path in Directory.GetFiles(folder, "*.json"))
                {
                    string key = Path.GetFileName(path);
                    TryAddFile(files, path, key);
                }
            }
            catch { }
        }

        private static void TryAddFile(Dictionary<string, string> files, string path, string key)
        {
            try
            {
                if (File.Exists(path))
                    files[key] = File.ReadAllText(path);
            }
            catch { }
        }
    }
}
