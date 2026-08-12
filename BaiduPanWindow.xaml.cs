using System;
using System.Collections.Generic;
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
    /// <summary>百度网盘文件管理窗口：OAuth 授权、文件夹层级浏览、上传、新建目录、下载、删除</summary>
    public partial class BaiduPanWindow : Window
    {
        /// <summary>面包屑路径栈：空 = 根目录 "/"；每项为 (dirPath, folderName)</summary>
        private readonly List<(string Path, string Name)> _pathStack = new();
        private bool _uploading;

        public BaiduPanWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
            Loaded += async (_, _) => await LoadFilesAsync(updateQuota: true);
        }

        /// <summary>打开并定位到指定目录</summary>
        public BaiduPanWindow(string initialDir, string initialName) : this()
        {
            if (!string.IsNullOrEmpty(initialDir) && initialDir != "/")
                _pathStack.Add((initialDir, initialName));
        }

        /// <summary>当前所在目录路径（"/" = 根目录）</summary>
        private string CurrentDir => _pathStack.Count > 0 ? _pathStack[^1].Path : "/";

        private bool IsAuthorized => BaiduPanService.Instance.IsAuthorized || BaiduPanService.Instance.HasRefreshToken;

        #region 文件列表加载

        private async Task LoadFilesAsync(bool updateQuota = false)
        {
            if (!IsAuthorized)
            {
                ShowPanel(notAuthPanel);
                return;
            }

            ShowPanel(loadingText);
            RefreshBreadcrumb();

            var files = await BaiduPanService.Instance.ListFilesAsync(CurrentDir);

            if (files == null)
            {
                // token 可能过期且刷新失败 → 回到未授权状态
                if (!BaiduPanService.Instance.IsAuthorized && !BaiduPanService.Instance.HasRefreshToken)
                {
                    ShowPanel(notAuthPanel);
                    return;
                }
                ShowPanel(errorPanel);
                statusText.Text = "";
                return;
            }

            fileListView.ItemsSource = files;

            if (files.Count == 0)
                ShowPanel(emptyText);
            else
                ShowPanel(fileListView);

            statusText.Text = Framework.Lang.TF("共 {0} 项", files.Count);

            if (updateQuota) _ = UpdateQuotaAsync();
        }

        /// <summary>获取并显示网盘容量信息</summary>
        private async Task UpdateQuotaAsync()
        {
            try
            {
                var quota = await BaiduPanService.Instance.GetQuotaAsync();
                quotaText.Text = quota != null ? $"💾 {quota.Summary}" : "";
            }
            catch
            {
                quotaText.Text = "";
            }
        }

        /// <summary>切换中部区域显示内容</summary>
        private void ShowPanel(UIElement target)
        {
            notAuthPanel.Visibility = ReferenceEquals(target, notAuthPanel) ? Visibility.Visible : Visibility.Collapsed;
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
            breadcrumbPanel.Children.Add(CreateCrumbButton(Framework.Lang.T("☁️ 全部文件"), -1));

            for (int i = 0; i < _pathStack.Count; i++)
            {
                breadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›",
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

        /// <summary>授权登录</summary>
        private async void Authorize_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(loadingText);
            statusText.Text = Framework.Lang.T("正在等待浏览器授权...");

            bool ok = await Task.Run(() => BaiduPanService.Instance.AuthorizeAsync());

            if (ok)
            {
                statusText.Text = Framework.Lang.T("授权成功");

                // 授权成功后自动添加云空间条目（若尚未存在）
                if (!CloudSpaceManager.GetAll().Exists(s => s.Name == "百度网盘"))
                {
                    CloudSpaceManager.Add(new CloudSpace
                    {
                        Name = "百度网盘",
                        Url = "baidupan://",
                        Icon = "🅱️",
                        Note = "百度网盘开放平台"
                    });
                }

                await LoadFilesAsync(updateQuota: true);
            }
            else
            {
                statusText.Text = Framework.Lang.T("授权失败或已取消");
                ShowPanel(notAuthPanel);
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadFilesAsync(updateQuota: true);
        }

        /// <summary>双击列表项：文件夹则进入子目录</summary>
        private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (fileListView.SelectedItem is BaiduPanFile file && file.IsFolder)
            {
                _pathStack.Add((file.Path, file.Name));
                await LoadFilesAsync();
            }
        }

        /// <summary>上传文件（支持多选）</summary>
        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            if (_uploading) return;
            if (!IsAuthorized)
            {
                MessageBox.Show(Framework.Lang.T("请先授权百度网盘"), Framework.Lang.T("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = Framework.Lang.T("选择要上传的文件"),
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

            _uploading = true;
            uploadBtn.IsEnabled = false;
            progressPanel.Visibility = Visibility.Visible;

            int successCount = 0;
            int totalCount = dlg.FileNames.Length;
            string remoteDir = CurrentDir;

            for (int i = 0; i < totalCount; i++)
            {
                string path = dlg.FileNames[i];
                string fileName = System.IO.Path.GetFileName(path);
                uploadProgressText.Text = Framework.Lang.TF("正在上传 ({0}/{1}): {2}", i + 1, totalCount, fileName);
                uploadProgress.Value = 0;

                bool ok = await Task.Run(() =>
                    BaiduPanService.Instance.UploadFileAsync(path, remoteDir,
                        percent => Dispatcher.BeginInvoke(() => uploadProgress.Value = percent)));

                if (ok) successCount++;
                else
                    System.Diagnostics.Debug.WriteLine($"[BaiduPan] 上传失败: {fileName}");
            }

            _uploading = false;
            uploadBtn.IsEnabled = true;

            if (successCount < totalCount)
            {
                uploadProgressText.Text = Framework.Lang.TF("完成：{0}/{1} 成功", successCount, totalCount);
                MessageBox.Show(Framework.Lang.TF("上传完成：{0} 成功，{1} 失败", successCount, totalCount - successCount),
                    Framework.Lang.T("上传结果"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                uploadProgressText.Text = Framework.Lang.TF("全部上传成功（{0} 个文件）", successCount);
            }

            await Task.Delay(3000);
            if (!_uploading) progressPanel.Visibility = Visibility.Collapsed;

            await LoadFilesAsync(updateQuota: true);
        }

        /// <summary>新建文件夹</summary>
        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAuthorized) return;

            // 简单输入对话框
            var inputDialog = new Window
            {
                Title = Framework.Lang.T("新建文件夹"), Width = 320, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = (Brush)FindResource("BrushBG")
            };
            inputDialog.SourceInitialized += (s, ev) => App.UpdateWindowDwmTheme(inputDialog);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = Framework.Lang.T("文件夹名称"), Foreground = (Brush)FindResource("BrushTextMain"),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
            });
            var nameBox = new TextBox { Style = (Style)FindResource("GlobalTextBoxStyle") };
            panel.Children.Add(nameBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var okBtn = new Button { Content = Framework.Lang.T("确定"), Width = 64, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle"), Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = Framework.Lang.T("取消"), Width = 64, IsCancel = true, Style = (Style)FindResource("StandardButtonStyle") };
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

            string newPath = CurrentDir.TrimEnd('/') + "/" + folderName;
            var result = await BaiduPanService.Instance.MkdirAsync(newPath);
            if (result != null)
                await LoadFilesAsync(updateQuota: true);
            else
                MessageBox.Show(Framework.Lang.T("新建失败，可能已存在同名目录"), Framework.Lang.T("操作失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>右键菜单 - 下载</summary>
        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not BaiduPanFile file) return;
            if (file.IsFolder)
            {
                _pathStack.Add((file.Path, file.Name));
                await LoadFilesAsync();
                return;
            }

            // 获取下载链接
            statusText.Text = Framework.Lang.T("正在获取下载地址...");
            string? dlink = await BaiduPanService.Instance.GetDownloadLinkAsync(file.FsId);
            if (string.IsNullOrEmpty(dlink))
            {
                MessageBox.Show(Framework.Lang.T("无法获取下载地址"), Framework.Lang.T("下载失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                statusText.Text = "";
                return;
            }

            // 选择保存位置
            var saveDlg = new SaveFileDialog
            {
                Title = Framework.Lang.T("保存文件"),
                FileName = file.Name,
                Filter = Framework.Lang.T("所有文件|*.*")
            };
            if (saveDlg.ShowDialog(this) != true) return;

            statusText.Text = Framework.Lang.TF("正在下载: {0}", file.Name);
            progressPanel.Visibility = Visibility.Visible;
            uploadProgress.Value = 0;

            bool ok = await Task.Run(() =>
                BaiduPanService.Instance.DownloadFileAsync(dlink, saveDlg.FileName,
                    percent => Dispatcher.BeginInvoke(() =>
                    {
                        uploadProgress.Value = percent;
                        uploadProgressText.Text = Framework.Lang.TF("下载中 {0}%", percent);
                    })));

            if (ok)
            {
                uploadProgressText.Text = Framework.Lang.T("下载完成");
                statusText.Text = Framework.Lang.TF("已保存: {0}", saveDlg.FileName);
            }
            else
            {
                uploadProgressText.Text = Framework.Lang.T("下载失败");
                statusText.Text = "";
                MessageBox.Show(Framework.Lang.T("下载失败，请稍后重试"), Framework.Lang.T("下载失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            await Task.Delay(3000);
            progressPanel.Visibility = Visibility.Collapsed;
            uploadProgressText.Text = "";
        }

        /// <summary>右键菜单 - 删除（带确认）</summary>
        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (fileListView.SelectedItem is not BaiduPanFile file) return;

            string typeText = file.IsFolder ? Framework.Lang.T("文件夹（含其下所有内容）") : Framework.Lang.T("文件");
            if (MessageBox.Show(Framework.Lang.TF("确定删除{0}\u201C{1}\u201D？此操作将移入回收站。", typeText, file.Name),
                Framework.Lang.T("删除确认"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            bool ok = await BaiduPanService.Instance.DeleteAsync(file.Path);
            if (ok)
                await LoadFilesAsync(updateQuota: true);
            else
                MessageBox.Show(Framework.Lang.T("删除失败，请稍后重试"), Framework.Lang.T("操作失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion
    }
}
