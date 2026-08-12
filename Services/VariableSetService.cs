using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// 变量集服务：把多个运行中的变量（显式勾选 + 通配符匹配）按 求和/求平均/最大/最小/计数
/// 聚合成一个新变量（如 qoder.count_all）。
/// 每个变量集对应一个虚拟 DataDashboardItem（VarKey=变量集名），经 DataPollingService.FindItemByVarKey
/// 对外暴露，从而像普通变量一样被信号灯条件、显示引用（RefVarKey）、格式 {变量名} 使用。
/// 任意数据项更新时重算；另有兜底定时器覆盖"新变量随后上线/通配匹配集变化"的场景。
/// </summary>
public sealed class VariableSetService
{
    private static readonly Lazy<VariableSetService> _lazy = new(() => new VariableSetService());
    public static VariableSetService Instance => _lazy.Value;

    private WindowConfigManager? _configs;
    // 变量集名 -> 虚拟数据项（保持实例稳定，引用方持有的对象不失效）
    private readonly Dictionary<string, DataDashboardItem> _items = new(StringComparer.OrdinalIgnoreCase);
    // 云端登记的变量集（其他设备/团队成员创建；同名时本地定义优先）
    private readonly List<VariableSet> _remoteSets = new();
    private readonly object _lock = new();
    private DispatcherTimer? _timer;
    private bool _recomputePending;

    private VariableSetService() { }

    /// <summary>应用启动时初始化（在 DataPollingService.SyncFromConfigs 之后调用）</summary>
    public void Init(WindowConfigManager configs)
    {
        _configs = configs;
        DataPollingService.Instance.DataUpdated += OnDataUpdated;
        Rebuild();

        // 兜底定时重算：源变量陆续上线、通配符匹配集变化时也能及时更新
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (s, e) => RecomputeAll();
        _timer.Start();
    }

    /// <summary>变量集定义变化后重建虚拟项并重算（增删变量集时由 UI 调用）</summary>
    public void Rebuild()
    {
        lock (_lock)
        {
            var sets = EffectiveSetsLocked();
            var live = new HashSet<string>(sets.Select(s => s.VarKey), StringComparer.OrdinalIgnoreCase);

            // 移除已删除的变量集
            foreach (var key in _items.Keys.Where(k => !live.Contains(k)).ToList())
                _items.Remove(key);

            // 新增/保留变量集（保留原实例，保证引用方对象稳定）
            foreach (var set in sets)
            {
                if (string.IsNullOrEmpty(set.VarKey)) continue;
                if (!_items.TryGetValue(set.VarKey, out var item))
                {
                    item = new DataDashboardItem { VarKey = set.VarKey, Label = set.VarKey };
                    _items[set.VarKey] = item;
                }
            }
        }
        RecomputeAll();
    }

    /// <summary>有效变量集定义：本地 + 云端（同名时本地优先）。须在 _lock 内调用</summary>
    private List<VariableSet> EffectiveSetsLocked()
    {
        var sets = (_configs?.VariableSets ?? new List<VariableSet>())
            .Where(s => !string.IsNullOrEmpty(s.VarKey)).ToList();
        var localKeys = new HashSet<string>(sets.Select(s => s.VarKey), StringComparer.OrdinalIgnoreCase);
        foreach (var r in _remoteSets)
            if (!string.IsNullOrEmpty(r.VarKey) && !localKeys.Contains(r.VarKey))
                sets.Add(r);
        return sets;
    }

    /// <summary>按变量名查找变量集虚拟项（供 DataPollingService.FindItemByVarKey 兜底）</summary>
    public DataDashboardItem? FindItemByVarKey(string? varKey)
    {
        if (string.IsNullOrEmpty(varKey)) return null;
        lock (_lock)
            return _items.TryGetValue(varKey, out var item) ? item : null;
    }

    /// <summary>获取全部变量集虚拟项（供变量选择器列出，如信号灯条件选择）</summary>
    public List<DataDashboardItem> GetItems()
    {
        lock (_lock) return _items.Values.ToList();
    }

    /// <summary>获取全部有效变量集定义（本地 + 云端，同名时本地优先；供 UI 展示来源配置）</summary>
    public List<VariableSet> GetSets()
    {
        lock (_lock) return EffectiveSetsLocked();
    }

    /// <summary>获取云端登记的变量集快照（含作用域，供详情窗展示/删除）</summary>
    public List<VariableSet> GetRemoteSets()
    {
        lock (_lock) return _remoteSets.ToList();
    }

    /// <summary>从云端登记列表移除指定变量集（服务端删除成功后由 UI 调用）并重算</summary>
    public void RemoveRemote(string varKey, string? teamId)
    {
        lock (_lock)
        {
            _remoteSets.RemoveAll(r => string.Equals(r.VarKey, varKey, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(r.TeamId) == string.IsNullOrEmpty(teamId)
                && (string.IsNullOrEmpty(teamId) || r.TeamId == teamId));
        }
        Rebuild();
    }

    /// <summary>
    /// 拉取云端登记的变量集（个人 + 已加入的每个团队），并入聚合计算（同名本地优先）。
    /// 应用启动后与打开变量详情窗时调用；后端不可达时静默跳过。
    /// </summary>
    public async Task PullRemoteAsync()
    {
        try
        {
            var fetched = new List<VariableSet>();
            // 个人作用域
            var mine = await DataVariableService.Instance.ListSetsAsync(null).ConfigureAwait(false);
            if (mine != null)
                foreach (var s in mine)
                    fetched.Add(ToSet(s, null, null));
            // 每个已加入团队的作用域
            var teams = await TeamService.Instance.ListTeamsAsync().ConfigureAwait(false);
            if (teams != null)
            {
                foreach (var t in teams)
                {
                    var list = await DataVariableService.Instance.ListSetsAsync(t.Id).ConfigureAwait(false);
                    if (list == null) continue;
                    foreach (var s in list)
                        fetched.Add(ToSet(s, t.Id, t.Name));
                }
            }
            lock (_lock)
            {
                _remoteSets.Clear();
                _remoteSets.AddRange(fetched);
            }
            Rebuild();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VariableSet] 拉取云端变量集失败: {ex.Message}");
        }
    }

    /// <summary>后端变量集记录转本地模型（teamName 仅本地展示用）</summary>
    private static VariableSet ToSet(VariableSetInfo s, string? teamId, string? teamName) => new()
    {
        VarKey = s.VarKey,
        Op = string.IsNullOrEmpty(s.Op) ? "SUM" : s.Op,
        SourceKeys = s.SourceKeys ?? new List<string>(),
        Pattern = s.Pattern ?? "",
        TeamId = teamId ?? s.CorpId,
        TeamName = teamName
    };

    // ==================== 重算 ====================

    private void OnDataUpdated(string id, string value)
    {
        // 合并高频更新事件：同一轮消息循环只重算一次
        if (_recomputePending) return;
        _recomputePending = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { _recomputePending = false; return; }
        _ = dispatcher.InvokeAsync(() =>
        {
            _recomputePending = false;
            RecomputeAll();
        });
    }

    /// <summary>重算全部变量集：收集运行中变量当前值 → 按各变量集的来源取子集 → 聚合</summary>
    public void RecomputeAll()
    {
        List<VariableSet> sets;
        List<KeyValuePair<string, DataDashboardItem>> snapshot;
        lock (_lock)
        {
            sets = EffectiveSetsLocked();
            snapshot = _items.ToList();
        }
        if (sets.Count == 0) return;

        // 运行中变量：VarKey -> 该名下的全部运行值（变量名允许重复：多个 Profile/账号下的同名变量
        // 各占一个值，聚合时全部参与，如 SUM(xiaohongshu.visit_count) 汇总 10 个账户）
        var running = DataPollingService.Instance.GetAllItems()
            .Where(i => !string.IsNullOrEmpty(i.VarKey))
            .GroupBy(i => i.VarKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Value ?? "").ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var set in sets)
        {
            if (!_items.TryGetValue(set.VarKey, out var item)) continue;
            var newValue = ComputeValue(set, running);
            if (!string.Equals(item.Value, newValue, StringComparison.Ordinal))
            {
                item.Value = newValue;
                // 经中央轮询服务广播更新：引用展示/格式渲染/信号灯均订阅该事件
                DataPollingService.Instance.PublishUpdate(item.Id, newValue);
            }
        }
    }

    /// <summary>解析变量集的源变量名集合（显式勾选 ∪ 通配匹配；排除变量集自身，避免自引用）</summary>
    public static List<string> ResolveSourceKeys(VariableSet set, IEnumerable<string> candidates)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in set.SourceKeys)
            if (!string.IsNullOrEmpty(k) && !Equals(k, set.VarKey) && seen.Add(k))
                result.Add(k);
        if (!string.IsNullOrWhiteSpace(set.Pattern))
        {
            var patterns = SplitPatterns(set.Pattern);
            foreach (var k in candidates)
                if (!string.IsNullOrEmpty(k) && !Equals(k, set.VarKey) && seen.Add(k) && patterns.Any(p => MatchesPattern(p, k)))
                    result.Add(k);
        }
        return result;
    }

    private static bool Equals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>按聚合逻辑计算变量集的值；无有效数值时返回 "--"。
    /// runningValues：VarKey -> 该名下的全部运行值（变量名允许重复，同名多值全部参与聚合）</summary>
    public static string ComputeValue(VariableSet set, IReadOnlyDictionary<string, List<string>> runningValues)
    {
        var keys = ResolveSourceKeys(set, runningValues.Keys);
        if (keys.Count == 0) return "--";

        // 展开每个源变量名下的全部运行值（同名重复变量的多个值都算进来）
        var values = new List<string>();
        foreach (var k in keys)
            if (runningValues.TryGetValue(k, out var vs)) values.AddRange(vs);

        if (string.Equals(set.Op, "COUNT", StringComparison.OrdinalIgnoreCase))
        {
            // 计数：统计有运行值（非缺失）的个数
            return values.Count(v => !IsMissing(v)).ToString();
        }

        var nums = new List<double>();
        foreach (var v in values)
        {
            if (IsMissing(v)) continue;
            var n = ParseNumber(v);
            if (n != null) nums.Add(n.Value);
        }
        if (nums.Count == 0) return "--";

        double result = set.Op.ToUpperInvariant() switch
        {
            "AVG" => nums.Average(),
            "MAX" => nums.Max(),
            "MIN" => nums.Min(),
            _ => nums.Sum() // SUM（默认）
        };
        return FormatNum(result);
    }

    private static bool IsMissing(string? v) => string.IsNullOrWhiteSpace(v) || v == "--";

    /// <summary>从值文本提取数字（优先整串解析，支持千分位逗号；否则取首个数字串），失败返回 null</summary>
    public static double? ParseNumber(string s)
    {
        s = s.Trim();
        if (double.TryParse(s.Replace(",", ""), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        var m = Regex.Match(s, @"-?\d[\d,]*(\.\d+)?");
        if (m.Success && double.TryParse(m.Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    /// <summary>拆分通配符串：支持逗号分隔多个模式（含中文逗号），返回去空白后的模式列表</summary>
    public static List<string> SplitPatterns(string? pattern)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(pattern)) return result;
        foreach (var part in pattern.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length > 0) result.Add(p);
        }
        return result;
    }

    /// <summary>通配符匹配：* 匹配任意字符（含空），? 匹配单个字符；忽略大小写</summary>
    public static bool MatchesPattern(string pattern, string key)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        try { return Regex.IsMatch(key, regex, RegexOptions.IgnoreCase); }
        catch { return false; }
    }

    private static string FormatNum(double v) => v == Math.Floor(v) ? v.ToString("0") : v.ToString("0.##");
}
