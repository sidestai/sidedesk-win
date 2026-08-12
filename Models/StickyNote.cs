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
                    return Framework.Lang.T("(空)");
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
        private static readonly object _lock = new object();

        static StickyNoteManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "sticky-notes.json");
            Load();
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
                        _notes = JsonConvert.DeserializeObject<List<StickyNote>>(json) ?? new List<StickyNote>();
                    }
                    catch
                    {
                        _notes = new List<StickyNote>();
                    }
                }
                else
                {
                    _notes = new List<StickyNote>();
                }
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
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
                return _notes.OrderByDescending(n => n.UpdatedAt).ToList();
        }

        public static void Add(StickyNote note)
        {
            lock (_lock)
                _notes.Add(note);
            Save();
        }

        public static void Update(StickyNote note)
        {
            lock (_lock)
            {
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
                _notes.RemoveAll(n => n.Id == id);
            Save();
        }

        public static StickyNote GetById(string id)
        {
            lock (_lock)
                return _notes.FirstOrDefault(n => n.Id == id);
        }
    }
}
