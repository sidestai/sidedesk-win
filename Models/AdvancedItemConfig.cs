using System.Collections.Generic;
using Newtonsoft.Json;

namespace SideDesk.Models
{
    /// <summary>
    /// 高级项（扩展）动作配置：序列化为 JSON 存入 QuickItem.AdvancedConfigJson。
    /// ActionType 决定执行行为，各字段按类型取用；未涉及的字段保持默认值。
    /// </summary>
    public class AdvancedItemConfig
    {
        /// <summary>动作类型：url / exec / keys / paste / script / clipboard / search</summary>
        public string ActionType { get; set; } = "url";

        // ===== 通用 =====
        /// <summary>描述（仅展示用）</summary>
        public string? Description { get; set; }
        /// <summary>关键词（逗号分隔，仅展示/搜索用）</summary>
        public string? Keywords { get; set; }
        /// <summary>全局热键（仅存储，暂不注册）</summary>
        public string? Hotkey { get; set; }

        // ===== url =====
        /// <summary>网址</summary>
        public string? Url { get; set; }
        /// <summary>ProgramRegistry 浏览器 Id（空=系统默认）</summary>
        public string? BrowserId { get; set; }
        /// <summary>以 APP 模式打开（--app，仅指定浏览器时生效）</summary>
        public bool UseAppMode { get; set; }
        /// <summary>浏览器 Profile Id（可选，空=不使用）</summary>
        public string? ProfileId { get; set; }

        // ===== exec =====
        /// <summary>程序/文件/文件夹路径</summary>
        public string? ProgramPath { get; set; }
        /// <summary>启动参数</summary>
        public string? LaunchArgs { get; set; }
        /// <summary>工作目录（空=程序所在目录）</summary>
        public string? WorkingDir { get; set; }
        /// <summary>启动方式：normal / admin / minimized / hidden</summary>
        public string LaunchMode { get; set; } = "normal";

        // ===== keys =====
        /// <summary>按键序列，每项如 "Ctrl+Shift+P"，按顺序发送</summary>
        public List<string> KeySequences { get; set; } = new List<string>();
        /// <summary>每步间隔（毫秒）</summary>
        public int StepIntervalMs { get; set; } = 50;
        /// <summary>触发后延迟（毫秒），等待原窗口接管焦点</summary>
        public int TriggerDelayMs { get; set; } = 100;

        // ===== paste =====
        /// <summary>粘贴内容</summary>
        public string? PasteText { get; set; }
        /// <summary>粘贴方式：ctrlv（模拟 Ctrl+V）/ type（逐字符输入）/ clipboard（仅替换剪贴板）</summary>
        public string PasteMode { get; set; } = "ctrlv";
        /// <summary>完成后是否恢复原剪贴板</summary>
        public bool RestoreClipboard { get; set; } = true;

        // ===== script =====
        /// <summary>脚本运行时：powershell / cmd</summary>
        public string ScriptRuntime { get; set; } = "powershell";
        /// <summary>脚本代码</summary>
        public string? ScriptCode { get; set; }
        /// <summary>输出处理：notify（通知）/ paste（粘贴到光标）/ copy（复制到剪贴板）/ none</summary>
        public string ScriptOutput { get; set; } = "notify";
        /// <summary>脚本超时（秒）</summary>
        public int ScriptTimeoutSec { get; set; } = 10;

        // ===== clipboard =====
        /// <summary>要写入剪贴板的文本</summary>
        public string? ClipboardText { get; set; }
        /// <summary>完成后是否显示气泡通知</summary>
        public bool ClipboardNotify { get; set; } = true;

        // ===== search =====
        /// <summary>搜索引擎模板，含 {query} 占位符</summary>
        public string? SearchTemplate { get; set; }

        /// <summary>序列化为缩进 JSON（存储格式）</summary>
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

        /// <summary>从 JSON 反序列化；失败返回 null</summary>
        public static AdvancedItemConfig? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<AdvancedItemConfig>(json!); }
            catch { return null; }
        }
    }
}
