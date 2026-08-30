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
        /// <summary>所属区域：cn=中国 / intl=国际 / custom=自定义（仅用于设置页分组展示）</summary>
        public string Region { get; set; }
        /// <summary>官方 API Key 申请地址（设置页展示用）</summary>
        public string ApiKeyUrl { get; set; }
    }

    public class AIConfig
    {
        public string SelectedModelId { get; set; }
        public string ApiKey { get; set; }
        public string CustomModelId { get; set; }
        /// <summary>按服务商保存的 API Key（provider id → key）；未配置的服务商不在主下拉中显示</summary>
        public Dictionary<string, string> ApiKeys { get; set; }
        /// <summary>已同步到云端的 Key 记录 ID（provider id → 服务端记录 id），用于云端删除/覆盖更新</summary>
        public Dictionary<string, string> ServerKeyIds { get; set; }
    }

    public static class AIConfigManager
    {
        private const string DbKey = "ai_config";
        private static readonly string _filePath;
        private static AIConfig _config;
        private static readonly object _lock = new object();

        public static readonly List<AIModelInfo> SupportedModels = new List<AIModelInfo>
        {
            // ---------- 中国厂商 ----------
            new AIModelInfo { Id = "deepseek", Name = "深度求索 (DeepSeek)", ApiBaseUrl = "https://api.deepseek.com", DefaultModelId = "deepseek-chat", Region = "cn", ApiKeyUrl = "https://platform.deepseek.com/api_keys" },
            new AIModelInfo { Id = "qwen", Name = "阿里巴巴 (通义千问)", ApiBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode", DefaultModelId = "qwen-plus", Region = "cn", ApiKeyUrl = "https://dashscope.console.aliyun.com/api-key" },
            new AIModelInfo { Id = "doubao", Name = "火山引擎豆包", ApiBaseUrl = "https://ark.cn-beijing.volces.com/api/v3", DefaultModelId = "doubao-1-5-pro-32k-250115", Region = "cn", ApiKeyUrl = "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey" },
            new AIModelInfo { Id = "wenxin", Name = "百度（文心一言）", ApiBaseUrl = "https://qianfan.baidubce.com/v2", DefaultModelId = "ernie-4.0-8k", Region = "cn", ApiKeyUrl = "https://console.bce.baidu.com/qianfan/ais/console/applicationConsole/application/v2" },
            new AIModelInfo { Id = "hunyuan", Name = "腾讯混元", ApiBaseUrl = "https://api.hunyuan.cloud.tencent.com/v1", DefaultModelId = "hunyuan-large", Region = "cn", ApiKeyUrl = "https://console.cloud.tencent.com/hunyuan/api-key" },
            new AIModelInfo { Id = "glm", Name = "智谱AI (GLM)", ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4", DefaultModelId = "glm-4", Region = "cn", ApiKeyUrl = "https://bigmodel.cn/usercenter/apikeys" },
            new AIModelInfo { Id = "kimi", Name = "月之暗面 (Kimi)", ApiBaseUrl = "https://api.moonshot.cn/v1", DefaultModelId = "moonshot-v1-8k", Region = "cn", ApiKeyUrl = "https://platform.moonshot.cn/console/api-keys" },
            new AIModelInfo { Id = "spark", Name = "科大讯飞 (星火)", ApiBaseUrl = "https://spark-api-open.xf-yun.com/v1", DefaultModelId = "general", Region = "cn", ApiKeyUrl = "https://console.xfyun.cn/services" },
            new AIModelInfo { Id = "minimax", Name = "MiniMax (海螺AI)", ApiBaseUrl = "https://api.minimax.chat/v1", DefaultModelId = "MiniMax-Text-01", Region = "cn", ApiKeyUrl = "https://platform.minimaxi.com/user-center/basic-information/interface-key" },
            new AIModelInfo { Id = "mimo", Name = "小米Mimo", ApiBaseUrl = "https://api.xiaomimimo.com/v1", DefaultModelId = "mimo-chat", Region = "cn", ApiKeyUrl = "https://xiaomimimo.com" },
            new AIModelInfo { Id = "tencentcloud", Name = "腾讯云", ApiBaseUrl = "https://api.lkeap.cloud.tencent.com/v1", DefaultModelId = "hunyuan-large", Region = "cn", ApiKeyUrl = "https://console.cloud.tencent.com/lkeap/api" },
            // ---------- 国际厂商 ----------
            new AIModelInfo { Id = "openai", Name = "OpenAI", ApiBaseUrl = "https://api.openai.com", DefaultModelId = "gpt-3.5-turbo", Region = "intl", ApiKeyUrl = "https://platform.openai.com/api-keys" },
            new AIModelInfo { Id = "anthropic", Name = "Anthropic (Claude)", ApiBaseUrl = "https://api.anthropic.com", DefaultModelId = "claude-sonnet-4-20250514", Region = "intl", ApiKeyUrl = "https://console.anthropic.com/settings/keys" },
            new AIModelInfo { Id = "google", Name = "Google Gemini", ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", DefaultModelId = "gemini-2.0-flash", Region = "intl", ApiKeyUrl = "https://aistudio.google.com/apikey" },
            new AIModelInfo { Id = "xai", Name = "xAI (Grok)", ApiBaseUrl = "https://api.x.ai/v1", DefaultModelId = "grok-3", Region = "intl", ApiKeyUrl = "https://console.x.ai" },
            new AIModelInfo { Id = "mistral", Name = "Mistral AI", ApiBaseUrl = "https://api.mistral.ai/v1", DefaultModelId = "mistral-large-latest", Region = "intl", ApiKeyUrl = "https://console.mistral.ai/api-keys" },
            new AIModelInfo { Id = "cohere", Name = "Cohere", ApiBaseUrl = "https://api.cohere.ai/compatibility/v1", DefaultModelId = "command-r-plus", Region = "intl", ApiKeyUrl = "https://dashboard.cohere.com/api-keys" },
            new AIModelInfo { Id = "groq", Name = "Groq", ApiBaseUrl = "https://api.groq.com/openai/v1", DefaultModelId = "llama-3.3-70b-versatile", Region = "intl", ApiKeyUrl = "https://console.groq.com/keys" },
            new AIModelInfo { Id = "together", Name = "Together AI", ApiBaseUrl = "https://api.together.xyz/v1", DefaultModelId = "meta-llama/Llama-3.3-70B-Instruct-Turbo", Region = "intl", ApiKeyUrl = "https://api.together.xyz/settings/api-keys" },
            new AIModelInfo { Id = "perplexity", Name = "Perplexity", ApiBaseUrl = "https://api.perplexity.ai", DefaultModelId = "sonar", Region = "intl", ApiKeyUrl = "https://www.perplexity.ai/settings/api" },
            // ---------- 自定义 ----------
            new AIModelInfo { Id = "custom", Name = "自定义", ApiBaseUrl = "", DefaultModelId = "", Region = "custom" }
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

                MigrateLegacyKey(_config);

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

        /// <summary>旧版单 Key 迁移：视为属于当时选中的服务商</summary>
        private static void MigrateLegacyKey(AIConfig config)
        {
            if (config.ApiKeys == null)
                config.ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(config.ApiKey)
                && !string.IsNullOrEmpty(config.SelectedModelId)
                && !config.ApiKeys.ContainsKey(config.SelectedModelId))
            {
                config.ApiKeys[config.SelectedModelId] = config.ApiKey;
            }
        }

        /// <summary>取指定服务商的 API Key（优先按服务商字典，回退旧版单 Key）</summary>
        public static string GetApiKeyFor(string providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return string.Empty;
            var cfg = GetConfig();
            if (cfg.ApiKeys != null && cfg.ApiKeys.TryGetValue(providerId, out var key) && !string.IsNullOrEmpty(key))
                return key;
            if (providerId.Equals(cfg.SelectedModelId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cfg.ApiKey))
                return cfg.ApiKey;
            return string.Empty;
        }

        /// <summary>服务商是否已配置（有 API Key）</summary>
        public static bool IsProviderConfigured(string providerId) => !string.IsNullOrEmpty(GetApiKeyFor(providerId));

        /// <summary>已配置的服务商列表（保持预设顺序），未配置的不在主下拉中显示；英文语言下隐藏中国厂商</summary>
        public static List<AIModelInfo> GetConfiguredModels() => SupportedModels.FindAll(m =>
            IsProviderConfigured(m.Id) && !(Framework.Lang.Current == Framework.Lang.En && m.Region == "cn"));

        /// <summary>新增/更新一个服务商定义（设置页「大模型」保存自定义厂商时调用；同 Id 覆盖）</summary>
        public static void AddOrUpdateModel(AIModelInfo model)
        {
            int idx = SupportedModels.FindIndex(m => m.Id == model.Id);
            if (idx >= 0) SupportedModels[idx] = model;
            else SupportedModels.Insert(Math.Max(0, SupportedModels.Count - 1), model); // 保持在 custom 之前
        }

        /// <summary>移除一个服务商定义及其 Key（设置页删除时调用；内置预设也允许移除，重启后恢复）</summary>
        public static void RemoveModel(string providerId)
        {
            var cfg = GetConfig();
            cfg.ApiKeys?.Remove(providerId);
            cfg.ServerKeyIds?.Remove(providerId);
            SupportedModels.RemoveAll(m => m.Id == providerId);
            if (cfg.SelectedModelId == providerId)
                cfg.SelectedModelId = SupportedModels.Count > 0 ? SupportedModels[0].Id : null;
            SaveConfig(cfg);
        }

        /// <summary>取服务商对应的云端记录 ID（未同步返回 null）</summary>
        public static string GetServerKeyId(string providerId)
        {
            var cfg = GetConfig();
            return cfg.ServerKeyIds != null && cfg.ServerKeyIds.TryGetValue(providerId, out var id) ? id : null;
        }

        /// <summary>记录服务商的云端记录 ID（同步成功后回写）</summary>
        public static void SetServerKeyId(string providerId, string serverId)
        {
            var cfg = GetConfig();
            if (cfg.ServerKeyIds == null)
                cfg.ServerKeyIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            cfg.ServerKeyIds[providerId] = serverId;
            SaveConfig(cfg);
        }
    }
}
