using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SideDesk.Models;
using SideDesk.Services;

namespace SideDesk;

/// <summary>添加云文件对话框：两种方式——「来自云盘」（选择已配置的云盘，可从云盘浏览多选文件）或「文件ID」（他人分享的网络文件ID）</summary>
public partial class AddCloudFileDialog : Window
{
    /// <summary>对话框确认后的结果列表（未确认为 null）</summary>
    public List<FileAreaItem>? Results { get; private set; }

    public AddCloudFileDialog()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => App.UpdateWindowDwmTheme(this);
        LoadCloudSpaces();
    }

    private void LoadCloudSpaces()
    {
        cloudSpaceCombo.Items.Clear();
        cloudSpaceCombo.Items.Add(new ComboBoxItem { Content = Framework.Lang.T("cloudDrive.selectDrivePlaceholder"), Tag = null });
        int builtinIndex = -1;
        int index = 1;
        foreach (var space in CloudSpaceManager.GetAll())
        {
            cloudSpaceCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{space.Icon} {CloudSpaceManager.GetDisplayName(space)}",
                Tag = space
            });
            // 默认选中内置云空间（元桌云盘）
            if (builtinIndex < 0 && space.IsBuiltin) builtinIndex = index;
            index++;
        }
        cloudSpaceCombo.SelectedIndex = builtinIndex >= 0 ? builtinIndex : 0;
    }

    /// <summary>当前选中的云空间</summary>
    private CloudSpace? SelectedSpace => (cloudSpaceCombo.SelectedItem as ComboBoxItem)?.Tag as CloudSpace;

    private static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
     || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>取ID尾部摘要用于自动命名</summary>
    private static string ShortId(string id) => id.Length > 12 ? id[^10..] : id;

    /// <summary>从所选云盘浏览并勾选文件（多选），确认后批量导入</summary>
    private void PickFromDrive_Click(object sender, RoutedEventArgs e)
    {
        var space = SelectedSpace;
        if (space == null)
        {
            MessageBox.Show(Framework.Lang.T("cloudDrive.pleaseSelectDriveFirst"), Framework.Lang.T("cloudDrive.cannotSelect"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 百度网盘：打开百度网盘窗口浏览
        if (space.Name == "百度网盘")
        {
            new BaiduPanWindow().Show();
            return;
        }

        // 外部存储源（S3 / WebDAV）：用通用选择器浏览多选，客户端直连无需登录
        var externalBackend = DriveBackends.Resolve(space);
        if (externalBackend != null)
        {
            var extPicker = new CloudDrivePickerWindow(externalBackend) { Owner = this };
            if (extPicker.ShowDialog() != true || extPicker.SelectedFiles is not { Count: > 0 } extSelected)
                return;

            Results = extSelected.Select(f => BuildFromDriveFile(space, f, external: true)).ToList();
            DialogResult = true;
            Close();
            return;
        }

        if (!space.IsBuiltin)
        {
            MessageBox.Show(Framework.Lang.T("cloudDrive.browseOnlyBuiltIn"), Framework.Lang.T("common.notice"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new CloudDrivePickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedFiles is not { Count: > 0 } selected)
            return;

        // 将勾选的云盘文件/目录批量转为 FileAreaItem
        Results = selected.Select(f => BuildFromDriveFile(space, f)).ToList();
        DialogResult = true;
        Close();
    }

    /// <summary>云盘选中项 → FileAreaItem（external=true 表示外部存储源：打开时走各自后端的认证下载）</summary>
    private static FileAreaItem BuildFromDriveFile(CloudSpace space, CloudDriveFile file, bool external = false)
    {
        // 元桌云盘桶为私有 ACL，不存匿名直链快照：Path 存对象键（打开时据此签名下载，
        // 编辑别名对话框可显示/复制），CloudUrl 保持 null
        string? downloadUrl = external ? null : CloudDriveService.Instance.BuildDownloadUrl(file);
        return new FileAreaItem
        {
            Name = file.Name,
            Path = external ? "" : (file.OssKey ?? downloadUrl ?? ""),
            IsFolder = file.IsFolder,
            IsCloudFile = true,
            CloudSpaceId = space.Id,
            CloudSpaceName = space.Name,
            CloudSpaceIcon = space.Icon,
            CloudFileId = file.Id,
            CloudUrl = downloadUrl
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var single = modeTabs.SelectedIndex == 0 ? BuildFromCloudSpace() : BuildFromNetFileId();
        if (single == null) return;

        Results = new List<FileAreaItem> { single };
        DialogResult = true;
        Close();
    }

    /// <summary>方式一：来自云盘（必选云盘，指向云盘主页；具体文件请用“从云盘选择文件”）</summary>
    private FileAreaItem? BuildFromCloudSpace()
    {
        var space = SelectedSpace;
        if (space == null)
        {
            MessageBox.Show(Framework.Lang.T("cloudDrive.pleaseSelectDrive"), Framework.Lang.T("cloudDrive.cannotImport"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        string name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = space.Name;

        return new FileAreaItem
        {
            Name = name,
            Path = space.Url, // 指向云盘主页
            IsFolder = false,
            IsCloudFile = true,
            CloudSpaceId = space.Id,
            CloudSpaceName = space.Name,
            CloudSpaceIcon = space.Icon,
            CloudFileId = null,
            CloudUrl = space.Url
        };
    }

    /// <summary>方式二：文件ID（他人分享的网络文件，必填ID）</summary>
    private FileAreaItem? BuildFromNetFileId()
    {
        string fileId = netFileIdBox.Text.Trim();
        if (string.IsNullOrEmpty(fileId))
        {
            MessageBox.Show(Framework.Lang.T("cloudDrive.pleaseEnterACloud"), Framework.Lang.T("cloudDrive.cannotImport"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        // 是网址则可直接打开，否则点击时复制ID到剪贴板
        string openUrl = IsUrl(fileId) ? fileId : "";

        string name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
            name = Framework.Lang.TF("cloudDrive.networkFileNamed", ShortId(fileId));

        return new FileAreaItem
        {
            Name = name,
            Path = fileId, // 非网址时 Path 存放原始ID
            IsFolder = false,
            IsCloudFile = true,
            CloudSpaceId = null,
            CloudSpaceName = null,
            CloudSpaceIcon = "🌐",
            CloudFileId = fileId,
            CloudUrl = string.IsNullOrEmpty(openUrl) ? null : openUrl
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
