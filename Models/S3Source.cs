using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    /// <summary>S3 存储源：用户配置的 Amazon S3（或 S3 兼容协议）桶，可作为元桌主存储使用</summary>
    public class S3Source
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>显示名称</summary>
        public string Name { get; set; } = "";
        /// <summary>服务端点（空 = AWS 官方端点；MinIO/R2 等 S3 兼容服务填自定义地址）</summary>
        public string Endpoint { get; set; } = "";
        /// <summary>区域（AWS 默认 us-east-1）</summary>
        public string Region { get; set; } = "us-east-1";
        /// <summary>桶名</summary>
        public string Bucket { get; set; } = "";
        public string AccessKeyId { get; set; } = "";
        /// <summary>SecretAccessKey（DPAPI 加密后的 base64 密文）</summary>
        public string SecretKeyEncrypted { get; set; } = "";
        /// <summary>可选 STS 会话令牌（DPAPI 加密后的 base64 密文）</summary>
        public string? SessionTokenEncrypted { get; set; }
        /// <summary>路径风格寻址（部分自定义端点需要）</summary>
        public bool UsePathStyle { get; set; }
        /// <summary>存储根前缀（可选，相当于虚拟根目录，不以 / 开头、以 / 结尾）</summary>
        public string Prefix { get; set; } = "";
        /// <summary>归属团队 ID（创建时当前选中的 corpId；空 = 个人）</summary>
        public string? CorpId { get; set; }
        /// <summary>归属团队名称（仅展示用，随创建时快照）</summary>
        public string? CorpName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>解密后的 SecretAccessKey（解密失败返回空串）</summary>
        [JsonIgnore]
        public string SecretAccessKey => S3SourceManager.Decrypt(SecretKeyEncrypted);

        /// <summary>解密后的会话令牌（无则 null）</summary>
        [JsonIgnore]
        public string? SessionToken
        {
            get
            {
                if (string.IsNullOrEmpty(SessionTokenEncrypted)) return null;
                string v = S3SourceManager.Decrypt(SessionTokenEncrypted);
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }

        /// <summary>规范化前缀：去首斜杠，非空时保证以 / 结尾</summary>
        [JsonIgnore]
        public string NormalizedPrefix
        {
            get
            {
                string p = (Prefix ?? "").Trim().TrimStart('/');
                if (p.Length == 0) return "";
                return p.EndsWith('/') ? p : p + "/";
            }
        }
    }

    /// <summary>S3 存储源管理器：主存储 SQLite（local_app_state KV），密钥字段以 DPAPI 加密落库</summary>
    public static class S3SourceManager
    {
        private const string DbKey = "s3_sources";
        private static readonly string _filePath;
        private static List<S3Source> _sources;
        private static readonly object _lock = new object();

        static S3SourceManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "s3-sources.json");
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
                            _sources = JsonConvert.DeserializeObject<List<S3Source>>(dbJson) ?? new List<S3Source>();
                            return;
                        }
                    }
                    if (File.Exists(_filePath))
                    {
                        string fileJson = File.ReadAllText(_filePath, Encoding.UTF8);
                        _sources = JsonConvert.DeserializeObject<List<S3Source>>(fileJson) ?? new List<S3Source>();
                        if (_sources.Count > 0 && Services.LocalDbService.DbExists)
                            Services.LocalDbService.SaveBusinessData(DbKey, JsonConvert.SerializeObject(_sources));
                        return;
                    }
                }
                catch { }
                _sources = new List<S3Source>();
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

        public static List<S3Source> GetAll()
        {
            lock (_lock)
                return new List<S3Source>(_sources);
        }

        public static S3Source? Find(string id)
        {
            lock (_lock)
                return _sources.Find(s => s.Id == id);
        }

        public static void Add(S3Source source)
        {
            lock (_lock)
            {
                _sources.Add(source);
                SaveNoLock();
            }
        }

        public static void Update(S3Source source)
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
                byte[] protected_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protected_);
            }
            catch { return ""; }
        }

        public static string Decrypt(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return "";
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return ""; }
        }
    }
}
