using System;
using System.IO;
using System.Windows;
using Velopack;

namespace SideDesk
{
    /// <summary>
    /// 自定义程序入口：Velopack 要求 VelopackApp.Run() 位于 Main() 第一行，
    /// 因此不再使用 WPF 从 App.xaml 自动生成的 Main（csproj 中 StartupObject 指向本类）。
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 静态构造函数在 Main() 之前执行：先把 CWD 切到临时目录，
        /// 避免安装/更新/卸载钩子删除应用目录时被进程 CWD 锁定。
        /// </summary>
        static Program()
        {
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }
            // 最早设置 Per-Monitor DPI Aware V2（早于任何窗口/DPI 查询）：
            // manifest 的 dpiAwareness 可能被系统兼容性设置/shim 忽略（实测副屏150%仍报 dpi=96），
            // 代码兜底确保异 DPI 多屏（主100%+副150%）下 GetMonitorInfo/GetDpiForMonitor/SHAppBarMessage
            // 返回真实物理值；否则副屏 DPI 被虚拟化为 96，AppBar 注册宽度与真实区域错位（bar 超限定区压最大化窗口）
            try { SetProcessDpiAwarenessContext((IntPtr)DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        }

        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        private static void Main()
        {
            // Velopack 必须最先执行，拦截安装/更新/卸载命令行钩子（钩子场景下 Run() 内部直接退出进程）
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
