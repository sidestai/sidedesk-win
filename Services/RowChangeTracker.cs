using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// 行级变更追踪器：记录模块内列表数据的增/改/删操作，供云同步增量推送。
/// </summary>
public class RowChangeTracker
{
    private static readonly Lazy<RowChangeTracker> _instance = new(() => new RowChangeTracker());
    public static RowChangeTracker Instance => _instance.Value;

    /// <summary>单行变更记录</summary>
    public class RowChange
    {
        public string RowId { get; set; } = "";
        public string RowType { get; set; } = "";
        public object? Content { get; set; }
        public int SortOrder { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public bool IsDeleted { get; set; }
    }

    /// <summary>某模块下的全部待推送变更</summary>
    public class ModuleRowChanges
    {
        // key = rowId; 后操作覆盖前操作（同一行多次编辑只保留最终状态）
        public Dictionary<string, RowChange> Changes { get; } = new();
    }

    // key = PanelItem.Id（本地模块 ID）
    private readonly ConcurrentDictionary<string, ModuleRowChanges> _pending = new();

    /// <summary>是否有待推送的变更</summary>
    public bool HasPending => !_pending.IsEmpty && _pending.Any(kv => kv.Value.Changes.Count > 0);

    /// <summary>标记行新增或修改</summary>
    public void MarkModified(string itemLocalId, string rowId, string rowType, object content, int sortOrder)
    {
        if (string.IsNullOrEmpty(itemLocalId) || string.IsNullOrEmpty(rowId)) return;
        var module = _pending.GetOrAdd(itemLocalId, _ => new ModuleRowChanges());
        lock (module)
        {
            module.Changes[rowId] = new RowChange
            {
                RowId = rowId,
                RowType = rowType,
                Content = content,
                SortOrder = sortOrder,
                UpdatedAt = DateTime.Now,
                IsDeleted = false
            };
        }
    }

    /// <summary>标记行删除</summary>
    public void MarkDeleted(string itemLocalId, string rowId)
    {
        if (string.IsNullOrEmpty(itemLocalId) || string.IsNullOrEmpty(rowId)) return;
        var module = _pending.GetOrAdd(itemLocalId, _ => new ModuleRowChanges());
        lock (module)
        {
            module.Changes[rowId] = new RowChange
            {
                RowId = rowId,
                RowType = "",
                Content = null,
                SortOrder = 0,
                UpdatedAt = DateTime.Now,
                IsDeleted = true
            };
        }
    }

    /// <summary>取出某模块的全部变更（取后清除）</summary>
    public ModuleRowChanges? TakeChanges(string itemLocalId)
    {
        if (_pending.TryRemove(itemLocalId, out var changes))
        {
            lock (changes)
            {
                return changes.Changes.Count > 0 ? changes : null;
            }
        }
        return null;
    }

    /// <summary>取出所有模块的变更（取后清除），返回 itemLocalId -> changes 映射</summary>
    public Dictionary<string, ModuleRowChanges> TakeAll()
    {
        var result = new Dictionary<string, ModuleRowChanges>();
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var changes))
            {
                lock (changes)
                {
                    if (changes.Changes.Count > 0)
                        result[key] = changes;
                }
            }
        }
        return result;
    }

    /// <summary>清除所有待推送变更（如用户退出登录时）</summary>
    public void Clear() => _pending.Clear();

    /// <summary>PanelType → 行类型映射</summary>
    public static string GetRowType(PanelType type) => type switch
    {
        PanelType.StickyNotes => "sticky_note",
        PanelType.TodoList => "todo",
        PanelType.Timer => "timer",
        PanelType.Countdown => "countdown",
        PanelType.SignalLight => "signal_light",
        PanelType.Clipboard => "clipboard",
        PanelType.FileArea => "file_area",
        PanelType.Pomodoro => "pomodoro",
        PanelType.DataDashboard => "dashboard",
        PanelType.DigitalEmployee => "digital_employee",
        PanelType.Friend => "friend_user",
        PanelType.QuickGroup => "shortcut",
        _ => "unknown"
    };

    /// <summary>判断模块类型是否支持行级同步</summary>
    public static bool IsRowSyncable(PanelType type) => type switch
    {
        PanelType.StickyNotes or PanelType.TodoList or PanelType.Timer
            or PanelType.Countdown or PanelType.SignalLight
            or PanelType.Clipboard or PanelType.FileArea
            or PanelType.Pomodoro or PanelType.DataDashboard
            or PanelType.DigitalEmployee or PanelType.Friend
            or PanelType.QuickGroup => true,
        _ => false
    };
}
