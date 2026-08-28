using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class StickyNote
    {
        public string Id { get; set; }
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public StickyNote()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
        }

        /// <summary>
        /// 获取预览文字：取第一行，最多30字，无内容时返回"(空)"
        /// </summary>
        [JsonIgnore]
        public string Preview
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Content))
                    return Framework.Lang.T("sticky.empty");
                string firstLine = Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                return firstLine.Length > 30 ? firstLine.Substring(0, 30) + "…" : firstLine;
            }
        }
    }

    public static class StickyNoteManager
    {
        private static readonly string _filePath;
        private static List<StickyNote> _notes;
        private static bool _loaded;
        private static readonly object _lock = new object();

        static StickyNoteManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "sticky-notes.json");
            _notes = new List<StickyNote>();
        }

        /// <summary>
        /// 惰性加载：主存储为 SQLite（local_global_note 表），首次访问时从旧 sticky-notes.json 一次性迁入；
        /// db 尚未建立（首启未完成 windows.json 导入）时暂用 JSON，db 就绪后的下次保存自动迁入。
        /// </summary>
        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                if (Services.LocalDbService.DbExists)
                {
                    try
                    {
                        var dbNotes = Services.LocalDbService.LoadGlobalNotes();
                        if (dbNotes.Count > 0)
                        {
                            _notes = dbNotes;
                            return;
                        }
                        // 表为空：从旧 JSON 一次性迁入（迁入后 JSON 仅作备份，不再读写）
                        var fileNotes = LoadFromFile();
                        if (fileNotes.Count > 0)
                            Services.LocalDbService.SaveGlobalNotes(fileNotes);
                        _notes = fileNotes;
                        return;
                    }
                    catch { }
                }
                _notes = LoadFromFile();
            }
        }

        private static List<StickyNote> LoadFromFile()
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    string json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
                    return JsonConvert.DeserializeObject<List<StickyNote>>(json) ?? new List<StickyNote>();
                }
                catch { }
            }
            return new List<StickyNote>();
        }

        public static void Save()
        {
            lock (_lock)
            {
                if (Services.LocalDbService.DbExists)
                {
                    try { Services.LocalDbService.SaveGlobalNotes(_notes); return; }
                    catch { }
                }
                // db 不可用/未建立：退回旧 JSON 存储
                try
                {
                    string json = JsonConvert.SerializeObject(_notes, Formatting.Indented);
                    File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

        public static List<StickyNote> GetAll()
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _notes.OrderByDescending(n => n.UpdatedAt).ToList();
            }
        }

        public static void Add(StickyNote note)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _notes.Add(note);
            }
            Save();
        }

        public static void Update(StickyNote note)
        {
            lock (_lock)
            {
                EnsureLoaded();
                var existing = _notes.FirstOrDefault(n => n.Id == note.Id);
                if (existing != null)
                {
                    existing.Content = note.Content;
                    existing.UpdatedAt = DateTime.Now;
                }
            }
            Save();
        }

        public static void Delete(string id)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _notes.RemoveAll(n => n.Id == id);
            }
            Save();
        }

        public static StickyNote GetById(string id)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _notes.FirstOrDefault(n => n.Id == id);
            }
        }
    }
}
