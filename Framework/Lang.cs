using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace SideDesk.Framework
{
    /// <summary>
    /// 多语言服务：以中文原文为 key，查对应语言词典，缺失回落中文原文。
    /// - 语言偏好："auto"（跟随系统）/ "zh-CN" / "en"，持久化于 AppSettings.Language
    /// - 词典：程序目录 langs/{语言代码}.json（en/zh-TW/ja/ko/de/es/fr/it/pl/ru/pt-BR/tr），改翻译无需重新编译
    /// - XAML 中用 {l:T 中文原文} 标记扩展接入，切换语言时已注册的依赖属性实时刷新
    /// - 代码中用 Lang.T("中文原文") / Lang.TF("含{0}占位", args)
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

        // 外部 JSON 词典：程序目录 langs/{语言代码}.json（改翻译无需重新编译，启动/切语言时重新加载）
        private static Dictionary<string, string> _jsonMap = LoadJsonMap(Current);

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
            catch { /* 词典文件缺失或损坏时回落 */ }
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
            ApplyCulture();
        }

        /// <summary>切换语言：刷新线程文化、更新全部已注册的 XAML 目标并触发事件</summary>
        public static void Apply(string preference)
        {
            string next = Resolve(preference);
            if (next == Current)
            {
                _jsonMap = LoadJsonMap(next); // 同语言下重新加载词典，支持运行中修改翻译
                RefreshRegistrations();
                return;
            }
            Current = next;
            _jsonMap = LoadJsonMap(next);
            ApplyCulture();
            RefreshRegistrations();
            Changed?.Invoke();
        }

        /// <summary>翻译：优先查 JSON 词典，英文再查内置词典兜底，缺失/中文环境直接返回原文</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (Current == ZhCN) return key;
            if (_jsonMap.TryGetValue(key, out var jsonValue)) return jsonValue;
            if (Current == En && LangEn.Map.TryGetValue(key, out var value)) return value;
            return key;
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
