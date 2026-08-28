using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SideDesk.Models
{
    /// <summary>
    /// 程序注册条目
    /// </summary>
    public class ProgramEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
    }

    /// <summary>
    /// 浏览器配置
    /// </summary>
    public class BrowserConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public List<UserDataProfile> Profiles { get; set; } = new();
    }

    /// <summary>
    /// 浏览器 UserData 配置
    /// </summary>
    public class UserDataProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Directory { get; set; } = "";
    }

    /// <summary>
    /// 程序注册表管理器（持久化 JSON）
    /// </summary>
    public class ProgramRegistryManager
    {
        public List<ProgramEntry> Programs { get; set; } = new();
        public List<BrowserConfig> Browsers { get; set; } = new();

        /// <summary>
        /// 默认 Profile 基础目录（所有浏览器通用）
        /// 自动新建 profile 时会在此目录下创建子目录
        /// </summary>
        public string DefaultProfileBaseDir { get; set; } = "";

        private static readonly string FilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "program-registry.json");

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProgramRegistry] save failed: {ex.Message}");
            }
        }

        public static ProgramRegistryManager Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<ProgramRegistryManager>(json) ?? new ProgramRegistryManager();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProgramRegistry] load failed: {ex.Message}");
            }
            return new ProgramRegistryManager();
        }

        /// <summary>
        /// 根据 Id 获取浏览器配置
        /// </summary>
        public BrowserConfig? GetBrowserById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Browsers.Find(b => b.Id == id);
        }

        /// <summary>
        /// 根据浏览器 Id 和 Profile Id 获取 UserData 配置
        /// </summary>
        public UserDataProfile? GetProfileById(string? browserId, string? profileId)
        {
            if (string.IsNullOrEmpty(browserId) || string.IsNullOrEmpty(profileId)) return null;
            var browser = GetBrowserById(browserId);
            return browser?.Profiles.Find(p => p.Id == profileId);
        }
    }
}
