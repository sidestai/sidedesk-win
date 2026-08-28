using System;
using System.Collections.Generic;
using WindowsDesktop;

namespace SideDesk.Services
{
    /// <summary>
    /// Windows 虚拟桌面服务：检测当前桌面、枚举所有桌面、监听桌面切换。
    /// 基于 Slions.VirtualDesktop NuGet 包实现，不支持的系统优雅降级。
    /// </summary>
    public class VirtualDesktopService : IDisposable
    {
        private bool _disposed;

        #region Public API

        /// <summary>桌面切换事件，参数为新的桌面 ID</summary>
        public event Action<Guid>? DesktopSwitched;

        /// <summary>是否支持虚拟桌面</summary>
        public bool IsAvailable { get; private set; }

        public VirtualDesktopService()
        {
            try
            {
                IsAvailable = VirtualDesktop.IsSupported;
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] IsSupported={IsAvailable}");

                if (IsAvailable)
                {
                    // 触发内部初始化：访问 Current 确保 COM 连接已建立
                    var _ = VirtualDesktop.Current;

                    // 订阅桌面切换事件
                    VirtualDesktop.CurrentChanged += OnCurrentChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] init failed: {ex.Message}");
                IsAvailable = false;
            }
        }

        /// <summary>获取当前活动虚拟桌面 ID</summary>
        public Guid GetCurrentDesktopId()
        {
            if (!IsAvailable) return Guid.Empty;
            try
            {
                return VirtualDesktop.Current.Id;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] GetCurrentDesktopId failed: {ex.Message}");
                return Guid.Empty;
            }
        }

        /// <summary>获取所有虚拟桌面 ID</summary>
        public List<Guid> GetAllDesktopIds()
        {
            var result = new List<Guid>();
            if (!IsAvailable) return result;
            try
            {
                foreach (var desktop in VirtualDesktop.GetDesktops())
                    result.Add(desktop.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] GetAllDesktopIds failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>获取虚拟桌面数量</summary>
        public int GetDesktopCount()
        {
            if (!IsAvailable) return 1;
            try
            {
                return VirtualDesktop.GetDesktops().Length;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>停止监听，释放资源</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                VirtualDesktop.CurrentChanged -= OnCurrentChanged;
            }
            catch { }
        }

        #endregion

        #region Private

        private void OnCurrentChanged(object? sender, VirtualDesktopChangedEventArgs e)
        {
            if (_disposed) return;
            try
            {
                var newId = e.NewDesktop?.Id ?? Guid.Empty;
                if (newId != Guid.Empty)
                {
                    System.Diagnostics.Debug.WriteLine($"[VirtualDesktop] desktop switched: {e.OldDesktop?.Id} -> {newId}");
                    DesktopSwitched?.Invoke(newId);
                }
            }
            catch { }
        }

        #endregion
    }
}
