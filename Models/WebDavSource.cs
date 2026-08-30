using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    /// <summary>WebDAV 存储源：用户配置的标准 WebDAV 服务器，可作为元桌主存储使用</summary>
    public class WebDavSource
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>显示名称</summary>
        public string Name { get; set; } = "";
        /// <summary>WebDAV 服务器地址（如 https://dav.example.com/user/）</summary>
        public string Url { get; set; } = "";
        /// <summary>用户名（匿名服务器可留空）</summary>
        public string Username { get; set; } = "";
        /// <summary>密码（DPAPI 加密后的 base64 密文）</summary>
        public string PasswordEncrypted { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>解密后的密码（解密失败返回空串）</summary>
        [JsonIgnore]
        public string Password => WebDavSourceManager.Decrypt(PasswordEncrypted);

        /// <summary>规范化根地址：保证以 / 结尾</summary>
        [JsonIgnore]
        public string BaseUrl
        {
            get
            {
                string u = (Url ?? "").Trim();
                if (u.Length == 0) return "";
                return u.EndsWith('/') ? u : u + "/";
            }
        }
    }

    /// <summary>WebDAV 存储源管理器：主存储 SQLite（local_app_state KV），密码字段以 DPAPI 加密落库</summary>
    public static class WebDavSourceManager
    {
        private const string DbKey = "webdav_sources";
        private static readonly string _filePath;
        private static List<WebDavSource> _sources;
        private static readonly object _lock = new object();

        static WebDavSourceManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "webdav-sources.json");
            Load();
        }

        private static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (Services.LocalDbService.DbExists)
                    {
                        string? dbJson = Services.LocalDbService.LoadBusinessData(DbKey);
                        if (!string.IsNullOrEmpty(dbJson))
                        {
                            _sources = JsonConvert.DeserializeObject<List<WebDavSource>>(dbJson) ?? new List<WebDavSource>();
                            return;
                        }
                    }
                    if (File.Exists(_filePath))
                    {
                        string fileJson = File.ReadAllText(_filePath, Encoding.UTF8);
                        _sources = JsonConvert.DeserializeObject<List<WebDavSource>>(fileJson) ?? new List<WebDavSource>();
                        if (_sources.Count > 0 && Services.LocalDbService.DbExists)
                            Services.LocalDbService.SaveBusinessData(DbKey, JsonConvert.SerializeObject(_sources));
                        return;
                    }
                }
                catch { }
                _sources = new List<WebDavSource>();
            }
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
                string json = JsonConvert.SerializeObject(_sources, Formatting.Indented);
                if (Services.LocalDbService.DbExists)
                {
                    try { Services.LocalDbService.SaveBusinessData(DbKey, json); return; }
                    catch { }
                }
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
            catch { }
        }

        public static List<WebDavSource> GetAll()
        {
            lock (_lock)
                return new List<WebDavSource>(_sources);
        }

        public static WebDavSource? Find(string id)
        {
            lock (_lock)
                return _sources.Find(s => s.Id == id);
        }

        public static void Add(WebDavSource source)
        {
            lock (_lock)
            {
                _sources.Add(source);
                SaveNoLock();
            }
        }

        public static void Update(WebDavSource source)
        {
            lock (_lock)
            {
                int idx = _sources.FindIndex(s => s.Id == source.Id);
                if (idx >= 0)
                    _sources[idx] = source;
                SaveNoLock();
            }
        }

        public static void Delete(string id)
        {
            lock (_lock)
            {
                _sources.RemoveAll(s => s.Id == id);
                SaveNoLock();
            }
        }

        // ===== DPAPI 加解密（仅当前 Windows 用户可解密） =====

        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] protected_ = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protected_);
            }
            catch { return ""; }
        }

        public static string Decrypt(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return "";
            try
            {
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return ""; }
        }
    }
}
