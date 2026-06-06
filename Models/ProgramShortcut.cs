using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class ProgramShortcut
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public string Arguments { get; set; }
        public string BackgroundColor { get; set; }
        public DateTime CreatedAt { get; set; }

        public ProgramShortcut()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            BackgroundColor = "#9B59B6";
        }
    }

    public static class ProgramShortcutManager
    {
        private static readonly string _filePath;
        private static List<ProgramShortcut> _shortcuts;
        private static readonly object _lock = new object();

        static ProgramShortcutManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "programs.json");
            Load();
        }

        public static List<ProgramShortcut> GetAll()
        {
            lock (_lock)
            {
                return new List<ProgramShortcut>(_shortcuts);
            }
        }

        public static void Add(ProgramShortcut shortcut)
        {
            lock (_lock)
            {
                _shortcuts.Add(shortcut);
                Save();
            }
        }

        public static void Remove(string id)
        {
            lock (_lock)
            {
                _shortcuts.RemoveAll(s => s.Id == id);
                Save();
            }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _shortcuts = JsonConvert.DeserializeObject<List<ProgramShortcut>>(json) ?? new List<ProgramShortcut>();
                }
                else
                {
                    _shortcuts = new List<ProgramShortcut>();
                }
            }
            catch
            {
                _shortcuts = new List<ProgramShortcut>();
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_shortcuts, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
