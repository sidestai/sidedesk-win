using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SideDesk.Models
{
    /// <summary>
    /// 扩展定义
    /// </summary>
    public class Extension
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>扩展类型：open（打开网址/程序）, paste（粘贴文本）, powershell, csharp</summary>
        public string Type { get; set; } = "open";
        /// <summary>目标内容：URL/路径（open）、粘贴内容（paste）、脚本代码（powershell/csharp）</summary>
        public string Target { get; set; } = "";
        /// <summary>图标标识</summary>
        public string Icon { get; set; } = "";
        /// <summary>主题颜色（HEX）</summary>
        public string ThemeColor { get; set; } = "#3B82F6";
        /// <summary>启动参数（open 类型用）</summary>
        public string LaunchArgs { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 扩展管理器（持久化 JSON）
    /// </summary>
    public class ExtensionManager
    {
        public List<Extension> Extensions { get; set; } = new();

        private static readonly string FilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "extension-list.json");

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
                System.Diagnostics.Debug.WriteLine($"[ExtensionManager] 保存失败: {ex.Message}");
            }
        }

        public static ExtensionManager Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<ExtensionManager>(json) ?? new ExtensionManager();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExtensionManager] 加载失败: {ex.Message}");
            }
            return new ExtensionManager();
        }

        public void Add(Extension ext)
        {
            Extensions.Add(ext);
            Save();
        }

        public void Remove(string id)
        {
            Extensions.RemoveAll(e => e.Id == id);
            Save();
        }

        public Extension? GetById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Extensions.Find(e => e.Id == id);
        }
    }
}
