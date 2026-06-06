using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class Bookmark
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public bool UseAppMode { get; set; }
        public string BackgroundColor { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore]
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

        public Bookmark()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            BackgroundColor = "#3498DB";
        }
    }

    public static class BookmarkManager
    {
        private static readonly string _filePath;
        private static List<Bookmark> _bookmarks;
        private static readonly object _lock = new object();

        static BookmarkManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "bookmarks.json");
            Load();
        }

        public static List<Bookmark> GetAll()
        {
            lock (_lock)
            {
                return new List<Bookmark>(_bookmarks);
            }
        }

        public static void Add(Bookmark bookmark)
        {
            lock (_lock)
            {
                _bookmarks.Add(bookmark);
                Save();
            }
        }

        public static void Remove(string id)
        {
            lock (_lock)
            {
                _bookmarks.RemoveAll(b => b.Id == id);
                Save();
            }
        }

        public static void Update(Bookmark bookmark)
        {
            lock (_lock)
            {
                int index = _bookmarks.FindIndex(b => b.Id == bookmark.Id);
                if (index >= 0)
                {
                    _bookmarks[index] = bookmark;
                    Save();
                }
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _bookmarks = JsonConvert.DeserializeObject<List<Bookmark>>(json) ?? new List<Bookmark>();
                }
                else
                {
                    _bookmarks = new List<Bookmark>();
                }
            }
            catch
            {
                _bookmarks = new List<Bookmark>();
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_bookmarks, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
