using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    /// <summary>云空间条目：用户配置的网盘/云存储快捷入口</summary>
    public class CloudSpace
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>显示名称</summary>
        public string Name { get; set; } = "";
        /// <summary>访问地址</summary>
        public string Url { get; set; } = "";
        /// <summary>图标（emoji 或图标路径）</summary>
        public string Icon { get; set; } = "☁️";
        /// <summary>备注说明</summary>
        public string? Note { get; set; }
        /// <summary>关联的存储源 ID（如 S3Source.Id）；非空时该项为可直连存储源，打开方式由源类型决定</summary>
        public string? RefId { get; set; }
        /// <summary>关联存储源类型（"s3" / "webdav"）；RefId 非空且本字段为空时按 s3 兼容旧数据</summary>
        public string? RefType { get; set; }
        /// <summary>是否为系统内置预设</summary>
        public bool IsBuiltin { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>云空间管理器：主存储 SQLite（local_app_state KV，原 cloud-spaces.json），首次运行自动填充内置预设</summary>
    public static class CloudSpaceManager
    {
        private const string DbKey = "cloud_spaces";
        private static readonly string _filePath;
        private static List<CloudSpace> _spaces;
        private static readonly object _lock = new object();

        static CloudSpaceManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "cloud-spaces.json");
            Load();
        }

        /// <summary>系统内置云空间预设（元桌云盘，不可删除/编辑）</summary>
        private static List<CloudSpace> GetBuiltinSpaces() => new()
        {
            new CloudSpace { Name = "元桌云盘", Url = "内置", Icon = "☁️", Note = "元桌内置云存储服务", IsBuiltin = true },
        };

        private static void Load()
        {
            lock (_lock)
            {
                // 主存储：SQLite；无值时从旧 cloud-spaces.json 一次性迁入
                if (Services.LocalDbService.DbExists)
                {
                    try
                    {
                        string? dbJson = Services.LocalDbService.LoadBusinessData(DbKey);
                        if (!string.IsNullOrEmpty(dbJson))
                        {
                            _spaces = JsonConvert.DeserializeObject<List<CloudSpace>>(dbJson) ?? new List<CloudSpace>();
                            MigrateBuiltins();
                            return;
                        }
                        if (File.Exists(_filePath))
                        {
                            string fileJson = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
                            _spaces = JsonConvert.DeserializeObject<List<CloudSpace>>(fileJson) ?? new List<CloudSpace>();
                            if (_spaces.Count > 0)
                                Services.LocalDbService.SaveBusinessData(DbKey, JsonConvert.SerializeObject(_spaces));
                            MigrateBuiltins();
                            return;
                        }
                        // 首次运行：填充内置预设
                        _spaces = GetBuiltinSpaces();
                        SaveNoLock();
                        return;
                    }
                    catch { }
                }

                if (File.Exists(_filePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
                        _spaces = JsonConvert.DeserializeObject<List<CloudSpace>>(json) ?? new List<CloudSpace>();
                    }
                    catch
                    {
                        _spaces = new List<CloudSpace>();
                    }
                    MigrateBuiltins();
                }
                else
                {
                    // 首次运行：填充内置预设
                    _spaces = GetBuiltinSpaces();
                    SaveNoLock();
                }
            }
        }

        /// <summary>迁移旧版内置预设：确保所有内置项存在，并移除不再是内置的旧条目</summary>
        private static void MigrateBuiltins()
        {
            bool changed = false;
            var builtins = GetBuiltinSpaces();
            var builtinNames = new HashSet<string>(builtins.ConvertAll(b => b.Name));

            // 移除已不再是内置的旧条目（如旧版“百度网盘”内置项）
            int removed = _spaces.RemoveAll(s => s.IsBuiltin && !builtinNames.Contains(s.Name));
            if (removed > 0) changed = true;

            // 确保所有内置云空间始终存在
            foreach (var builtin in builtins)
            {
                if (!_spaces.Exists(s => s.IsBuiltin && s.Name == builtin.Name))
                {
                    _spaces.Insert(_spaces.FindIndex(s => s.IsBuiltin) < 0 ? 0 : _spaces.FindLastIndex(s => s.IsBuiltin) + 1, builtin);
                    changed = true;
                }
            }
            if (changed) SaveNoLock();
        }

        public static void Save()
        {
            lock (_lock)
                SaveNoLock();
        }

        private static void SaveNoLock()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_spaces, Formatting.Indented);
                if (Services.LocalDbService.DbExists)
                {
                    try { Services.LocalDbService.SaveBusinessData(DbKey, json); return; }
                    catch { }
                }
                // db 不可用/未建立：退回旧 JSON 存储
                File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static List<CloudSpace> GetAll()
        {
            lock (_lock)
                return new List<CloudSpace>(_spaces);
        }

        /// <summary>云空间显示名称（内置项按当前语言翻译，用户项用原名；存储的 Name 保持不变仅作标识）</summary>
        public static string GetDisplayName(CloudSpace space)
            => space.IsBuiltin ? Framework.Lang.T("cloudDrive.metadeskDrive") : space.Name;

        /// <summary>云空间描述行（内置项显示翻译后的说明，用户项显示地址）</summary>
        public static string GetDisplayUrl(CloudSpace space)
            => space.IsBuiltin ? Framework.Lang.T("cloudDrive.builtinNote") : space.Url;

        public static void Add(CloudSpace space)
        {
            lock (_lock)
            {
                _spaces.Add(space);
                SaveNoLock();
            }
        }

        public static void Update(CloudSpace space)
        {
            lock (_lock)
            {
                var existing = _spaces.FindIndex(s => s.Id == space.Id);
                if (existing >= 0)
                    _spaces[existing] = space;
                SaveNoLock();
            }
        }

        public static void Delete(string id)
        {
            lock (_lock)
            {
                // 内置云空间不可删除
                _spaces.RemoveAll(s => s.Id == id && !s.IsBuiltin);
                SaveNoLock();
            }
        }
    }
}
