using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk
{
    /// <summary>云盘文件管理窗口：文件夹层级浏览、上传、新建目录、下载、删除。
    /// 支持两种存储后端：元桌云盘（默认）与用户配置的 S3 存储源（客户端直连）</summary>
    public partial class CloudDriveWindow : Window
    {
        /// <summary>面包屑路径栈：空 = 根目录；每项为 (folderId, folderName)</summary>
        private readonly List<(string Id, string Name)> _pathStack = new();
        /// <summary>当前存储后端（元桌云盘 / S3 源）</summary>
        private readonly IDriveBackend _backend;
        private bool _uploading;

        public CloudDriveWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);

            _backend = new MetadeskDriveBackend();

            // 标题栏显示当前团队名（个人模式显示“个人”）
            var app = (App)Application.Current;
            var corpName = app.AppSettings.SelectedCorpName;
            titleText.Text = string.IsNullOrEmpty(corpName) ? Framework.Lang.T("cloudDrive.personal") : $"☁️ {corpName}";
            corpNameText.Text = "";

            Loaded += async (_, _) => await LoadFilesAsync(updateCapacity: true);
        }

        /// <summary>以 S3 存储源打开（客户端直连，无需元桌登录）；可选定位到指定目录</summary>
        public CloudDriveWindow(S3Source source, string? initialFolderId = null, string? initialFolderName = null) : this()
        {
            _backend = new S3DriveBackend(source);
            titleText.Text = $"🪣 {source.Name}";
            corpNameText.Text = $"{source.Bucket} · {source.Region}";
            PushInitialFolder(initialFolderId, initialFolderName);
        }

        /// <summary>以 WebDAV 存储源打开（客户端直连，无需元桌登录）；可选定位到指定目录</summary>
        public CloudDriveWindow(WebDavSource source, string? initialFolderId = null, string? initialFolderName = null) : this()
        {
            _backend = new WebDavDriveBackend(source);
            titleText.Text = $"🌐 {source.Name}";
            try { corpNameText.Text = new Uri(source.BaseUrl).Host; }
            catch { corpNameText.Text = source.Url; }
            PushInitialFolder(initialFolderId, initialFolderName);
        }

        /// <summary>非根目录时压入面包屑路径栈（在 Loaded 加载前执行）</summary>
        private void PushInitialFolder(string? folderId, string? folderName)
        {
            if (!string.IsNullOrEmpty(folderId) && folderId != _backend.RootId)
                _pathStack.Add((folderId, string.IsNullOrEmpty(folderName) ? folderId : folderName));
        }

        /// <summary>打开并定位到指定云目录（folderId 非空时直接显示该目录内容）</summary>
        public CloudDriveWindow(string initialFolderId, string initialFolderName) : this()
        {
            if (!string.IsNullOrEmpty(initialFolderId))
                _pathStack.Add((initialFolderId, initialFolderName));
        }

        /// <summary>当前所在目录 ID（元桌 "0" = 根目录；S3 = 根前缀）</summary>
        private string CurrentParentId => _pathStack.Count > 0 ? _pathStack[^1].Id : _backend.RootId;

        private bool IsLoggedIn
        {
            get
            {
                var app = (App)Application.Current;
                return !string.IsNullOrEmpty(app.AppSettings.AccountToken);
            }
        }

        /// <summary>当前后端是否可操作（元桌云盘需登录，S3 源直连即可）</summary>
        private bool CanOperate => !_backend.RequiresLogin || IsLoggedIn;

        #region 文件列表加载

        private async Task LoadFilesAsync(bool updateCapacity = false)
        {
            if (!CanOperate)
            {
                ShowPanel(notLoginPanel);
                return;
            }
        
            ShowPanel(loadingText);
            RefreshBreadcrumb();
        
            var files = await _backend.ListFilesAsync(CurrentParentId);
        
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
        
            statusText.Text = Framework.Lang.TF("cloudDrive.itemsInTotal", files.Count);
        
            if (updateCapacity) _ = UpdateCapacityAsync();
        }
        
        /// <summary>递归统计并显示整个云盘已用容量（不阻塞列表加载）</summary>
        private async Task UpdateCapacityAsync()
        {
            try
            {
                long bytes = await _backend.GetTotalUsedBytesAsync();
                capacityText.Text = Framework.Lang.TF("cloudDrive.used", CloudDriveFile.FormatSize(bytes));
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
            breadcrumbPanel.Children.Add(CreateCrumbButton(Framework.Lang.T("cloudDrive.allFiles"), -1));

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
                Content = new Emoji.Wpf.TextBlock { Text = text },
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

        // ===== 拖拽上传 =====

        private void RootGrid_DragEnter(object sender, DragEventArgs e)
        {
            bool accept = CanOperate && !_uploading && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            dragOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RootGrid_DragLeave(object sender, DragEventArgs e)
        {
            dragOverlay.Visibility = Visibility.Collapsed;
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            if (!CanOperate || _uploading) { e.Effects = DragDropEffects.None; return; }
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void RootGrid_Drop(object sender, DragEventArgs e)
        {
            dragOverlay.Visibility = Visibility.Collapsed;
            if (!CanOperate || _uploading) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

            // 过滤掉目录，只上传文件
            var filePaths = files.Where(f => System.IO.File.Exists(f)).ToArray();
            if (filePaths.Length == 0) return;

            await UploadFilesAsync(filePaths);
        }

        // ===== 上传 =====

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
            if (!CanOperate)
            {
                MessageBox.Show(Framework.Lang.T("cloudDrive.loginRequiredUpload"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = Framework.Lang.T("cloudDrive.selectFilesToUpload"),
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

            await UploadFilesAsync(dlg.FileNames);
        }

        /// <summary>执行文件上传（按钮点击与拖拽共用）</summary>
        private async Task UploadFilesAsync(string[] filePaths)
        {
            _uploading = true;
            uploadBtn.IsEnabled = false;
            progressPanel.Visibility = Visibility.Visible;

            int successCount = 0;
            int totalCount = filePaths.Length;
            string parentId = CurrentParentId;

            for (int i = 0; i < totalCount; i++)
            {
                string path = filePaths[i];
                string fileName = System.IO.Path.GetFileName(path);
                uploadProgressText.Text = Framework.Lang.TF("cloudDrive.uploading", i + 1, totalCount, fileName);
                uploadProgress.Value = 0;

                bool ok = await Task.Run(() =>
                    _backend.UploadFileAsync(path, parentId,
                        percent => Dispatcher.BeginInvoke(() => uploadProgress.Value = percent)));

                if (ok) successCount++;
                else
                    System.Diagnostics.Debug.WriteLine($"[CloudDrive] \u4E0A\u4F20\u5931\u8D25: {fileName}");
            }

            _uploading = false;
            uploadBtn.IsEnabled = true;

            if (successCount < totalCount)
            {
                uploadProgressText.Text = Framework.Lang.TF("cloudDrive.doneSummary", successCount, totalCount);
                MessageBox.Show(Framework.Lang.TF("cloudDrive.uploadSummary", successCount, totalCount - successCount),
                    Framework.Lang.T("cloudDrive.uploadResult"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                uploadProgressText.Text = Framework.Lang.TF("cloudDrive.uploadAllSucceeded", successCount);
            }

            await Task.Delay(3000);
            if (!_uploading) progressPanel.Visibility = Visibility.Collapsed;

            await LoadFilesAsync(updateCapacity: true);
        }

        /// <summary>新建文件夹</summary>
        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate) return;

            // 简单输入对话框
            var inputDialog = new Window
            {
                Title = Framework.Lang.T("cloudDrive.newFolder"), Width = 320,
                SizeToContent = SizeToContent.Height, // 高度随内容自适应，避免固定高度裁切底部按钮
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushBG")
            };
            inputDialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(inputDialog);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("cloudDrive.folderName"), Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            panel.Children.Add(nameBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var okBtn = new Button { Content = Framework.Lang.T("common.ok"), Width = 64, Padding = new Thickness(0, 6, 0, 6), IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("common.cancel"), Width = 64, Padding = new Thickness(0, 6, 0, 6), IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
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

            var result = await _backend.MkdirAsync(CurrentParentId, folderName);
            if (result != null)
                await LoadFilesAsync(updateCapacity: true);
            else
                MessageBox.Show(Framework.Lang.T("cloudDrive.createFolderFailedExists"), Framework.Lang.T("common.operationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>右键菜单 - 下载（元桌云盘打开直链；S3 源认证下载到本地）</summary>
        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not CloudDriveFile file) return;
            if (file.IsFolder)
            {
                _pathStack.Add((file.Id, file.Name));
                await LoadFilesAsync();
                return;
            }

            string? url = _backend.BuildOpenUrl(file);
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
                return;
            }

            // S3 源：认证下载到用户选择的位置
            var saveDlg = new SaveFileDialog
            {
                Title = Framework.Lang.T("cloudDrive.saveFile"),
                FileName = file.Name
            };
            if (saveDlg.ShowDialog(this) != true) return;

            progressPanel.Visibility = Visibility.Visible;
            uploadProgress.Value = 0;
            uploadProgressText.Text = Framework.Lang.TF("cloudDrive.downloadingFile", file.Name);

            string savePath = saveDlg.FileName;
            bool ok = await Task.Run(() =>
                _backend.DownloadToFileAsync(file, savePath,
                    percent => Dispatcher.BeginInvoke(() => uploadProgress.Value = percent)));

            if (ok)
            {
                uploadProgressText.Text = Framework.Lang.TF("cloudDrive.saved", file.Name);
                await Task.Delay(3000);
                if (!_uploading) progressPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                progressPanel.Visibility = Visibility.Collapsed;
                MessageBox.Show(Framework.Lang.T("cloudDrive.downloadFailedPleaseTry"), Framework.Lang.T("common.operationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>右键菜单 - 删除（带确认，目录递归删除）</summary>
        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not CloudDriveFile file) return;

            string typeText = file.IsFolder ? Framework.Lang.T("cloudDrive.folderWithContents") : Framework.Lang.T("cloudDrive.fileItem");
            if (MessageBox.Show(Framework.Lang.TF("cloudDrive.confirmDeleteItem", typeText, file.Name),
                Framework.Lang.T("cloudDrive.confirmDeletion"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            bool ok = await _backend.DeleteAsync(file);
            if (ok)
                await LoadFilesAsync(updateCapacity: true);
            else
                MessageBox.Show(Framework.Lang.T("cloudDrive.deletionFailedPleaseTry"), Framework.Lang.T("common.operationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion
    }
}
