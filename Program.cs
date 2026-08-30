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
        }

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
