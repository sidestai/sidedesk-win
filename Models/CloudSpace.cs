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
        /// <summary>是否为系统内置预设</summary>
        public bool IsBuiltin { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>云空间管理器：持久化到 cloud-spaces.json，首次运行自动填充内置预设</summary>
    public static class CloudSpaceManager
    {
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

        /// <summary>系统内置云空间预设（仅元桌云盘一项，不可删除/编辑）</summary>
        private static List<CloudSpace> GetBuiltinSpaces() => new()
        {
            new CloudSpace { Name = "元桌云盘", Url = "内置", Icon = "☁️", Note = "元桌内置云存储服务", IsBuiltin = true },
        };

        private static void Load()
        {
            lock (_lock)
            {
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

        /// <summary>迁移旧版内置预设：移除已废弃的内置网盘项，确保元桌云盘存在</summary>
        private static void MigrateBuiltins()
        {
            bool changed = false;
            // 移除旧版内置网盘预设（百度网盘等已不再内置）
            int removed = _spaces.RemoveAll(s => s.IsBuiltin && s.Name != "元桌云盘");
            if (removed > 0) changed = true;
            // 确保元桌云盘始终存在
            if (!_spaces.Exists(s => s.IsBuiltin && s.Name == "元桌云盘"))
            {
                _spaces.Insert(0, GetBuiltinSpaces()[0]);
                changed = true;
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
                File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static List<CloudSpace> GetAll()
        {
            lock (_lock)
                return new List<CloudSpace>(_spaces);
        }

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

        /// <summary>恢复内置预设（将缺失的内置项补回）</summary>
        public static void RestoreBuiltins()
        {
            lock (_lock)
            {
                foreach (var builtin in GetBuiltinSpaces())
                {
                    if (!_spaces.Exists(s => s.Name == builtin.Name))
                        _spaces.Add(builtin);
                }
                SaveNoLock();
            }
        }
    }
}
