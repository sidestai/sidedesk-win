using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SideDesk.Models;

namespace SideDesk.Services
{
    /// <summary>
    /// 云盘驱动后端抽象：元桌云盘（服务端）与 S3 存储源共用同一套文件管理语义，
    /// 使云盘窗口可在不同存储源之间无差别切换。
    /// 文件夹/文件统一用 CloudDriveFile 表示，Id 语义由后端自定义（元桌=服务端记录 ID，S3=对象 key/前缀）。
    /// </summary>
    public interface IDriveBackend
    {
        /// <summary>根目录 ID（元桌为 "0"，S3 为配置的根前缀）</summary>
        string RootId { get; }
        /// <summary>是否要求元桌账号登录（S3 直连不需要）</summary>
        bool RequiresLogin { get; }
        Task<List<CloudDriveFile>?> ListFilesAsync(string parentId);
        Task<bool> UploadFileAsync(string localPath, string parentId, Action<double>? progress);
        Task<CloudDriveFile?> MkdirAsync(string parentId, string name);
        Task<bool> DeleteAsync(CloudDriveFile file);
        Task<long> GetTotalUsedBytesAsync();
        /// <summary>可直接用浏览器打开的下载地址（无则返回 null，走本地保存流程）</summary>
        string? BuildOpenUrl(CloudDriveFile file);
        /// <summary>下载文件到本地路径（进度回调）</summary>
        Task<bool> DownloadToFileAsync(CloudDriveFile file, string savePath, Action<double>? progress);
        /// <summary>上传前预检目标目录可写性：返回 null 放行；非 null 为拦截原因（已本地化，可直接展示）</summary>
        Task<string?> ProbeWritableAsync(string parentId);
        /// <summary>最近一次 UploadFileAsync 失败的原因（成功或从未失败为 null）</summary>
        string? LastUploadError { get; }
    }

    /// <summary>元桌云盘后端：包装现有 CloudDriveService（STS + OSS 直传 + 服务端入库）</summary>
    public sealed class MetadeskDriveBackend : IDriveBackend
    {
        public string RootId => "0";
        public bool RequiresLogin => true;

        public Task<List<CloudDriveFile>?> ListFilesAsync(string parentId)
            => CloudDriveService.Instance.ListFilesAsync(parentId);

        public Task<bool> UploadFileAsync(string localPath, string parentId, Action<double>? progress)
            => CloudDriveService.Instance.UploadFileAsync(localPath, parentId, progress);

        public Task<CloudDriveFile?> MkdirAsync(string parentId, string name)
            => CloudDriveService.Instance.MkdirAsync(parentId, name);

        public Task<bool> DeleteAsync(CloudDriveFile file)
            => CloudDriveService.Instance.DeleteAsync(file.Id);

        public Task<long> GetTotalUsedBytesAsync()
            => CloudDriveService.Instance.GetTotalUsedBytesAsync();

        /// <summary>元桌云盘桶为私有 ACL，不提供匿名直链，打开/下载一律走 STS 签名 GET</summary>
        public string? BuildOpenUrl(CloudDriveFile file) => null;

        public async Task<bool> DownloadToFileAsync(CloudDriveFile file, string savePath, Action<double>? progress)
        {
            // 私有桶：按文件记录 ID 向后端请求预签名直链下载（file.Id 为加密记录 id）
            if (file.IsFolder || string.IsNullOrEmpty(file.Id)) return false;
            return await CloudDriveService.Instance.DownloadViaPresignedUrlAsync(file.Id, savePath, progress).ConfigureAwait(false);
        }

        public string? LastUploadError => CloudDriveService.Instance.LastUploadError;

        public Task<string?> ProbeWritableAsync(string parentId)
            => CloudDriveService.Instance.ProbeUploadAsync();
    }

    /// <summary>S3 存储源后端：客户端直连用户自己的 S3 桶（无需元桌登录）</summary>
    public sealed class S3DriveBackend : IDriveBackend
    {
        private readonly S3StorageService _s3;

        public S3Source Source { get; }

        public S3DriveBackend(S3Source source)
        {
            Source = source;
            _s3 = new S3StorageService(source);
        }

        public string RootId => Source.NormalizedPrefix;
        public bool RequiresLogin => false;

        public Task<List<CloudDriveFile>?> ListFilesAsync(string parentId)
            => _s3.ListDirectoryAsync(parentId);

        public Task<bool> UploadFileAsync(string localPath, string parentId, Action<double>? progress)
            => _s3.UploadFileAsync(localPath, parentId, progress);

        public async Task<CloudDriveFile?> MkdirAsync(string parentId, string name)
        {
            string trimmed = name.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            bool ok = await _s3.CreateFolderAsync(parentId, trimmed).ConfigureAwait(false);
            if (!ok) return null;
            return new CloudDriveFile { Id = parentId + trimmed.TrimEnd('/') + "/", Name = trimmed, IsFolder = true };
        }

        public Task<bool> DeleteAsync(CloudDriveFile file)
            => file.IsFolder ? _s3.DeletePrefixAsync(file.Id) : _s3.DeleteObjectAsync(file.Id);

        public Task<long> GetTotalUsedBytesAsync()
            => _s3.GetTotalUsedBytesAsync(Source.NormalizedPrefix);

        /// <summary>S3 桶为私有访问，一律走客户端认证下载，不提供公开直链</summary>
        public string? BuildOpenUrl(CloudDriveFile file) => null;

        public Task<bool> DownloadToFileAsync(CloudDriveFile file, string savePath, Action<double>? progress)
            => _s3.DownloadToFileAsync(file.Id, savePath, progress);

        public string? LastUploadError => _s3.LastUploadError;

        public Task<string?> ProbeWritableAsync(string parentId)
            => _s3.ProbeWritableAsync(parentId);
    }

    /// <summary>WebDAV 存储源后端：客户端直连标准 WebDAV 服务器（无需元桌登录）</summary>
    public sealed class WebDavDriveBackend : IDriveBackend
    {
        private readonly WebDavStorageService _dav;

        public WebDavSource Source { get; }

        public WebDavDriveBackend(WebDavSource source)
        {
            Source = source;
            _dav = new WebDavStorageService(source);
        }

        public string RootId => "";
        public bool RequiresLogin => false;

        public Task<List<CloudDriveFile>?> ListFilesAsync(string parentId)
            => _dav.ListDirectoryAsync(parentId);

        public Task<bool> UploadFileAsync(string localPath, string parentId, Action<double>? progress)
            => _dav.UploadFileAsync(localPath, parentId, progress);

        public async Task<CloudDriveFile?> MkdirAsync(string parentId, string name)
        {
            string trimmed = name.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            bool ok = await _dav.CreateFolderAsync(parentId, trimmed).ConfigureAwait(false);
            if (!ok) return null;
            return new CloudDriveFile { Id = parentId + trimmed.TrimEnd('/') + "/", Name = trimmed, IsFolder = true };
        }

        /// <summary>WebDAV 的 DELETE 集合即递归删除，文件/文件夹同一接口</summary>
        public Task<bool> DeleteAsync(CloudDriveFile file)
            => _dav.DeleteAsync(file.Id);

        public Task<long> GetTotalUsedBytesAsync()
            => _dav.GetTotalUsedBytesAsync();

        /// <summary>需认证的 WebDAV 资源一律走客户端认证下载，不提供公开直链</summary>
        public string? BuildOpenUrl(CloudDriveFile file) => null;

        public Task<bool> DownloadToFileAsync(CloudDriveFile file, string savePath, Action<double>? progress)
            => _dav.DownloadToFileAsync(file.Id, savePath, progress);

        public string? LastUploadError => _dav.LastUploadError;

        public Task<string?> ProbeWritableAsync(string parentId)
            => _dav.ProbeWritableAsync(parentId);
    }

    /// <summary>存储后端解析器：按云空间配置查找外部存储后端（S3 / WebDAV）；
    /// 返回 null 表示非外部存储源（内置元桌云盘或无法解析）</summary>
    public static class DriveBackends
    {
        public static IDriveBackend? Resolve(CloudSpace? space)
        {
            if (space == null || string.IsNullOrEmpty(space.RefId)) return null;
            if (space.RefType == "webdav")
            {
                var dav = WebDavSourceManager.Find(space.RefId);
                return dav == null ? null : new WebDavDriveBackend(dav);
            }
            var s3 = S3SourceManager.Find(space.RefId);
            return s3 == null ? null : new S3DriveBackend(s3);
        }

        public static IDriveBackend? ResolveBySpaceId(string? spaceId)
        {
            if (string.IsNullOrEmpty(spaceId)) return null;
            return Resolve(CloudSpaceManager.GetAll().Find(sp => sp.Id == spaceId));
        }
    }
}
