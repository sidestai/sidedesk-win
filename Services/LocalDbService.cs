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

    /// <summary>
    /// 当前数据作用域：登录后 "u:{encrypt2密文}"（来自 /auth/userinfo），未登录 "local"（未归属，登录时晋升给当前登录者）。
    /// 布局与内容类数据（窗口/组件/便签/待办等）按作用域隔离，防止切换登录账号串数据。
    /// </summary>
    public static string CurrentScope()
    {
        var settings = ((SideDesk.App)System.Windows.Application.Current)?.AppSettings;
        if (!string.IsNullOrEmpty(settings?.AccountUserId)) return "u:" + settings.AccountUserId;
        return "local";
    }

    /// <summary>按作用域隔离的应用级状态 key（左右停靠/后台看板项/变量集）：key 追加 @作用域 后缀</summary>
    private static string ScopedKey(string key, string scope) => key + "@" + scope;

    /// <summary>需要按作用域隔离的应用级状态 key 列表（晋升时随行迁移）</summary>
    private static readonly string[] _scopedStateKeys = { "left_dock", "right_dock", "background_dashboard_items", "variable_sets" };

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
        // 账号作用域隔离：布局与内容类数据加 user_id（'local'=未归属；登录后晋升 u:{密文}）；存量行由 DEFAULT 归入 'local'
        AddColumnIfMissing(conn, "local_bar", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_item", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_item_row", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_sticky_note", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_global_note", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_todo_item", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_clipboard_item", "user_id", "TEXT NOT NULL DEFAULT 'local'");
        AddColumnIfMissing(conn, "local_row_id_map", "user_id", "TEXT NOT NULL DEFAULT 'local'");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS local_bar (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  local_uid     TEXT NOT NULL UNIQUE,
  user_id       TEXT NOT NULL DEFAULT 'local',
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
  user_id     TEXT NOT NULL DEFAULT 'local',
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
  user_id       TEXT NOT NULL DEFAULT 'local',
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
  user_id        TEXT NOT NULL DEFAULT 'local',
  row_server_id  TEXT,
  data_server_id TEXT
);

CREATE TABLE IF NOT EXISTS local_sticky_note (
  uid        TEXT PRIMARY KEY,
  user_id    TEXT NOT NULL DEFAULT 'local',
  content    TEXT,
  created_at TEXT,
  updated_at TEXT
);

CREATE TABLE IF NOT EXISTS local_global_note (
  uid        TEXT PRIMARY KEY,
  user_id    TEXT NOT NULL DEFAULT 'local',
  content    TEXT,
  created_at TEXT,
  updated_at TEXT
);

CREATE TABLE IF NOT EXISTS local_todo_item (
  uid                   TEXT PRIMARY KEY,
  user_id               TEXT NOT NULL DEFAULT 'local',
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
  user_id      TEXT NOT NULL DEFAULT 'local',
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
);

CREATE TABLE IF NOT EXISTS local_key_count_daily (
  date      TEXT PRIMARY KEY,
  key_count INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS local_data_variable (
  user_id    TEXT NOT NULL DEFAULT 'local',
  var_key    TEXT NOT NULL,
  var_value  TEXT,
  corp_id    TEXT,
  updated_at TEXT,
  PRIMARY KEY (user_id, var_key)
);

CREATE TABLE IF NOT EXISTS local_mouse_distance_daily (
  date           TEXT PRIMARY KEY,
  distance_pixel INTEGER NOT NULL DEFAULT 0
);";
        cmd.ExecuteNonQuery();

        // local_data_variable 主键升级：旧表（var_key 单列主键，无 user_id）→ (user_id, var_key)，
        // 防止切换登录账号后手动值串号；存量数据归入机器级 'local' 作用域
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='local_data_variable'";
            var tableExists = Convert.ToInt64(chk.ExecuteScalar()) > 0;
            if (tableExists)
            {
                chk.CommandText = "SELECT COUNT(*) FROM pragma_table_info('local_data_variable') WHERE name='user_id'";
                if (Convert.ToInt64(chk.ExecuteScalar()) == 0)
                {
                    using var mig = conn.CreateCommand();
                    mig.CommandText = @"
ALTER TABLE local_data_variable RENAME TO local_data_variable_old;
CREATE TABLE local_data_variable (
  user_id    TEXT NOT NULL DEFAULT 'local',
  var_key    TEXT NOT NULL,
  var_value  TEXT,
  corp_id    TEXT,
  updated_at TEXT,
  PRIMARY KEY (user_id, var_key)
);
INSERT INTO local_data_variable (user_id, var_key, var_value, corp_id, updated_at)
  SELECT 'local', var_key, var_value, corp_id, updated_at FROM local_data_variable_old;
DROP TABLE local_data_variable_old;";
                    mig.ExecuteNonQuery();
                }
            }
        }
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

    // ==================== 写入 ====================

    /// <summary>将整个管理器状态写入 SQLite（单事务，全量 upsert + 清理已删除行；只影响当前作用域的行）。</summary>
    public static void SaveManager(WindowConfigManager mgr)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTime.Now.ToString("o");
            var scope = CurrentScope();

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
                UpsertBar(conn, win, now, scope);

                int order = 0;
                foreach (var p in win.Panels)
                    SaveItemTree(conn, p, win.Id, null, ref order, now, liveItemUids, scope);
            }

            // 清理内存中已不存在的行（窗口/模块删除；仅当前作用域，不碰其他账号的数据）
            DeleteMissing(conn, "local_bar", "local_uid", liveBarUids, scope);
            DeleteMissing(conn, "local_item", "local_uid", liveItemUids, scope);

            // 行级数据：全量重写（数据量小，语义最简单：删掉的行/孤儿行自动清理；仅当前作用域）
            // 重写前先留存已缓存的服务端行 ID（映射表优先，行表已有值兜底），重插后回填
            var serverIdMap = LoadRowServerIdMap(conn, scope);
            using (var delRows = conn.CreateCommand())
            {
                delRows.CommandText = @"DELETE FROM local_item_row WHERE user_id=$scope;
DELETE FROM local_sticky_note WHERE user_id=$scope;
DELETE FROM local_todo_item WHERE user_id=$scope;
DELETE FROM local_clipboard_item WHERE user_id=$scope;";
                delRows.Parameters.AddWithValue("$scope", scope);
                delRows.ExecuteNonQuery();
            }
            foreach (var win in mgr.Windows)
                foreach (var p in EnumerateAllPanelsLocal(win.Panels))
                    SaveItemRows(conn, p, now, serverIdMap, scope);
            UpsertState(conn, "row_storage_migrated", "1");

            // 应用级状态（按作用域隔离）
            UpsertState(conn, ScopedKey("left_dock", scope), JsonConvert.SerializeObject(mgr.LeftDock));
            UpsertState(conn, ScopedKey("right_dock", scope), JsonConvert.SerializeObject(mgr.RightDock));
            UpsertState(conn, ScopedKey("background_dashboard_items", scope), JsonConvert.SerializeObject(mgr.BackgroundDashboardItems));
            UpsertState(conn, ScopedKey("variable_sets", scope), JsonConvert.SerializeObject(mgr.VariableSets));

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void SaveItemTree(SqliteConnection conn, PanelItem p, string barUid,
        string? parentUid, ref int order, string now, HashSet<string> liveUids, string scope)
    {
        liveUids.Add(p.Id);
        UpsertItem(conn, p, barUid, parentUid, order++, now, scope);
        if (p.ChildPanels != null)
        {
            int childOrder = 0;
            foreach (var c in p.ChildPanels)
                SaveItemTree(conn, c, barUid, p.Id, ref childOrder, now, liveUids, scope);
        }
    }

    private static void UpsertBar(SqliteConnection conn, WindowConfig win, string now, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_bar
  (local_uid, user_id, server_id, title, bg_color, window_type, url, is_edit_mode, is_app_mode,
   show_in_tabs, owner_type, corp_id, share_mode, source_bar_id, enabled_tools, created_at, updated_at)
VALUES
  ($uid, $scope, $sid, $title, $bg, $wtype, $url, $edit, $app, $tabs, $owner, $corp, $share, $src, $tools, $created, $updated)
ON CONFLICT(local_uid) DO UPDATE SET
  user_id=excluded.user_id,
  server_id=excluded.server_id, title=excluded.title, bg_color=excluded.bg_color,
  window_type=excluded.window_type, url=excluded.url, is_edit_mode=excluded.is_edit_mode,
  is_app_mode=excluded.is_app_mode, show_in_tabs=excluded.show_in_tabs, owner_type=excluded.owner_type,
  corp_id=excluded.corp_id, share_mode=excluded.share_mode, source_bar_id=excluded.source_bar_id,
  enabled_tools=excluded.enabled_tools, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", win.Id);
        cmd.Parameters.AddWithValue("$scope", scope);
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

    /// <summary>读取 row_uid → (row_server_id, data_server_id) 留存映射（仅当前作用域）：
    /// local_row_id_map（拉取时回写的权威源）优先，行表已有值兜底。</summary>
    private static Dictionary<string, (string? RowServerId, string? DataServerId)> LoadRowServerIdMap(SqliteConnection conn, string scope)
    {
        var map = new Dictionary<string, (string?, string?)>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT row_uid, row_server_id, data_server_id FROM local_item_row WHERE (row_server_id IS NOT NULL OR data_server_id IS NOT NULL) AND user_id=$scope";
            q.Parameters.AddWithValue("$scope", scope);
            using var rd = q.ExecuteReader();
            while (rd.Read())
                map[rd.GetString(0)] = (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
        }
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT row_uid, row_server_id, data_server_id FROM local_row_id_map WHERE user_id=$scope";
            q.Parameters.AddWithValue("$scope", scope);
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
            var scope = CurrentScope();
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (rowUid, rowServerId, dataServerId) in entries)
            {
                if (string.IsNullOrEmpty(rowUid)) continue;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO local_row_id_map (row_uid, user_id, row_server_id, data_server_id)
VALUES ($uid, $scope, $rsid, $dsid)
ON CONFLICT(row_uid) DO UPDATE SET
  user_id = excluded.user_id,
  row_server_id = COALESCE(excluded.row_server_id, local_row_id_map.row_server_id),
  data_server_id = COALESCE(excluded.data_server_id, local_row_id_map.data_server_id);";
                    cmd.Parameters.AddWithValue("$uid", rowUid);
                    cmd.Parameters.AddWithValue("$scope", scope);
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
            var scope = CurrentScope();
            string inList = string.Join(",", uids.Select((_, i) => $"$u{i}"));
            using var conn = Open();
            void Query(string table)
            {
                using var q = conn.CreateCommand();
                q.CommandText = $"SELECT row_uid, row_server_id, data_server_id FROM {table} WHERE user_id=$scope AND row_uid IN ({inList})";
                q.Parameters.AddWithValue("$scope", scope);
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

    /// <summary>将面板的行列表数据写入 local_item_row + 独立数据表（镜像服务端结构；打当前作用域）。</summary>
    private static void SaveItemRows(SqliteConnection conn, PanelItem p, string now,
        Dictionary<string, (string? RowServerId, string? DataServerId)> serverIdMap, string scope)
    {
        void Insert(string rowUid, string rowType, string content, int order)
        {
            serverIdMap.TryGetValue(rowUid, out var sids);
            InsertRow(conn, p.Id, rowUid, rowType, content, order, now, sids.RowServerId, sids.DataServerId, scope);
        }

        if (p.StickyNotes != null)
        {
            int o = 0;
            foreach (var n in p.StickyNotes)
            {
                Insert(n.Id, "sticky_note", JsonConvert.SerializeObject(n), o++);
                UpsertStickyNote(conn, n, scope);
            }
        }
        if (p.TodoItems != null)
        {
            int o = 0;
            foreach (var t in p.TodoItems)
            {
                Insert(t.Id, "todo", JsonConvert.SerializeObject(t), o++);
                UpsertTodoItem(conn, t, scope);
            }
        }
        if (p.ClipboardItems != null)
        {
            int o = 0;
            foreach (var c in p.ClipboardItems)
            {
                Insert(c.Id, "clipboard", JsonConvert.SerializeObject(c), o++);
                UpsertClipboardItem(conn, c, scope);
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
        string rowType, string content, int order, string now, string? rowServerId, string? dataServerId, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_item_row
  (item_uid, row_uid, user_id, row_type, content, sort_order, row_server_id, data_server_id, created_at, updated_at)
VALUES ($item, $row, $scope, $type, $content, $order, $rsid, $dsid, $created, $updated)";
        cmd.Parameters.AddWithValue("$item", itemUid);
        cmd.Parameters.AddWithValue("$row", rowUid);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$type", rowType);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$order", order);
        cmd.Parameters.AddWithValue("$rsid", (object?)rowServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dsid", (object?)dataServerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStickyNote(SqliteConnection conn, StickyNote n, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_sticky_note (uid, user_id, content, created_at, updated_at)
VALUES ($uid, $scope, $content, $created, $updated)
ON CONFLICT(uid) DO UPDATE SET user_id=excluded.user_id, content=excluded.content, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", n.Id);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$content", n.Content);
        cmd.Parameters.AddWithValue("$created", n.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", n.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 全局便签库（「我的数据」便签列表 + AI 工具）。
    /// 独立于面板行数据表，不参与 SaveManager 的全量重写；原存储为 sticky-notes.json。
    /// </summary>
    public static List<StickyNote> LoadGlobalNotes()
    {
        var list = new List<StickyNote>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT uid, content, created_at, updated_at FROM local_global_note WHERE user_id=$scope";
        cmd.Parameters.AddWithValue("$scope", CurrentScope());
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var n = new StickyNote { Id = rd.GetString(0), Content = rd.IsDBNull(1) ? "" : rd.GetString(1) };
            if (!rd.IsDBNull(2) && DateTime.TryParse(rd.GetString(2), out var c)) n.CreatedAt = c;
            if (!rd.IsDBNull(3) && DateTime.TryParse(rd.GetString(3), out var u)) n.UpdatedAt = u;
            list.Add(n);
        }
        return list;
    }

    public static void SaveGlobalNotes(IReadOnlyList<StickyNote> notes)
    {
        var scope = CurrentScope();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM local_global_note WHERE user_id=$scope";
            del.Parameters.AddWithValue("$scope", scope);
            del.ExecuteNonQuery();
        }
        foreach (var n in notes)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO local_global_note (uid, user_id, content, created_at, updated_at)
VALUES ($uid, $scope, $content, $created, $updated);";
            cmd.Parameters.AddWithValue("$uid", n.Id);
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.Parameters.AddWithValue("$content", n.Content);
            cmd.Parameters.AddWithValue("$created", n.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$updated", n.UpdatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 作用域晋升：把 from 作用域下的布局与内容数据归入 to 作用域（未归属 local 数据“跟人走”）。
    /// 内容表直接 UPDATE；作用域化 state keys 源值覆盖目标（离线数据时间上更新）后删除源 key。
    /// </summary>
    public static void AdoptScope(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        foreach (var table in new[] { "local_bar", "local_item", "local_item_row", "local_row_id_map",
            "local_sticky_note", "local_global_note", "local_todo_item", "local_clipboard_item" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET user_id=$to WHERE user_id=$from";
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.ExecuteNonQuery();
        }

        // 作用域化 state keys 迁移：早期构建无后缀的 key 视为 'local' 作用域
        foreach (var key in _scopedStateKeys)
        {
            var src = GetState(conn, ScopedKey(key, from)) ?? (from == "local" ? GetState(conn, key) : null);
            if (src == null) continue;
            UpsertState(conn, ScopedKey(key, to), src);
            DeleteState(conn, ScopedKey(key, from));
            if (from == "local") DeleteState(conn, key);
        }
        tx.Commit();
    }

    private static void UpsertTodoItem(SqliteConnection conn, TodoItem t, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_todo_item
  (uid, user_id, text, description, is_done, due_time, remind_before_minutes, reminded, created_at)
VALUES ($uid, $scope, $text, $desc, $done, $due, $remind, $reminded, $created)
ON CONFLICT(uid) DO UPDATE SET
  user_id=excluded.user_id,
  text=excluded.text, description=excluded.description, is_done=excluded.is_done,
  due_time=excluded.due_time, remind_before_minutes=excluded.remind_before_minutes,
  reminded=excluded.reminded;";
        cmd.Parameters.AddWithValue("$uid", t.Id);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$text", t.Text);
        cmd.Parameters.AddWithValue("$desc", t.Description);
        cmd.Parameters.AddWithValue("$done", t.IsDone ? 1 : 0);
        cmd.Parameters.AddWithValue("$due", (object?)t.DueTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remind", t.RemindBeforeMinutes);
        cmd.Parameters.AddWithValue("$reminded", t.Reminded ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", t.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertClipboardItem(SqliteConnection conn, ClipboardSavedItem c, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_clipboard_item (uid, user_id, content_type, content, saved_at)
VALUES ($uid, $scope, $type, $content, $saved)
ON CONFLICT(uid) DO UPDATE SET user_id=excluded.user_id, content_type=excluded.content_type, content=excluded.content,
  saved_at=excluded.saved_at;";
        cmd.Parameters.AddWithValue("$uid", c.Id);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$type", c.ContentType);
        cmd.Parameters.AddWithValue("$content", c.Content);
        cmd.Parameters.AddWithValue("$saved", c.SavedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertItem(SqliteConnection conn, PanelItem p, string barUid,
        string? parentUid, int order, string now, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO local_item
  (local_uid, user_id, server_id, bar_uid, parent_uid, sort_order, title, item_type, group_id,
   item_data, owner_type, team_id, team_name, corp_id, created_at, updated_at)
VALUES
  ($uid, $scope, $sid, $bar, $parent, $order, $title, $type, $group, $data, $owner, $team, $teamname, $corp, $created, $updated)
ON CONFLICT(local_uid) DO UPDATE SET
  user_id=excluded.user_id,
  server_id=excluded.server_id, bar_uid=excluded.bar_uid, parent_uid=excluded.parent_uid,
  sort_order=excluded.sort_order, title=excluded.title, item_type=excluded.item_type,
  group_id=excluded.group_id, item_data=excluded.item_data, owner_type=excluded.owner_type,
  team_id=excluded.team_id, team_name=excluded.team_name, corp_id=excluded.corp_id, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$uid", p.Id);
        cmd.Parameters.AddWithValue("$scope", scope);
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

    /// <summary>删除指定应用级状态 key（在已有连接上执行，供作用域晋升等事务内使用）</summary>
    private static void DeleteState(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_app_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除表中 uid 不在内存集合里的行（仅当前作用域；按 IN 分批，避免参数上限）。</summary>
    private static void DeleteMissing(SqliteConnection conn, string table, string column, HashSet<string> liveUids, string scope)
    {
        // 先取出库中当前作用域的全部 uid，内存求差集，再按差集删除（数据量小，简单可靠）
        var dbUids = new List<string>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT {column} FROM {table} WHERE user_id=$scope";
            q.Parameters.AddWithValue("$scope", scope);
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
        cmd.CommandText = $"DELETE FROM {table} WHERE user_id=$scope AND {column} IN ({string.Join(",", names)})";
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.ExecuteNonQuery();
    }

    // ==================== 读取 ====================

    /// <summary>从 SQLite 加载完整管理器状态（仅当前作用域的数据）。</summary>
    public static WindowConfigManager LoadManager()
    {
        var mgr = new WindowConfigManager();
        var scope = CurrentScope();
        using var conn = Open();

        // 1) bars
        var bars = new List<(WindowConfig Win, DateTime Created)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT local_uid, server_id, title, bg_color, window_type, url,
  is_edit_mode, is_app_mode, show_in_tabs, owner_type, corp_id, share_mode, source_bar_id,
  enabled_tools, created_at
FROM local_bar WHERE deleted_at IS NULL AND user_id=$scope ORDER BY id";
            cmd.Parameters.AddWithValue("$scope", scope);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var win = new WindowConfig
                {
                    Id = rd.GetString(0),
                    ServerId = rd.IsDBNull(1) ? null : rd.GetString(1),
                    Name = rd.IsDBNull(2) ? Framework.Lang.T("common.newWindow") : rd.GetString(2),
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
FROM local_item WHERE deleted_at IS NULL AND user_id=$scope ORDER BY sort_order, id";
            cmd2.Parameters.AddWithValue("$scope", scope);
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
            AttachRowsToPanels(conn, items.Values, scope);

        // 3) 应用级状态（按作用域隔离；无后缀旧 key 作为未作用域化存量回退）
        string? left = GetState(conn, ScopedKey("left_dock", scope)) ?? GetState(conn, "left_dock");
        string? right = GetState(conn, ScopedKey("right_dock", scope)) ?? GetState(conn, "right_dock");
        string? bgDash = GetState(conn, ScopedKey("background_dashboard_items", scope)) ?? GetState(conn, "background_dashboard_items");
        string? varSets = GetState(conn, ScopedKey("variable_sets", scope)) ?? GetState(conn, "variable_sets");
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

    /// <summary>从 local_item_row 按 row_type 还原各面板的行列表（仅当前作用域）。</summary>
    private static void AttachRowsToPanels(SqliteConnection conn, IEnumerable<PanelItem> panels, string scope)
    {
        var rowsByItem = new Dictionary<string, List<(string Type, string? Content)>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT item_uid, row_type, content FROM local_item_row WHERE user_id=$scope ORDER BY sort_order, id";
            cmd.Parameters.AddWithValue("$scope", scope);
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

    // ==================== 通用业务数据 KV（原独立 JSON 文件迁入 SQLite）====================

    /// <summary>读取业务数据 JSON（原独立 JSON 文件的 SQLite 存储），无值返回 null。</summary>
    public static string? LoadBusinessData(string key)
    {
        using var conn = Open();
        return GetState(conn, key);
    }

    /// <summary>单行 upsert 写入业务数据 JSON（事务原子，替代整文件写盘）。</summary>
    public static void SaveBusinessData(string key, string json)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertState(conn, key, json);
        tx.Commit();
    }

    /// <summary>删除业务数据 KV（旧存储迁移完成后的清理用）</summary>
    public static void DeleteBusinessData(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_app_state WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    // ==================== 数据变量手动值（独立表 local_data_variable，旧 KV blob 自动迁移）====================

    /// <summary>读取指定作用域（user_id）下的全部本地数据变量（手动值层）</summary>
    public static List<(string VarKey, string VarValue, string? CorpId)> LoadDataVariables(string scope)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT var_key, var_value, corp_id FROM local_data_variable WHERE user_id=$s";
        cmd.Parameters.AddWithValue("$s", scope);
        var list = new List<(string, string, string?)>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var k = rd.GetString(0);
            var v = rd.IsDBNull(1) ? "" : rd.GetString(1);
            var c = rd.IsDBNull(2) ? null : rd.GetString(2);
            list.Add((k, v, c));
        }
        return list;
    }

    /// <summary>单变量 upsert（按 作用域+变量名 定位）；corpId 传 null 时保留行上已有值（WS 推送不带作用域信息）</summary>
    public static void UpsertDataVariable(string scope, string varKey, string? varValue, string? corpId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_data_variable (user_id, var_key, var_value, corp_id, updated_at)
VALUES ($s, $k, $v, $c, $u)
ON CONFLICT(user_id, var_key) DO UPDATE SET
  var_value  = excluded.var_value,
  corp_id    = COALESCE(excluded.corp_id, local_data_variable.corp_id),
  updated_at = excluded.updated_at";
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$k", varKey);
        cmd.Parameters.AddWithValue("$v", (object?)varValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", (object?)corpId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除指定作用域下的数据变量，返回是否存在过记录</summary>
    public static bool DeleteDataVariable(string scope, string varKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_data_variable WHERE user_id=$s AND var_key=$k";
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$k", varKey);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ==================== 键鼠每日统计（原 keycount.json / mousedistance.json）====================

    public static int? LoadKeyCount(string date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key_count FROM local_key_count_daily WHERE date=$d";
        cmd.Parameters.AddWithValue("$d", date);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? (int?)null : Convert.ToInt32(v);
    }

    public static void SaveKeyCount(string date, int count)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_key_count_daily (date, key_count) VALUES ($d, $v)
ON CONFLICT(date) DO UPDATE SET key_count=excluded.key_count;";
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$v", count);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public static long? LoadMouseDistance(string date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT distance_pixel FROM local_mouse_distance_daily WHERE date=$d";
        cmd.Parameters.AddWithValue("$d", date);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? (long?)null : Convert.ToInt64(v);
    }

    public static void SaveMouseDistance(string date, long pixels)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO local_mouse_distance_daily (date, distance_pixel) VALUES ($d, $v)
ON CONFLICT(date) DO UPDATE SET distance_pixel=excluded.distance_pixel;";
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$v", pixels);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>一次性导入旧 keycount.json 全量历史（仅导入表中尚无的日期）。</summary>
    public static void ImportKeyCounts(IReadOnlyDictionary<string, int> daily)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var kv in daily)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO local_key_count_daily (date, key_count) VALUES ($d, $v)
ON CONFLICT(date) DO NOTHING;";
            cmd.Parameters.AddWithValue("$d", kv.Key);
            cmd.Parameters.AddWithValue("$v", kv.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>一次性导入旧 mousedistance.json 全量历史（仅导入表中尚无的日期）。</summary>
    public static void ImportMouseDistances(IReadOnlyDictionary<string, long> daily)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var kv in daily)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO local_mouse_distance_daily (date, distance_pixel) VALUES ($d, $v)
ON CONFLICT(date) DO NOTHING;";
            cmd.Parameters.AddWithValue("$d", kv.Key);
            cmd.Parameters.AddWithValue("$v", kv.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
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
}
