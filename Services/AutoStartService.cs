using System;
using Microsoft.Win32;

namespace SideDesk.Services
{
    /// <summary>
    /// 开机自启动服务：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表项管理。
    /// 无需管理员权限、随用户登录启动；Velopack 更新后 exe 路径不变，注册值无需迁移。
    /// </summary>
    public static class AutoStartService
    {
        private const string RunKeyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SideDesk";

        /// <summary>当前可执行文件的带引号完整路径（注册表值需带引号，路径含空格时才能正确启动）</summary>
        private static string ExePath => $"\"{Environment.ProcessPath}\"";

        /// <summary>当前是否已注册开机自启（且指向当前 exe 路径）</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyName);
                return string.Equals(key?.GetValue(ValueName) as string, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>按设置同步注册表状态：启用则写入（含路径修正），禁用则删除</summary>
        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyName);
                if (key == null) return;

                if (enabled) key.SetValue(ValueName, ExePath);
                else key.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoStart] 设置开机自启失败: {ex.Message}");
            }
        }
    }
}
