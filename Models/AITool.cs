using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class AITool
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public string Icon { get; set; }
        public string BackgroundColor { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore]
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

        public AITool()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.Now;
            BackgroundColor = "#7C3AED";
        }
    }

    public static class AIToolManager
    {
        private static readonly string _filePath;
        private static List<AITool> _tools;
        private static readonly object _lock = new object();

        static AIToolManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "aitools.json");
            Load();
        }

        public static List<AITool> GetAll()
        {
            lock (_lock)
            {
                return new List<AITool>(_tools);
            }
        }

        public static void Add(AITool tool)
        {
            lock (_lock)
            {
                _tools.Add(tool);
                Save();
            }
        }

        public static void Remove(string id)
        {
            lock (_lock)
            {
                _tools.RemoveAll(t => t.Id == id);
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
                    _tools = JsonConvert.DeserializeObject<List<AITool>>(json) ?? new List<AITool>();
                }
                else
                {
                    _tools = new List<AITool>();
                }
            }
            catch
            {
                _tools = new List<AITool>();
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_tools, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
