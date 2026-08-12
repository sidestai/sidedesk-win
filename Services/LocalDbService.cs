using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SideDesk.Models;

namespace SideDesk.Services;

/// <summary>
/// 本地 SQLite 持久层：镜像服务端 bar / item / item_row
/// + 独立数据表（sticky_note/todo_item/clipboard_item）的结构，
/// 替代整文件 windows.json 的全量序列化写盘。
///
/// 表设计（本地表一律 local_ 前缀）：
///   local_bar         —— 窗口（bar），local_uid 对应 WindowConfig.Id；corp_id 非空即团队数据
///   local_item        —— 模块（item），local_uid 对应 PanelItem.Id；corp_id 非空即团队数据；
///                        分组树用 parent_uid 表达（Group 的子面板），sort_order 保持顺序；
///                        item_data 只存模块级设置（不含行列表数据）
///   local_item_row  —— 行级数据（每条便签/待办/计时器…一行，镜像服务端 item_row）
///   local_sticky_note / local_todo_item / local_clipboard_item
///                     —— 独立数据表（镜像服务端数据表，按 uid 存取）
///   local_app_state   —— 应用级状态 KV（左右停靠、后台看板项）
/// </summary>
public static class LocalDbService
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideDesk");

    public static string DbPath => Path.Combine(Dir, "sidedesk.db");

    public static bool DbExists => File.Exists(DbPath);

    /// <summary>打开连接并初始化 PRAGMA / 建表。</summary>
    public static SqliteConnection Open()
    {
        if (!System.IO.Directory.Exists(Dir)) System.IO.Directory.CreateDirectory(Dir);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema(conn);
        CleanupLegacyXidOnce(conn);
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        // 兼容早期构建：表曾用名 sidedesk_bar/sidedesk_item/sidedesk_item_row，统一改为 local_*
        RenameLegacyTable(conn, "sidedesk_bar", "local_bar");
        RenameLegacyTable(conn, "sidedesk_item", "local_item");
        RenameLegacyTable(conn, "sidedesk_item_row", "local_item_row");
        AddColumnIfMissing(conn, "local_item_row", "row_server_id", "TEXT");
        AddColumnIfMissing(conn, "local_item_row", "data_server_id", "TEXT");
        AddColumnIfMissing(conn, "local_item", "corp_id", "TEXT");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS local_bar (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  local_uid     TEXT NOT NULL UNIQUE,
  server_id     TEXT,
  title         TEXT,
  bg_color      TEXT,
  window_type   INTEGER NOT NULL DEFAULT 0,
  url           TEXT,
  is_edit_mode  INTEGER NOT NULL DEFAULT 0,
  is_app_mode   INTEGER NOT NULL DEFAULT 0,
  show_in_tabs  INTEGER NOT NULL DEFAULT 1,
  owner_type    TEXT NOT NULL DEFAULT 'personal',
  corp_id       TEXT,
  share_mode    TEXT,
  source_bar_id TEXT,
  enabled_tools TEXT,
  created_at    TEXT,
  updated_at    TEXT,
  deleted_at    TEXT
);

CREATE TABLE IF NOT EXISTS local_item (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  local_uid   TEXT NOT NULL UNIQUE,
  server_id   TEXT,
  bar_uid     TEXT NOT NULL,
  parent_uid  TEXT,
  sort_order  INTEGER NOT NULL DEFAULT 0,
  title       TEXT,
  item_type   TEXT NOT NULL,
  group_id    TEXT,
  item_data   TEXT,
  owner_type  TEXT NOT NULL DEFAULT 'personal',
  team_id     TEXT,
  team_name   TEXT,
  corp_id     TEXT,
  created_at  TEXT,
  updated_at  TEXT,
  deleted_at  TEXT
);

CREATE INDEX IF NOT EXISTS idx_item_bar ON local_item(bar_uid, sort_order);

CREATE TABLE IF NOT EXISTS local_item_row (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  item_uid      TEXT NOT NULL,
  row_uid       TEXT NOT NULL,
  row_type      TEXT NOT NULL,
  content       TEXT,
  sort_order    INTEGER NOT NULL DEFAULT 0,
  row_server_id TEXT,
  data_server_id TEXT,
  created_at    TEXT,
  updated_at    TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_row_item_rowuid ON local_item_row(item_uid, row_uid);

CREATE TABLE IF NOT EXISTS local_row_id_map (
  row_uid        TEXT PRIMARY KEY,
  row_server_id  TEXT,
  data_server_id TEXT
);

CREATE TABLE IF NOT EXISTS local_sticky_note (
  uid        TEXT PRIMARY KEY,
  content    TEXT,
  created_at TEXT,
  updated_at TEXT
);

CREATE TABLE IF NOT EXISTS local_todo_item (
  uid                   TEXT PRIMARY KEY,
  text                  TEXT,
  description           TEXT,
  is_done               INTEGER NOT NULL DEFAULT 0,
  due_time              TEXT,
  remind_before_minutes INTEGER NOT NULL DEFAULT -1,
  reminded              INTEGER NOT NULL DEFAULT 0,
  created_at            TEXT
);

CREATE TABLE IF NOT EXISTS local_clipboard_item (
  uid          TEXT PRIMARY KEY,
  content_type TEXT NOT NULL DEFAULT 'Text',
  content      TEXT,
  saved_at     TEXT
);

CREATE TABLE IF NOT EXISTS local_app_state (
  key   TEXT PRIMARY KEY,
  value TEXT
);

CREATE TABLE IF NOT EXISTS local_screen_time_hourly (
  date    TEXT NOT NULL,
  hour    INTEGER NOT NULL,
  process TEXT NOT NULL,
  seconds INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (date, hour, process)
);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>旧表名存在且新表名不存在时，无损改名（早期构建兼容）。</summary>
    private static void RenameLegacyTable(SqliteConnection conn, string oldName, string newName)
    {
        long Count(string name)
        {
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
            q.Parameters.AddWithValue("$n", name);
            return Convert.ToInt64(q.ExecuteScalar());
        }
        if (Count(oldName) == 0 || Count(newName) > 0) return;
        using var rename = conn.CreateCommand();
        rename.CommandText = $"ALTER TABLE {oldName} RENAME TO {newName}";
        rename.ExecuteNonQuery();
    }

    /// <summary>表缺列时补列（早期构建兼容）。表不存在时跳过（全新库由后续 CREATE TABLE 建出含新列的表）。</summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string colType)
    {
        using (var exists = conn.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
            exists.Parameters.AddWithValue("$n", table);
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return;
        }
        using var q = conn.CreateCommand();
        q.CommandText = $"PRAGMA table_info({table})";
        using var rd = q.ExecuteReader();
        while (rd.Read())
        {
            if (string.Equals(rd.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        rd.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {colType}";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 一次性清理：后端已废弃 encrypt3（xid- 前缀密文）统一改用 encrypt2，服务端数据表已清空。
    /// 本地缓存的 xid- ServerId/CorpId/行ID 无法再与服务端匹配，置空后下次同步以新格式重建映射。
    /// </summary>
    private static void CleanupLegacyXidOnce(SqliteConnection conn)
    {
        if (GetState(conn, "xid3_cleanup_done") == "1") return;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE local_bar      SET server_id=NULL      WHERE server_id LIKE 'xid-%';
UPDATE local_bar      SET corp_id=NULL        WHERE corp_id LIKE 'xid-%';
UPDATE local_bar      SET source_bar_id=NULL  WHERE source_bar_id LIKE 'xid-%';
UPDATE local_item     SET server_id=NULL      WHERE server_id LIKE 'xid-%';
UPDATE local_item     SET corp_id=NULL        WHERE corp_id LIKE 'xid-%';
UPDATE local_item     SET team_id=NULL        WHERE team_id LIKE 'xid-%';
UPDATE local_item_row SET row_server_id=NULL  WHERE row_server_id LIKE 'xid-%';
UPDATE local_item_row SET data_server_id=NULL WHERE data_server_id LIKE 'xid-%';
DELETE FROM local_row_id_map WHERE row_server_id LIKE 'xid-%' OR data_server_id LIKE 'xid-%';";
            cmd.ExecuteNonQuery();
        }
        UpsertState(conn, "xid3_cleanup_done", "1");
        System.Diagnostics.Debug.WriteLine("[LocalDb] xid- 旧密文一次性清理完成");
    }

    // ==================== 写入 ====================

    /// <summary>将整个管理器状态写入 SQLite（单事务，全量 upsert + 清理已删除行）。</summary>
    public static void SaveManager(WindowConfigManager mgr)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTime.Now.ToString("o");

            // 快捷分组一次性迁移：quickgroups.json（及更早的 bookmarks/aitools/programs）
            // 的分组数据写入对应快捷面板，此后快捷项随面板走行级存储
            if (GetState(conn, "quickgroup_migrated") != "1")
            {
                try { QuickGroupManager.MigrateLegacyOnce(); } catch { }
                UpsertState(conn, "quickgroup_migrated", "1");
            }

            var liveBarUids = new HashSet<string>();
            var liveItemUids = new HashSet<string>();

            foreach (var win in mgr.Windows)
            {
                liveBarUids.Add(win.Id);
                UpsertBar(conn, win, now);

                int order = 0;
                foreach (var p in win.Panels)
                    SaveItemTree(conn, p, win.Id, null, ref order, now, liveItemUids);
            }

            // 清理内存中已不存在的行（窗口/模块删除）
            DeleteMissing(conn, "local_bar", "local_uid", liveBarUids);
            DeleteMissing(conn, "local_item", "local_uid", liveItemUids);

            // 行级数据：全量重写（数据量小，语义最简单：删掉的行/孤儿行自动清理）
            // 重写前先留存已缓存的服务端行 ID（映射表优先，行表已有值兜底），重插后回填
            var serverIdMap = LoadRowServerIdMap(conn);
            using (var delRows = conn.CreateCommand())
            {
                delRows.CommandText = @"DELETE FROM local_item_row;
DELETE FROM local_sticky_note;
DELETE FROM local_todo_item;
DELETE FROM local_clipboard_item;";
                delRows.ExecuteNonQuery();
            }
            foreach (var win in mgr.Windows)
                foreach (var p in EnumerateAllPanelsLocal(win.Panels))
                    SaveItemRows(conn, p, now, serverIdMap);
            UpsertState(conn, "row_storage_migrated", "1");

            // 应用级状态
            UpsertState(conn, "left_dock", JsonConvert.SerializeObject(mgr.LeftDock));
            UpsertState(conn, "right_dock", JsonConvert.SerializeObject(mgr.RightDock));
            UpsertState(conn, "background_dashboard_items", JsonConvert.SerializeObject(mgr.BackgroundDashboardItems));
            UpsertState(conn, "variable_sets", JsonConvert.SerializeObject(mgr.VariableSets));

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void SaveItemTree(SqliteConnection conn, PanelItem p, string barUid,
        string? parentUid, ref int order, string now, HashSet<string> liveUids)
    {
        liveUids.Add(p.Id);
        UpsertItem(conn, p, barUid, parentUid, order++, now);
        if (p.ChildPanels != null)
        {
            int childOrder = 0;
            foreach (var c in p.ChildPanels)
                SaveItemTree(conn, c, barUid, p.Id, ref childOrder, now, liveUids);
        }
    }

    private static void UpsertBar(SqliteConnection conn, WindowConfig win, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_bar
  (local_uid, server_id, title, bg_color, window_type, url, is_edit_mode, is_app_mode,
   show_in_tabs, owner_type, corp_id, share_mode, source_bar_id, enabled_tools, created_at, updated_at)
VALUES
  ($uid, $sid, $title, $bg, $wtype, $url, $edit, $app, $tabs, $owner, $corp, $share, $src, $tools, $created, $updated)
ON CONFLICT(local_uid) DO UPDATE SET
  server_id=excluded.server_id, title=excluded.title, bg_color=excluded.bg_color,
  window_type=excluded.window_type, url=excluded.url, is_edit_mode=excluded.is_edit_mode,
  is_app_mode=excluded.is_app_mode, show_in_tabs=excluded.show_in_tabs, owner_type=excluded.owner_type,
  corp_id=excluded.corp_id, share_mode=excluded.share_mode, source_bar_id=excluded.source_bar_id,
  enabled_tools=excluded.enabled_tools, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", win.Id);
        cmd.Parameters.AddWithValue("$sid", (object?)win.ServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", win.Name);
        cmd.Parameters.AddWithValue("$bg", (object?)win.BGColor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wtype", (int)win.Type);
        cmd.Parameters.AddWithValue("$url", (object?)win.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$edit", win.IsEditMode ? 1 : 0);
        cmd.Parameters.AddWithValue("$app", win.IsAppMode ? 1 : 0);
        cmd.Parameters.AddWithValue("$tabs", win.ShowInTabs ? 1 : 0);
        cmd.Parameters.AddWithValue("$owner", win.OwnerType);
        cmd.Parameters.AddWithValue("$corp", (object?)win.CorpId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$share", (object?)win.ShareMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", (object?)win.SourceBarId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tools", JsonConvert.SerializeObject(win.EnabledTools));
        cmd.Parameters.AddWithValue("$created", win.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>item_data 序列化时排除 ChildPanels 与行列表数据（子面板/行数据均以独立行存储）。</summary>
    private static readonly JsonSerializerSettings _itemDataSettings = new()
    {
        ContractResolver = new PanelItemContractResolver()
    };

    private static readonly HashSet<string> _excludedItemDataProps = new()
    {
        nameof(PanelItem.ChildPanels),
        nameof(PanelItem.StickyNotes),
        nameof(PanelItem.TodoItems),
        nameof(PanelItem.ClipboardItems),
        nameof(PanelItem.TimerItems),
        nameof(PanelItem.CountdownItems),
        nameof(PanelItem.SignalLights),
        nameof(PanelItem.FileItems),
        nameof(PanelItem.Pomodoro),
        nameof(PanelItem.DashboardItems),
        nameof(PanelItem.DigitalEmployeeIds),
        nameof(PanelItem.FriendUserIds),
        nameof(PanelItem.QuickItems)
    };

    private class PanelItemContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(System.Reflection.MemberInfo member, MemberSerialization memberSerialization)
        {
            var prop = base.CreateProperty(member, memberSerialization);
            if (prop.PropertyName != null && _excludedItemDataProps.Contains(prop.PropertyName))
                prop.ShouldSerialize = _ => false;
            return prop;
        }
    }

    private static IEnumerable<PanelItem> EnumerateAllPanelsLocal(List<PanelItem> panels)
    {
        foreach (var p in panels)
        {
            yield return p;
            if (p.ChildPanels != null)
                foreach (var c in EnumerateAllPanelsLocal(p.ChildPanels))
                    yield return c;
        }
    }

    // ==================== 行级数据写入 ====================

    /// <summary>读取 row_uid → (row_server_id, data_server_id) 留存映射：
    /// local_row_id_map（拉取时回写的权威源）优先，行表已有值兜底。</summary>
    private static Dictionary<string, (string? RowServerId, string? DataServerId)> LoadRowServerIdMap(SqliteConnection conn)
    {
        var map = new Dictionary<string, (string?, string?)>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT row_uid, row_server_id, data_server_id FROM local_item_row WHERE row_server_id IS NOT NULL OR data_server_id IS NOT NULL";
            using var rd = q.ExecuteReader();
            while (rd.Read())
                map[rd.GetString(0)] = (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
        }
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT row_uid, row_server_id, data_server_id FROM local_row_id_map";
            using var rd = q.ExecuteReader();
            while (rd.Read())
                map[rd.GetString(0)] = (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
        }
        return map;
    }

    /// <summary>拉取回写：缓存服务端行 ID（row_uid → rowServerId/dataServerId）。
    /// 写入映射表（不依赖行已落库），并尽力同步更新当前行表。</summary>
    public static void SaveRowServerIds(IEnumerable<(string RowUid, string? RowServerId, string? DataServerId)> entries)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (rowUid, rowServerId, dataServerId) in entries)
            {
                if (string.IsNullOrEmpty(rowUid)) continue;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO local_row_id_map (row_uid, row_server_id, data_server_id)
VALUES ($uid, $rsid, $dsid)
ON CONFLICT(row_uid) DO UPDATE SET
  row_server_id = COALESCE(excluded.row_server_id, local_row_id_map.row_server_id),
  data_server_id = COALESCE(excluded.data_server_id, local_row_id_map.data_server_id);";
                    cmd.Parameters.AddWithValue("$uid", rowUid);
                    cmd.Parameters.AddWithValue("$rsid", (object?)rowServerId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$dsid", (object?)dataServerId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var upd = conn.CreateCommand())
                {
                    upd.CommandText = @"UPDATE local_item_row
SET row_server_id = COALESCE($rsid, row_server_id),
    data_server_id = COALESCE($dsid, data_server_id)
WHERE row_uid = $uid;";
                    upd.Parameters.AddWithValue("$uid", rowUid);
                    upd.Parameters.AddWithValue("$rsid", (object?)rowServerId ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$dsid", (object?)dataServerId ?? DBNull.Value);
                    upd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] 回写服务端行 ID 失败: {ex.Message}");
        }
    }

    /// <summary>批量查询已缓存的服务端行 ID（row_uid → serverId）：
    /// local_item_row 先查，local_row_id_map（拉取回写的权威源）覆盖。推送前用于组装 serverId 寻址载荷。</summary>
    public static Dictionary<string, (string? RowServerId, string? DataServerId)> GetRowServerIds(IEnumerable<string> rowUids)
    {
        var result = new Dictionary<string, (string?, string?)>();
        var uids = rowUids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (uids.Count == 0) return result;
        try
        {
            string inList = string.Join(",", uids.Select((_, i) => $"$u{i}"));
            using var conn = Open();
            void Query(string table)
            {
                using var q = conn.CreateCommand();
                q.CommandText = $"SELECT row_uid, row_server_id, data_server_id FROM {table} WHERE row_uid IN ({inList})";
                for (int i = 0; i < uids.Count; i++)
                    q.Parameters.AddWithValue($"$u{i}", uids[i]);
                using var rd = q.ExecuteReader();
                while (rd.Read())
                    result[rd.GetString(0)] = (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
            }
            Query("local_item_row");
            Query("local_row_id_map"); // 权威源，覆盖行表旧值
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] 查询服务端行 ID 失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>将面板的行列表数据写入 local_item_row + 独立数据表（镜像服务端结构）。</summary>
    private static void SaveItemRows(SqliteConnection conn, PanelItem p, string now,
        Dictionary<string, (string? RowServerId, string? DataServerId)> serverIdMap)
    {
        void Insert(string rowUid, string rowType, string content, int order)
        {
            serverIdMap.TryGetValue(rowUid, out var sids);
            InsertRow(conn, p.Id, rowUid, rowType, content, order, now, sids.RowServerId, sids.DataServerId);
        }

        if (p.StickyNotes != null)
        {
            int o = 0;
            foreach (var n in p.StickyNotes)
            {
                Insert(n.Id, "sticky_note", JsonConvert.SerializeObject(n), o++);
                UpsertStickyNote(conn, n);
            }
        }
        if (p.TodoItems != null)
        {
            int o = 0;
            foreach (var t in p.TodoItems)
            {
                Insert(t.Id, "todo", JsonConvert.SerializeObject(t), o++);
                UpsertTodoItem(conn, t);
            }
        }
        if (p.ClipboardItems != null)
        {
            int o = 0;
            foreach (var c in p.ClipboardItems)
            {
                Insert(c.Id, "clipboard", JsonConvert.SerializeObject(c), o++);
                UpsertClipboardItem(conn, c);
            }
        }
        if (p.TimerItems != null)
        {
            int o = 0;
            foreach (var t in p.TimerItems)
                Insert(t.Id, "timer", JsonConvert.SerializeObject(t), o++);
        }
        if (p.CountdownItems != null)
        {
            int o = 0;
            foreach (var c in p.CountdownItems)
                Insert(c.Id, "countdown", JsonConvert.SerializeObject(c), o++);
        }
        if (p.SignalLights != null)
        {
            int o = 0;
            foreach (var s in p.SignalLights)
                Insert(s.Id, "signal_light", JsonConvert.SerializeObject(s), o++);
        }
        if (p.FileItems != null)
        {
            int o = 0;
            foreach (var f in p.FileItems)
                Insert(f.Id, "file_area", JsonConvert.SerializeObject(f), o++);
        }
        // 番茄钟：单行记录（设置+运行状态），与其他行同步面板统一走行级存储
        if (p.Pomodoro != null)
            Insert(p.Pomodoro.Id, "pomodoro", JsonConvert.SerializeObject(p.Pomodoro), 0);
        if (p.DashboardItems != null)
        {
            int o = 0;
            foreach (var d in p.DashboardItems)
                Insert(d.Id, "dashboard", JsonConvert.SerializeObject(d), o++);
        }
        // 数字员工/好友：引用 Id 列表包装为行对象（Id = 引用 Id）
        if (p.DigitalEmployeeIds != null)
        {
            int o = 0;
            foreach (var id in p.DigitalEmployeeIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                Insert(id, "digital_employee",
                    JsonConvert.SerializeObject(new DigitalEmployeeRow { Id = id, EmployeeId = id }), o++);
            }
        }
        if (p.FriendUserIds != null)
        {
            int o = 0;
            foreach (var id in p.FriendUserIds)
                Insert(id.ToString(), "friend_user",
                    JsonConvert.SerializeObject(new FriendUserRow { Id = id.ToString(), UserId = id.ToString() }), o++);
        }
        // 快捷方式：每个快捷项一行（原 quickgroups.json 数据已迁入面板）
        if (p.QuickItems != null)
        {
            int o = 0;
            foreach (var q in p.QuickItems)
                Insert(q.Id, "shortcut", JsonConvert.SerializeObject(q), o++);
        }
    }

    private static void InsertRow(SqliteConnection conn, string itemUid, string rowUid,
        string rowType, string content, int order, string now, string? rowServerId, string? dataServerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_item_row
  (item_uid, row_uid, row_type, content, sort_order, row_server_id, data_server_id, created_at, updated_at)
VALUES ($item, $row, $type, $content, $order, $rsid, $dsid, $created, $updated)";
        cmd.Parameters.AddWithValue("$item", itemUid);
        cmd.Parameters.AddWithValue("$row", rowUid);
        cmd.Parameters.AddWithValue("$type", rowType);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$order", order);
        cmd.Parameters.AddWithValue("$rsid", (object?)rowServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dsid", (object?)dataServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStickyNote(SqliteConnection conn, StickyNote n)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_sticky_note (uid, content, created_at, updated_at)
VALUES ($uid, $content, $created, $updated)
ON CONFLICT(uid) DO UPDATE SET content=excluded.content, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", n.Id);
        cmd.Parameters.AddWithValue("$content", n.Content);
        cmd.Parameters.AddWithValue("$created", n.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", n.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertTodoItem(SqliteConnection conn, TodoItem t)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_todo_item
  (uid, text, description, is_done, due_time, remind_before_minutes, reminded, created_at)
VALUES ($uid, $text, $desc, $done, $due, $remind, $reminded, $created)
ON CONFLICT(uid) DO UPDATE SET
  text=excluded.text, description=excluded.description, is_done=excluded.is_done,
  due_time=excluded.due_time, remind_before_minutes=excluded.remind_before_minutes,
  reminded=excluded.reminded;";
        cmd.Parameters.AddWithValue("$uid", t.Id);
        cmd.Parameters.AddWithValue("$text", t.Text);
        cmd.Parameters.AddWithValue("$desc", t.Description);
        cmd.Parameters.AddWithValue("$done", t.IsDone ? 1 : 0);
        cmd.Parameters.AddWithValue("$due", (object?)t.DueTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remind", t.RemindBeforeMinutes);
        cmd.Parameters.AddWithValue("$reminded", t.Reminded ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", t.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertClipboardItem(SqliteConnection conn, ClipboardSavedItem c)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_clipboard_item (uid, content_type, content, saved_at)
VALUES ($uid, $type, $content, $saved)
ON CONFLICT(uid) DO UPDATE SET content_type=excluded.content_type, content=excluded.content,
  saved_at=excluded.saved_at;";
        cmd.Parameters.AddWithValue("$uid", c.Id);
        cmd.Parameters.AddWithValue("$type", c.ContentType);
        cmd.Parameters.AddWithValue("$content", c.Content);
        cmd.Parameters.AddWithValue("$saved", c.SavedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertItem(SqliteConnection conn, PanelItem p, string barUid,
        string? parentUid, int order, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_item
  (local_uid, server_id, bar_uid, parent_uid, sort_order, title, item_type, group_id,
   item_data, owner_type, team_id, team_name, corp_id, created_at, updated_at)
VALUES
  ($uid, $sid, $bar, $parent, $order, $title, $type, $group, $data, $owner, $team, $teamname, $corp, $created, $updated)
ON CONFLICT(local_uid) DO UPDATE SET
  server_id=excluded.server_id, bar_uid=excluded.bar_uid, parent_uid=excluded.parent_uid,
  sort_order=excluded.sort_order, title=excluded.title, item_type=excluded.item_type,
  group_id=excluded.group_id, item_data=excluded.item_data, owner_type=excluded.owner_type,
  team_id=excluded.team_id, team_name=excluded.team_name, corp_id=excluded.corp_id, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", p.Id);
        cmd.Parameters.AddWithValue("$sid", (object?)p.ServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bar", barUid);
        cmd.Parameters.AddWithValue("$parent", (object?)parentUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$order", order);
        cmd.Parameters.AddWithValue("$title", (object?)p.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", p.Type.ToString());
        cmd.Parameters.AddWithValue("$group", (object?)p.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$data", JsonConvert.SerializeObject(p, _itemDataSettings));
        cmd.Parameters.AddWithValue("$owner", p.OwnerType);
        cmd.Parameters.AddWithValue("$team", (object?)p.TeamId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$teamname", (object?)p.TeamName ?? DBNull.Value);
        // corp_id 与 local_bar 语义对齐：非空即团队数据（团队时与 team_id 同值，内存模型仅维护 TeamId 单点）
        cmd.Parameters.AddWithValue("$corp", p.OwnerType == "team" && !string.IsNullOrEmpty(p.TeamId) ? p.TeamId : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertState(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_app_state (key, value) VALUES ($k, $v)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除表中 uid 不在内存集合里的行（按 IN 分批，避免参数上限）。</summary>
    private static void DeleteMissing(SqliteConnection conn, string table, string column, HashSet<string> liveUids)
    {
        // 先取出库中全部 uid，内存求差集，再按差集删除（数据量小，简单可靠）
        var dbUids = new List<string>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT {column} FROM {table}";
            using var rd = q.ExecuteReader();
            while (rd.Read()) dbUids.Add(rd.GetString(0));
        }
        var dead = dbUids.Where(u => !liveUids.Contains(u)).ToList();
        if (dead.Count == 0) return;

        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < dead.Count; i++)
        {
            names.Add($"$d{i}");
            cmd.Parameters.AddWithValue($"$d{i}", dead[i]);
        }
        cmd.CommandText = $"DELETE FROM {table} WHERE {column} IN ({string.Join(",", names)})";
        cmd.ExecuteNonQuery();
    }

    // ==================== 读取 ====================

    /// <summary>从 SQLite 加载完整管理器状态。</summary>
    public static WindowConfigManager LoadManager()
    {
        var mgr = new WindowConfigManager();
        using var conn = Open();

        // 1) bars
        var bars = new List<(WindowConfig Win, DateTime Created)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT local_uid, server_id, title, bg_color, window_type, url,
  is_edit_mode, is_app_mode, show_in_tabs, owner_type, corp_id, share_mode, source_bar_id,
  enabled_tools, created_at
FROM local_bar WHERE deleted_at IS NULL ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var win = new WindowConfig
                {
                    Id = rd.GetString(0),
                    ServerId = rd.IsDBNull(1) ? null : rd.GetString(1),
                    Name = rd.IsDBNull(2) ? Framework.Lang.T("新窗口") : rd.GetString(2),
                    BGColor = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Type = (WindowType)rd.GetInt32(4),
                    Url = rd.IsDBNull(5) ? null : rd.GetString(5),
                    IsEditMode = rd.GetInt64(6) != 0,
                    IsAppMode = rd.GetInt64(7) != 0,
                    ShowInTabs = rd.GetInt64(8) != 0,
                    OwnerType = rd.GetString(9),
                    CorpId = rd.IsDBNull(10) ? null : rd.GetString(10),
                    ShareMode = rd.IsDBNull(11) ? null : rd.GetString(11),
                    SourceBarId = rd.IsDBNull(12) ? null : rd.GetString(12),
                    EnabledTools = SafeDeserializeList<string>(rd.IsDBNull(13) ? null : rd.GetString(13)) ?? new(),
                };
                DateTime created = DateTime.Now;
                if (!rd.IsDBNull(14) && DateTime.TryParse(rd.GetString(14), out var c)) created = c;
                win.CreatedAt = created;
                bars.Add((win, created));
            }
        }

        // 2) items（全部读出后按 bar_uid/parent_uid 组装树）
        var items = new Dictionary<string, PanelItem>();        // local_uid -> panel
        var barOf = new Dictionary<string, string>();            // local_uid -> bar_uid
        var parentOf = new Dictionary<string, string?>();        // local_uid -> parent_uid
        var barRoots = new List<(string BarUid, PanelItem Panel, int Order)>();

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = @"SELECT local_uid, bar_uid, parent_uid, sort_order, item_data
FROM local_item WHERE deleted_at IS NULL ORDER BY sort_order, id";
            using var rd = cmd2.ExecuteReader();
            while (rd.Read())
            {
                string uid = rd.GetString(0);
                string barUid = rd.GetString(1);
                string? parentUid = rd.IsDBNull(2) ? null : rd.GetString(2);
                int order = (int)rd.GetInt64(3);
                string? dataJson = rd.IsDBNull(4) ? null : rd.GetString(4);

                PanelItem? p = null;
                if (!string.IsNullOrEmpty(dataJson))
                {
                    try { p = JsonConvert.DeserializeObject<PanelItem>(dataJson); } catch { }
                }
                p ??= new PanelItem();
                p.Id = uid;
                items[uid] = p;
                barOf[uid] = barUid;
                parentOf[uid] = parentUid;
                if (parentUid == null) barRoots.Add((barUid, p, order));
            }
        }

        // 组装父子关系
        foreach (var (uid, parentUid) in parentOf)
        {
            if (parentUid == null) continue;
            if (items.TryGetValue(parentUid, out var parent) && items.TryGetValue(uid, out var child))
            {
                parent.ChildPanels ??= new List<PanelItem>();
                parent.ChildPanels.Add(child);
            }
        }

        // 挂到各 bar
        foreach (var (win, _) in bars)
        {
            win.Panels = barRoots.Where(r => r.BarUid == win.Id)
                                 .OrderBy(r => r.Order)
                                 .Select(r => r.Panel)
                                 .ToList();
            mgr.Windows.Add(win);
        }

        // 2.5) 行级数据：从 local_item_row 组装回面板列表
        //      （升级前的旧库无行表数据，item_data blob 里已含列表，直接沿用）
        if (GetState(conn, "row_storage_migrated") == "1")
            AttachRowsToPanels(conn, items.Values);

        // 3) 应用级状态
        string? left = GetState(conn, "left_dock");
        string? right = GetState(conn, "right_dock");
        string? bgDash = GetState(conn, "background_dashboard_items");
        string? varSets = GetState(conn, "variable_sets");
        if (!string.IsNullOrEmpty(left))
            mgr.LeftDock = JsonConvert.DeserializeObject<DockWindowState>(left) ?? new DockWindowState();
        if (!string.IsNullOrEmpty(right))
            mgr.RightDock = JsonConvert.DeserializeObject<DockWindowState>(right) ?? new DockWindowState();
        if (!string.IsNullOrEmpty(bgDash))
            mgr.BackgroundDashboardItems = SafeDeserializeList<DataDashboardItem>(bgDash) ?? new();
        if (!string.IsNullOrEmpty(varSets))
            mgr.VariableSets = SafeDeserializeList<VariableSet>(varSets) ?? new();

        return mgr;
    }

    private static List<T>? SafeDeserializeList<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<List<T>>(json); }
        catch { return null; }
    }

    /// <summary>从 local_item_row 按 row_type 还原各面板的行列表。</summary>
    private static void AttachRowsToPanels(SqliteConnection conn, IEnumerable<PanelItem> panels)
    {
        var rowsByItem = new Dictionary<string, List<(string Type, string? Content)>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT item_uid, row_type, content FROM local_item_row ORDER BY sort_order, id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string itemUid = rd.GetString(0);
                if (!rowsByItem.TryGetValue(itemUid, out var list))
                    rowsByItem[itemUid] = list = new List<(string, string?)>();
                list.Add((rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
            }
        }

        foreach (var p in panels)
        {
            rowsByItem.TryGetValue(p.Id, out var rows);
            rows ??= new List<(string, string?)>();
            p.StickyNotes = RowsToList<StickyNote>(rows, "sticky_note");
            p.TodoItems = RowsToList<TodoItem>(rows, "todo");
            p.ClipboardItems = RowsToList<ClipboardSavedItem>(rows, "clipboard");
            p.TimerItems = RowsToList<TimerItem>(rows, "timer");
            p.CountdownItems = RowsToList<CountdownItem>(rows, "countdown");
            p.SignalLights = RowsToList<SignalLightItem>(rows, "signal_light");
            p.FileItems = RowsToList<FileAreaItem>(rows, "file_area");
            // 番茄钟单行：有行则覆盖；无行保留 item_data 旧值（旧库升级首载兼容），仍为空则不干预
            var pomodoros = RowsToList<PomodoroSettings>(rows, "pomodoro");
            if (pomodoros != null && pomodoros.Count > 0)
                p.Pomodoro = pomodoros[0];
            // 数据看板/数字员工/好友/快捷方式：有行则覆盖；无行保留 item_data 旧值（旧库升级首载兼容）
            var dashboards = RowsToList<DataDashboardItem>(rows, "dashboard");
            if (dashboards != null)
                p.DashboardItems = dashboards;
            var deRows = RowsToList<DigitalEmployeeRow>(rows, "digital_employee");
            if (deRows != null)
                p.DigitalEmployeeIds = deRows.Select(r => r.EmployeeId).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var fuRows = RowsToList<FriendUserRow>(rows, "friend_user");
            if (fuRows != null)
                p.FriendUserIds = fuRows.Select(r => r.UserId).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var shortcuts = RowsToList<QuickItem>(rows, "shortcut");
            if (shortcuts != null)
                p.QuickItems = shortcuts;

            // 行同步类型面板无行时兜底空列表（区别于从未初始化的 null）；
            // 非行同步面板的列表属性保持 null（RowsToList 无匹配行时返回 null）
            if (RowChangeTracker.IsRowSyncable(p.Type))
            {
                switch (p.Type)
                {
                    case PanelType.StickyNotes: p.StickyNotes ??= new List<StickyNote>(); break;
                    case PanelType.TodoList: p.TodoItems ??= new List<TodoItem>(); break;
                    case PanelType.Clipboard: p.ClipboardItems ??= new List<ClipboardSavedItem>(); break;
                    case PanelType.Timer: p.TimerItems ??= new List<TimerItem>(); break;
                    case PanelType.Countdown: p.CountdownItems ??= new List<CountdownItem>(); break;
                    case PanelType.SignalLight: p.SignalLights ??= new List<SignalLightItem>(); break;
                    case PanelType.FileArea: p.FileItems ??= new List<FileAreaItem>(); break;
                    case PanelType.Pomodoro: p.Pomodoro ??= new PomodoroSettings(); break;
                    case PanelType.DataDashboard: p.DashboardItems ??= new List<DataDashboardItem>(); break;
                    case PanelType.DigitalEmployee: p.DigitalEmployeeIds ??= new List<string>(); break;
                    case PanelType.Friend: p.FriendUserIds ??= new List<string>(); break;
                    case PanelType.QuickGroup: p.QuickItems ??= new List<QuickItem>(); break;
                }
            }
        }
    }

    private static List<T>? RowsToList<T>(List<(string Type, string? Content)> rows, string rowType)
    {
        List<T>? list = null;
        foreach (var (t, content) in rows)
        {
            if (t != rowType || string.IsNullOrEmpty(content)) continue;
            try
            {
                var item = JsonConvert.DeserializeObject<T>(content);
                if (item != null) (list ??= new List<T>()).Add(item);
            }
            catch { }
        }
        return list;
    }

    private static string? GetState(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM local_app_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v as string;
    }

    // ==================== JSON 互导（迁移 / 备份恢复兜底）====================

    /// <summary>
    /// 将旧版 windows.json 内容导入 SQLite，成功后把 json 文件改名为 .bak-migrated 保留。
    /// 返回是否导入成功。
    /// </summary>
    public static bool ImportFromJsonFile(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            var mgr = JsonConvert.DeserializeObject<WindowConfigManager>(json);
            if (mgr == null) return false;
            SanitizeLegacyXid(mgr);
            SaveManager(mgr);
            try { File.Move(jsonPath, jsonPath + ".bak-migrated", overwrite: true); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] 导入 windows.json 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 备份恢复后：若存在待导入的 windows.json（比 db 新或由恢复写入），重新导入 SQLite。
    /// 用于 RestoreFromBundleJson 写回 windows.json 后的再导入。
    /// </summary>
    public static bool ReimportJsonIfExists(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return false;
            return ImportFromJsonFile(jsonPath);
        }
        catch { return false; }
    }

    /// <summary>导入 JSON 时清除 xid-（已废弃 encrypt3）密文，防止无效 ServerId 重新入库。</summary>
    private static void SanitizeLegacyXid(WindowConfigManager mgr)
    {
        foreach (var win in mgr.Windows)
        {
            if (win.ServerId != null && win.ServerId.StartsWith("xid-")) win.ServerId = null;
            if (win.CorpId != null && win.CorpId.StartsWith("xid-")) win.CorpId = null;
            foreach (var p in EnumerateAllPanelsLocal(win.Panels))
            {
                if (p.ServerId != null && p.ServerId.StartsWith("xid-")) p.ServerId = null;
                if (p.TeamId != null && p.TeamId.StartsWith("xid-")) p.TeamId = null;
            }
        }
    }

    /// <summary>将当前管理器状态导出为 windows.json 快照（供备份打包与灾难恢复兜底）。</summary>
    public static void ExportJsonSnapshot(WindowConfigManager mgr, string jsonPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(jsonPath)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(mgr, Formatting.Indented);
            // 原子写：临时文件 + 替换，避免写坏快照
            string tmp = jsonPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(jsonPath))
            {
                try { File.Replace(tmp, jsonPath, jsonPath + ".bak", ignoreMetadataErrors: true); }
                catch { File.Move(tmp, jsonPath, overwrite: true); }
            }
            else
            {
                File.Move(tmp, jsonPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalDb] 导出 JSON 快照失败: {ex.Message}");
        }
    }
}
