using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// Windows 已安装应用枚举服务：读取注册表 Uninstall 键的 DisplayName/DisplayIcon 等，
    /// 过滤系统组件与补丁，并为每个应用解析一个可执行/图标路径。
    /// 结果缓存到 %LocalAppData%\SideDesk\installed-apps-cache.json，打开设置不重复拉取；缓存超过 1 天后台静默刷新。
    /// </summary>
    public static class InstalledAppsService
    {
        public class InstalledApp
        {
            public string Name { get; set; } = "";
            /// <summary>用于提取图标的可执行文件或图标路径</summary>
            public string IconPath { get; set; } = "";
            /// <summary>可作为启动目标的主程序 exe 路径（可能为 null：未解析到有效启动项）</summary>
            public string? LaunchPath { get; set; }
            public string? Publisher { get; set; }
            public string? Version { get; set; }

            /// <summary>可用作快捷方式启动目标的 exe：优先 LaunchPath，否则 IconPath 为非卸载类 .exe 时兜底；无则 null。</summary>
            [JsonIgnore]
            public string? LaunchExe => !string.IsNullOrEmpty(LaunchPath) ? LaunchPath :
                (!string.IsNullOrEmpty(IconPath)
                 && IconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                 && !IsInstallerLike(Path.GetFileName(IconPath))) ? IconPath : null;
        }

        private class CacheFile
        {
            public int Version { get; set; }
            public DateTime SavedAt { get; set; }
            public List<InstalledApp> Apps { get; set; } = new();
        }

        /// <summary>缓存结构版本号：字段变化时递增以废弃旧缓存强制重扫</summary>
        private const int CacheVersion = 2;

        private static readonly object _lock = new();
        private static List<InstalledApp>? _apps;
        private static bool _refreshing;

        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDesk", "installed-apps-cache.json");

        /// <summary>取缓存的已安装应用列表（内存 → 磁盘）；从未扫描返回空列表；缓存超 1 天后台静默刷新。</summary>
        public static List<InstalledApp> GetCached()
        {
            lock (_lock) { if (_apps != null) return _apps; }
            try
            {
                if (File.Exists(CachePath))
                {
                    var cache = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(CachePath));
                    if (cache?.Apps != null && cache.Version >= CacheVersion)
                    {
                        lock (_lock) { _apps = cache.Apps; }
                        if (DateTime.Now - cache.SavedAt > TimeSpan.FromDays(1))
                            RefreshAsync();
                        return cache.Apps;
                    }
                }
            }
            catch { /* 缓存损坏视为无缓存，重新扫描 */ }
            return new List<InstalledApp>();
        }

        /// <summary>确保有数据：缓存为空时后台扫描并在完成后回调结果；已有缓存不做任何事。</summary>
        public static void EnsureLoaded(Action<List<InstalledApp>>? onScanned = null)
        {
            if (GetCached().Count > 0) return;
            Task.Run(() =>
            {
                var apps = Scan();
                SaveCache(apps);
                lock (_lock) { _apps = apps; }
                onScanned?.Invoke(apps);
            });
        }

        private static void RefreshAsync()
        {
            lock (_lock) { if (_refreshing) return; _refreshing = true; }
            Task.Run(() =>
            {
                try
                {
                    var apps = Scan();
                    SaveCache(apps);
                    lock (_lock) { _apps = apps; }
                }
                finally { lock (_lock) { _refreshing = false; } }
            });
        }

        private static void SaveCache(List<InstalledApp> apps)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(new CacheFile { Version = CacheVersion, SavedAt = DateTime.Now, Apps = apps }));
            }
            catch { }
        }

        /// <summary>扫描注册表 Uninstall 键（LM 64/32 + CU），过滤系统组件/补丁，按名称去重并排序。</summary>
        private static List<InstalledApp> Scan()
        {
            var result = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var views = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
            };
            foreach (var (hive, view) in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = uninstall.OpenSubKey(subName);
                            if (key == null) continue;
                            var name = (key.GetValue("DisplayName") as string)?.Trim();
                            if (string.IsNullOrEmpty(name)) continue;
                            if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                            if (!string.IsNullOrEmpty(key.GetValue("ParentKeyName") as string)) continue;
                            var rel = key.GetValue("ReleaseType") as string;
                            if (!string.IsNullOrEmpty(rel) && rel.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (Regex.IsMatch(name, @"^KB\d+")) continue;
                            var launch = ResolveLaunchPath(key, name);
                            var icon = launch ?? ResolveIconPath(key);
                            if (icon == null || !seen.Add(name)) continue;
                            result.Add(new InstalledApp
                            {
                                Name = name,
                                IconPath = icon,
                                LaunchPath = launch,
                                Publisher = key.GetValue("Publisher") as string,
                                Version = key.GetValue("DisplayVersion") as string,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>解析可作为启动目标的主程序 exe：DisplayIcon 的 .exe → InstallLocation 顶层 .exe（排除卸载类、优先文件名与显示名匹配）。</summary>
        private static string? ResolveLaunchPath(RegistryKey key, string displayName)
        {
            var di = StripIconIndex(key.GetValue("DisplayIcon") as string);
            if (di != null && di.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !IsInstallerLike(Path.GetFileName(di)) && File.Exists(di))
                return di;

            var loc = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
            {
                try
                {
                    var exes = Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !IsInstallerLike(Path.GetFileName(f)))
                        .ToList();
                    var hit = exes.FirstOrDefault(f => NameMatches(Path.GetFileNameWithoutExtension(f), displayName));
                    return hit ?? exes.FirstOrDefault();
                }
                catch { }
            }
            return null;
        }

        /// <summary>判断是否为卸载器/安装器类可执行文件（不宜作为启动目标）。</summary>
        internal static bool IsInstallerLike(string fileName)
        {
            var n = fileName.ToLowerInvariant();
            return n.Contains("unins") || n.Contains("uninstall") || n.Contains("setup")
                || n.Contains("redist") || n.Contains("installer");
        }

        /// <summary>exe 文件名与显示名简单匹配（归一化后互相包含，过短不匹配）。</summary>
        private static bool NameMatches(string exeName, string displayName)
        {
            string Norm(string s)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in s.ToLowerInvariant())
                    if (char.IsLetterOrDigit(c)) sb.Append(c);
                return sb.ToString();
            }
            var a = Norm(exeName);
            var b = Norm(displayName);
            return a.Length >= 3 && b.Length >= 3 && (b.Contains(a) || a.Contains(b));
        }

        /// <summary>解析图标来源文件：DisplayIcon（剥离 ,index）→ UninstallString 中的 .exe → InstallLocation 顶层 .exe。</summary>
        private static string? ResolveIconPath(RegistryKey key)
        {
            var displayIcon = StripIconIndex(key.GetValue("DisplayIcon") as string);
            if (displayIcon != null && File.Exists(displayIcon)) return displayIcon;

            var uninstall = key.GetValue("QuietUninstallString") as string ?? key.GetValue("UninstallString") as string;
            var exe = ExtractExePath(uninstall);
            if (exe != null && File.Exists(exe)) return exe;

            var loc = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
            {
                try
                {
                    var top = Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (top != null) return top;
                }
                catch { }
            }
            return null;
        }

        /// <summary>剥离 DisplayIcon 类值的引号与尾部 ",index"；结果非 exe/ico 文件返回 null。</summary>
        private static string? StripIconIndex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var file = value.Trim().Trim('"').Trim();
            var comma = file.LastIndexOf(',');
            if (comma > 0 && int.TryParse(file.Substring(comma + 1).Trim(), out _))
                file = file.Substring(0, comma).Trim();
            var ext = Path.GetExtension(file).ToLowerInvariant();
            return ext is ".exe" or ".ico" ? file : null;
        }

        /// <summary>从卸载命令中提取第一个 .exe 路径（优先引号内内容）。</summary>
        private static string? ExtractExePath(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var m = Regex.Match(command, "\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(command, @"(\S+\.exe)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
