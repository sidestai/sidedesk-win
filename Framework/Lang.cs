using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace SideDesk.Framework
{
    /// <summary>
    /// 多语言服务：以语义键（如 settings.language.followSystem）查对应语言词典。
    /// - 键约定：小写驼峰、点分层（最多 4 层）{域}.{区域}.{语义}；
    ///   存量代码仍可传中文旧键，入口经 langs/_legacy.json（中文→语义键）归一化，新旧键同轨。
    /// - 语言偏好："auto"（跟随系统）及 13 种语言代码，持久化于 AppSettings.Language
    /// - 词典：程序目录 langs/{语言代码}.json（含 zh-CN/en 共 13 种），改翻译无需重新编译；
    ///   词典仅在 Init/Apply 时加载（避免启动期双重加载）。
    /// - fallback 链：当前语言词典 → en 词典（懒加载缓存）→ 键本身；
    ///   当前语言为 en 时主词典即兜底，不重复加载。
    /// - 缺键告警：最终回落到键本身（且当前语言 ≠ en）时记入缺键集合并 Debug 输出，
    ///   退出时经 DumpMissingKeys() 追加写入 %LocalAppData%\SideDesk\Logs\lang-missing.log。
    /// - XAML 中用 {l:T 键} 标记扩展接入，切换语言时已注册的依赖属性实时刷新；
    ///   代码中用 Lang.T("键") / Lang.TF("misc.containsPlaceholder", args)。
    /// </summary>
    public static class Lang
    {
        public const string Auto = "auto";
        public const string ZhCN = "zh-CN";
        public const string En = "en";

        /// <summary>当前生效语言代码（zh-CN / en / zh-TW / ja / ko / de / es / fr / it / pl / ru / pt-BR / tr）</summary>
        public static string Current { get; private set; } = ResolveSystemLanguage();

        /// <summary>语言切换事件（UI 线程外触发时订阅方自行调度）</summary>
        public static event Action? Changed;

        // 外部 JSON 词典：程序目录 langs/{语言代码}.json（改翻译无需重新编译）。
        // 启动时保持为空，仅在 Init/Apply 时加载（修复旧版静态初始化 + Init 的双重加载）。
        private static Dictionary<string, string> _jsonMap = new();

        // 旧键映射表：langs/_legacy.json（中文原文 → 语义键），启动时加载一次，用于 T() 入口归一化。
        private static readonly Dictionary<string, string> _legacyMap = LoadJsonMap("_legacy");

        // en 兜底词典：非 en 语言首次 miss 时才解析，之后缓存复用；当前语言为 en 时不加载（主词典即 en）。
        private static Dictionary<string, string>? _enMap;
        private static Dictionary<string, string> EnMap => _enMap ??= LoadJsonMap(En);

        // 缺键告警集合（去重）。T() 可能被后台线程调用（如 HttpLog / McpServerService），
        // 故用线程安全的 ConcurrentDictionary（值固定为 0，仅作集合语义），避免并发 Add 损坏。
        private static readonly ConcurrentDictionary<string, byte> _missingKeys = new();

        private static Dictionary<string, string> LoadJsonMap(string lang)
        {
            try
            {
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "langs", lang + ".json");
                if (System.IO.File.Exists(path))
                {
                    var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
                        System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                    if (dict != null) return dict;
                }
            }
            catch (Exception ex)
            {
                // 词典文件缺失或损坏时回落为空词典（最终回落键本身）
                System.Diagnostics.Debug.WriteLine($"[Lang] failed to load dictionary {lang}.json: {ex.Message}");
            }
            return new Dictionary<string, string>();
        }

        // XAML 标记扩展注册表：弱引用目标 + 依赖属性 + key，切换时逐一刷新
        private static readonly List<Registration> _registrations = new();

        private sealed class Registration
        {
            public WeakReference<DependencyObject> Target = null!;
            public DependencyProperty Property = null!;
            public string Key = "";
        }

        /// <summary>启动时按已保存偏好初始化（不触发事件）</summary>
        public static void Init(string preference)
        {
            Current = Resolve(preference);
            _jsonMap = LoadJsonMap(Current);
            _enMap = null; // 重新加载时清空 en 兜底缓存，支持运行中修改翻译
            ApplyCulture();
        }

        /// <summary>切换语言：刷新线程文化、更新全部已注册的 XAML 目标并触发事件</summary>
        public static void Apply(string preference)
        {
            string next = Resolve(preference);
            if (next == Current)
            {
                // 同语言重载路径：仅重载词典并刷新已注册的 XAML 目标，
                // 不更新线程文化、不触发 Changed 事件（订阅方不会收到通知）。
                _jsonMap = LoadJsonMap(next); // 同语言下重新加载词典，支持运行中修改翻译
                _enMap = null;
                RefreshRegistrations();
                return;
            }
            Current = next;
            _jsonMap = LoadJsonMap(next);
            _enMap = null;
            ApplyCulture();
            RefreshRegistrations();
            Changed?.Invoke();
        }

        /// <summary>
        /// 翻译：入口将中文旧键经 _legacy.json 归一化为语义键；
        /// fallback 链：当前语言词典 → en 词典（懒加载）→ 键本身。
        /// 中文不再短路，同样查 zh-CN.json；最终回落键本身且非 en 时记录缺键。
        /// </summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            // 入口归一化：中文旧键 → 语义新键（语义键不命中则原样使用，新旧键同轨）
            if (_legacyMap.TryGetValue(key, out var normalized)) key = normalized;
            if (_jsonMap.TryGetValue(key, out var jsonValue)) return jsonValue;
            // en 兜底：当前语言为 en 时主词典即兜底，不重复加载
            if (Current != En && EnMap.TryGetValue(key, out var enValue)) return enValue;
            if (Current != En && _missingKeys.TryAdd(key, 0))
            {
                System.Diagnostics.Debug.WriteLine("[Lang] missing: " + key);
            }
            return key;
        }

        /// <summary>将本次运行中缺失的键追加写入 %LocalAppData%\SideDesk\Logs\lang-missing.log（退出路径调用）</summary>
        public static void DumpMissingKeys()
        {
            try
            {
                if (_missingKeys.Count == 0) return;
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SideDesk", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "lang-missing.log");
                // 防止日志无限增长：超过 1MB 时先清空再写（简单轮转）
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    System.IO.File.WriteAllText(path, string.Empty, System.Text.Encoding.UTF8);
                }
                var lines = new List<string>
                {
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] lang={Current} missing={_missingKeys.Count}"
                };
                // Keys 快照遍历（ConcurrentDictionary 枚举是线程安全的）
                lines.AddRange(_missingKeys.Keys.OrderBy(k => k, StringComparer.Ordinal));
                System.IO.File.AppendAllLines(path, lines, System.Text.Encoding.UTF8);
            }
            catch { /* 缺键日志写入失败不阻断退出 */ }
        }

        /// <summary>翻译并格式化（占位符 {0}{1}...）</summary>
        public static string TF(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        /// <summary>XAML 标记扩展注册（弱引用，目标释放后自动失效）</summary>
        public static void Register(DependencyObject target, DependencyProperty property, string key)
        {
            _registrations.Add(new Registration
            {
                Target = new WeakReference<DependencyObject>(target),
                Property = property,
                Key = key
            });
        }

        private static void RefreshRegistrations()
        {
            var app = Application.Current;
            void DoRefresh()
            {
                for (int i = _registrations.Count - 1; i >= 0; i--)
                {
                    var reg = _registrations[i];
                    if (reg.Target.TryGetTarget(out var dep))
                    {
                        try { dep.SetValue(reg.Property, T(reg.Key)); }
                        catch { /* 单个目标刷新失败不影响整体 */ }
                    }
                    else
                    {
                        _registrations.RemoveAt(i); // 目标已释放，清理
                    }
                }
            }

            if (app?.Dispatcher?.CheckAccess() == true) DoRefresh();
            else app?.Dispatcher?.BeginInvoke((Action)DoRefresh);
        }

        // 已提供词典的语言代码（对应 langs/{代码}.json）
        private static readonly string[] Supported = { ZhCN, En, "zh-TW", "ja", "ko", "de", "es", "fr", "it", "pl", "ru", "pt-BR", "tr" };

        private static string Resolve(string preference)
        {
            if (string.IsNullOrEmpty(preference) || preference == Auto) return ResolveSystemLanguage();
            return Array.IndexOf(Supported, preference) >= 0 ? preference : ResolveSystemLanguage();
        }

        /// <summary>系统语言判定：映射到已提供词典的语言，无词典的回落英文</summary>
        private static string ResolveSystemLanguage()
        {
            try
            {
                string culture = CultureInfo.CurrentUICulture.Name;
                if (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    // 繁体系：zh-TW / zh-HK / zh-MO / zh-Hant
                    return (culture.Contains('-') && (culture.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                            || culture.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                            || culture.EndsWith("MO", StringComparison.OrdinalIgnoreCase)))
                           || culture.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "zh-TW" : ZhCN;
                }
                string primary = culture.Split('-')[0].ToLowerInvariant();
                return primary switch
                {
                    "de" => "de",
                    "es" => "es",
                    "fr" => "fr",
                    "it" => "it",
                    "ja" => "ja",
                    "ko" => "ko",
                    "pl" => "pl",
                    "ru" => "ru",
                    "pt" => "pt-BR",
                    "tr" => "tr",
                    _ => En
                };
            }
            catch { return ZhCN; }
        }

        private static void ApplyCulture()
        {
            try
            {
                string cultureName = Current switch
                {
                    ZhCN => "zh-CN",
                    "zh-TW" => "zh-TW",
                    "pt-BR" => "pt-BR",
                    En => "en-US",
                    _ => Current
                };
                var culture = new CultureInfo(cultureName);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                // 已打开窗口的语言标记（影响日期等本地化呈现）
                foreach (Window w in Application.Current?.Windows ?? new WindowCollection())
                {
                    try { w.Language = XmlLanguage.GetLanguage(culture.Name); } catch { }
                }
            }
            catch { /* 文化设置失败不阻断 */ }
        }
    }

    /// <summary>
    /// XAML 翻译标记扩展：Text="{l:T 中文原文}"。
    /// 切换语言时经 Lang.Register 注册的目标会被实时刷新；
    /// Style/Template 内的 Setter 无法注册，将在窗口下次创建时生效。
    /// </summary>
    public class TExtension : MarkupExtension
    {
        public string Key { get; set; } = "";

        public TExtension() { }

        public TExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target
                && target.TargetObject is DependencyObject dep
                && target.TargetProperty is DependencyProperty prop)
            {
                Lang.Register(dep, prop, Key);
            }
            return Lang.T(Key);
        }
    }
}
