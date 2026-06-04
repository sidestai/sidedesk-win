using System;
using System.Windows;
using System.Diagnostics;

namespace SideDesk
{
    public partial class MainWindow : AppBarWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            // 配置 AppBar
            DockSide = DockEdge.Left;   // 停靠在左侧
            AppBarWidth = 205;          // 宽度 205 物理像素
            ScreenIndex = 0;            // 主显示器

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 窗口加载后注册 AppBar
            RegisterAppBar();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 先注销 AppBar 释放屏幕空间，再关闭窗口
            UnregisterAppBar();
            Close();
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            // 拖拽进入时显示复制指针
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            // 处理拖放的文件
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                foreach (var file in files)
                {
                    Debug.WriteLine($"[Drop] 文件: {file}");
                    // TODO: 处理拖入的文件
                }

                MessageBox.Show($"拖入了 {files.Length} 个文件:\n{string.Join("\n", files)}",
                    "SideDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.Data.GetData(DataFormats.Text)!;
                Debug.WriteLine($"[Drop] 文本: {text}");
                // TODO: 处理拖入的文本
            }

            e.Handled = true;
        }
    }
}