using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    public class AIModelInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ApiBaseUrl { get; set; }
        public string DefaultModelId { get; set; }
    }

    public class AIConfig
    {
        public string SelectedModelId { get; set; }
        public string ApiKey { get; set; }
        public string CustomModelId { get; set; }
    }

    public static class AIConfigManager
    {
        private const string DbKey = "ai_config";
        private static readonly string _filePath;
        private static AIConfig _config;
        private static readonly object _lock = new object();

        public static readonly List<AIModelInfo> SupportedModels = new List<AIModelInfo>
        {
            new AIModelInfo
            {
                Id = "deepseek",
                Name = "DeepSeek",
                ApiBaseUrl = "https://api.deepseek.com",
                DefaultModelId = "deepseek-chat"
            },
            new AIModelInfo
            {
                Id = "openai",
                Name = "OpenAI",
                ApiBaseUrl = "https://api.openai.com",
                DefaultModelId = "gpt-3.5-turbo"
            },
            new AIModelInfo
            {
                Id = "qwen",
                Name = "通义千问",
                ApiBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode",
                DefaultModelId = "qwen-plus"
            },
            new AIModelInfo
            {
                Id = "custom",
                Name = "自定义",
                ApiBaseUrl = "",
                DefaultModelId = ""
            }
        };

        static AIConfigManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "SideDesk");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "aiconfig.json");
        }

        public static AIConfig GetConfig()
        {
            if (_config != null)
                return _config;

            lock (_lock)
            {
                if (_config != null)
                    return _config;

                try
                {
                    // 主存储：SQLite（local_app_state KV）；无值时从旧 aiconfig.json 一次性迁入
                    if (Services.LocalDbService.DbExists)
                    {
                        string? dbJson = Services.LocalDbService.LoadBusinessData(DbKey);
                        if (!string.IsNullOrEmpty(dbJson))
                            _config = JsonConvert.DeserializeObject<AIConfig>(dbJson);
                        else if (File.Exists(_filePath))
                        {
                            string fileJson = File.ReadAllText(_filePath);
                            _config = JsonConvert.DeserializeObject<AIConfig>(fileJson);
                            if (_config != null)
                                Services.LocalDbService.SaveBusinessData(DbKey, JsonConvert.SerializeObject(_config));
                        }
                    }
                    else if (File.Exists(_filePath))
                    {
                        string json = File.ReadAllText(_filePath);
                        _config = JsonConvert.DeserializeObject<AIConfig>(json);
                    }
                }
                catch
                {
                    // Ignore load errors
                }

                if (_config == null)
                {
                    _config = new AIConfig
                    {
                        SelectedModelId = "deepseek"
                    };
                }

                return _config;
            }
        }

        public static void SaveConfig(AIConfig config)
        {
            lock (_lock)
            {
                try
                {
                    _config = config;
                    string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                    if (Services.LocalDbService.DbExists)
                    {
                        try { Services.LocalDbService.SaveBusinessData(DbKey, json); return; }
                        catch { }
                    }
                    // db 不可用/未建立：退回旧 JSON 存储
                    File.WriteAllText(_filePath, json);
                }
                catch
                {
                    // Ignore save errors
                }
            }
        }

        public static AIModelInfo GetSelectedModel()
        {
            var config = GetConfig();
            return SupportedModels.Find(m => m.Id == config.SelectedModelId) ?? SupportedModels[0];
        }
    }
}
