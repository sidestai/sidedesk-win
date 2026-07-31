using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>元桌云盘文件管理窗口：文件夹层级浏览、上传（STS+OSS直传）、新建目录、下载、删除</summary>
    public partial class CloudDriveWindow : Window
    {
        /// <summary>面包屑路径栈：空 = 根目录；每项为 (folderId, folderName)</summary>
        private readonly List<(string Id, string Name)> _pathStack = new();
        private bool _uploading;

        public CloudDriveWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

            // 标题栏显示当前团队名（个人模式显示“个人”）
            var app = (App)Application.Current;
            var corpName = app.AppSettings.SelectedCorpName;
            titleText.Text = string.IsNullOrEmpty(corpName) ? "☁️ 个人" : $"☁️ {corpName}";
            corpNameText.Text = "";

            Loaded += async (_, _) => await LoadFilesAsync(updateCapacity: true);
        }

        /// <summary>打开并定位到指定云目录（folderId 非空时直接显示该目录内容）</summary>
        public CloudDriveWindow(string initialFolderId, string initialFolderName) : this()
        {
            if (!string.IsNullOrEmpty(initialFolderId))
                _pathStack.Add((initialFolderId, initialFolderName));
        }

        /// <summary>当前所在目录 ID（"0" = 根目录）</summary>
        private string CurrentParentId => _pathStack.Count > 0 ? _pathStack[^1].Id : "0";

        private bool IsLoggedIn
        {
            get
            {
                var app = (App)Application.Current;
                return !string.IsNullOrEmpty(app.AppSettings.AccountToken);
            }
        }

        #region 文件列表加载

        private async Task LoadFilesAsync(bool updateCapacity = false)
        {
            if (!IsLoggedIn)
            {
                ShowPanel(notLoginPanel);
                return;
            }
        
            ShowPanel(loadingText);
            RefreshBreadcrumb();
        
            var files = await CloudDriveService.Instance.ListFilesAsync(CurrentParentId);
        
            if (files == null)
            {
                ShowPanel(errorPanel);
                statusText.Text = "";
                return;
            }
        
            fileListView.ItemsSource = files;
        
            if (files.Count == 0)
                ShowPanel(emptyText);
            else
                ShowPanel(fileListView);
        
            statusText.Text = $"共 {files.Count} 项";
        
            if (updateCapacity) _ = UpdateCapacityAsync();
        }
        
        /// <summary>递归统计并显示整个云盘已用容量（不阻塞列表加载）</summary>
        private async Task UpdateCapacityAsync()
        {
            try
            {
                long bytes = await CloudDriveService.Instance.GetTotalUsedBytesAsync();
                capacityText.Text = $"💾 已用 {CloudDriveFile.FormatSize(bytes)}";
            }
            catch
            {
                capacityText.Text = "";
            }
        }

        /// <summary>切换中部区域显示内容</summary>
        private void ShowPanel(UIElement target)
        {
            notLoginPanel.Visibility = ReferenceEquals(target, notLoginPanel) ? Visibility.Visible : Visibility.Collapsed;
            loadingText.Visibility = ReferenceEquals(target, loadingText) ? Visibility.Visible : Visibility.Collapsed;
            emptyText.Visibility = ReferenceEquals(target, emptyText) ? Visibility.Visible : Visibility.Collapsed;
            errorPanel.Visibility = ReferenceEquals(target, errorPanel) ? Visibility.Visible : Visibility.Collapsed;
            fileListView.Visibility = ReferenceEquals(target, fileListView) ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 面包屑导航

        private void RefreshBreadcrumb()
        {
            breadcrumbPanel.Children.Clear();

            // 根目录按钮
            breadcrumbPanel.Children.Add(CreateCrumbButton("\u2601\uFE0F \u5168\u90E8\u6587\u4EF6", -1));

            for (int i = 0; i < _pathStack.Count; i++)
            {
                breadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "\u203A",
                    Foreground = (Brush)FindResource("BrushTextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                });
                breadcrumbPanel.Children.Add(CreateCrumbButton(_pathStack[i].Name, i));
            }
        }

        private Button CreateCrumbButton(string text, int level)
        {
            bool isCurrent = level == _pathStack.Count - 1;
            var btn = new Button
            {
                Content = text,
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                IsEnabled = !isCurrent
            };
            int targetLevel = level;
            btn.Click += async (_, _) =>
            {
                while (_pathStack.Count > targetLevel + 1)
                    _pathStack.RemoveAt(_pathStack.Count - 1);
                await LoadFilesAsync();
            };
            return btn;
        }

        #endregion

        #region 事件处理

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadFilesAsync(updateCapacity: true);
        }

        /// <summary>双击列表项：文件夹则进入子目录</summary>
        private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (fileListView.SelectedItem is CloudDriveFile file && file.IsFolder)
            {
                _pathStack.Add((file.Id, file.Name));
                await LoadFilesAsync();
            }
        }

        /// <summary>上传文件（支持多选）</summary>
        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            if (_uploading) return;
            if (!IsLoggedIn)
            {
                MessageBox.Show("\u8BF7\u5148\u767B\u5F55\u540E\u518D\u4E0A\u4F20\u6587\u4EF6", "\u63D0\u793A", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "\u9009\u62E9\u8981\u4E0A\u4F20\u7684\u6587\u4EF6",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

            _uploading = true;
            uploadBtn.IsEnabled = false;
            progressPanel.Visibility = Visibility.Visible;

            int successCount = 0;
            int totalCount = dlg.FileNames.Length;
            string parentId = CurrentParentId;

            for (int i = 0; i < totalCount; i++)
            {
                string path = dlg.FileNames[i];
                string fileName = System.IO.Path.GetFileName(path);
                uploadProgressText.Text = $"\u6B63\u5728\u4E0A\u4F20 ({i + 1}/{totalCount}): {fileName}";
                uploadProgress.Value = 0;

                bool ok = await Task.Run(() =>
                    CloudDriveService.Instance.UploadFileAsync(path, parentId,
                        percent => Dispatcher.BeginInvoke(() => uploadProgress.Value = percent)));

                if (ok) successCount++;
                else
                    System.Diagnostics.Debug.WriteLine($"[CloudDrive] \u4E0A\u4F20\u5931\u8D25: {fileName}");
            }

            _uploading = false;
            uploadBtn.IsEnabled = true;

            if (successCount < totalCount)
            {
                uploadProgressText.Text = $"\u5B8C\u6210\uFF1A{successCount}/{totalCount} \u6210\u529F";
                MessageBox.Show($"\u4E0A\u4F20\u5B8C\u6210\uFF1A{successCount} \u6210\u529F\uFF0C{totalCount - successCount} \u5931\u8D25",
                    "\u4E0A\u4F20\u7ED3\u679C", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                uploadProgressText.Text = $"\u5168\u90E8\u4E0A\u4F20\u6210\u529F\uFF08{successCount} \u4E2A\u6587\u4EF6\uFF09";
            }

            await Task.Delay(3000);
            if (!_uploading) progressPanel.Visibility = Visibility.Collapsed;

            await LoadFilesAsync(updateCapacity: true);
        }

        /// <summary>新建文件夹</summary>
        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLoggedIn) return;

            // 简单输入对话框
            var inputDialog = new Window
            {
                Title = "新建文件夹", Width = 320, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushBG")
            };
            inputDialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(inputDialog);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "\u6587\u4EF6\u5939\u540D\u79F0", Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            panel.Children.Add(nameBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var okBtn = new Button { Content = "\u786E\u5B9A", Width = 64, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "\u53D6\u6D88", Width = 64, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);
            inputDialog.Content = panel;

            bool confirmed = false;
            okBtn.Click += (_, _) => { confirmed = true; inputDialog.Close(); };
            cancelBtn.Click += (_, _) => inputDialog.Close();
            inputDialog.ShowDialog();

            if (!confirmed) return;
            string folderName = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(folderName)) return;

            var result = await CloudDriveService.Instance.MkdirAsync(CurrentParentId, folderName);
            if (result != null)
                await LoadFilesAsync(updateCapacity: true);
            else
                MessageBox.Show("\u65B0\u5EFA\u5931\u8D25\uFF0C\u53EF\u80FD\u5DF2\u5B58\u5728\u540C\u540D\u76EE\u5F55", "\u64CD\u4F5C\u5931\u8D25", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>右键菜单 - 下载</summary>
        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not CloudDriveFile file) return;
            if (file.IsFolder)
            {
                _pathStack.Add((file.Id, file.Name));
                await LoadFilesAsync();
                return;
            }

            string? url = CloudDriveService.Instance.BuildDownloadUrl(file);
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("\u65E0\u6CD5\u83B7\u53D6\u4E0B\u8F7D\u5730\u5740", "\u4E0B\u8F7D\u5931\u8D25", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>右键菜单 - 删除（带确认，目录递归删除）</summary>
        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not CloudDriveFile file) return;

            string typeText = file.IsFolder ? "\u6587\u4EF6\u5939\uFF08\u542B\u5176\u4E0B\u6240\u6709\u5185\u5BB9\uFF09" : "\u6587\u4EF6";
            if (MessageBox.Show($"\u786E\u5B9A\u5220\u9664{typeText}\u201C{file.Name}\u201D\uFF1F\u6B64\u64CD\u4F5C\u4E0D\u53EF\u6062\u590D\u3002",
                "\u5220\u9664\u786E\u8BA4", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            bool ok = await CloudDriveService.Instance.DeleteAsync(file.Id);
            if (ok)
                await LoadFilesAsync(updateCapacity: true);
            else
                MessageBox.Show("\u5220\u9664\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5", "\u64CD\u4F5C\u5931\u8D25", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion
    }
}
