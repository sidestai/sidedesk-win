using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace SideDesk.Services
{
    /// <summary>
    /// 设备唯一标识服务：基于 CPU ID + MAC 地址计算机器指纹。
    /// 值在进程内只计算一次并缓存（线程安全）；硬件信息读取失败时使用回退策略，保证始终返回非空 ID。
    /// </summary>
    public static class DeviceIdService
    {
        private static readonly Lazy<string> _deviceId = new(BuildDeviceId);

        /// <summary>设备唯一 ID（CPU ID + MAC 地址的 SHA256 十六进制小写串，64 位）</summary>
        public static string DeviceId => _deviceId.Value;

        /// <summary>组合硬件信息并哈希为固定长度的设备指纹</summary>
        private static string BuildDeviceId()
        {
            string cpu = GetCpuId();
            string mac = GetMacAddress();
            string raw = $"{cpu}|{mac}";

            // 两项硬件信息都取不到时，退化为机器名，保证 ID 非空
            if (string.IsNullOrEmpty(cpu) && string.IsNullOrEmpty(mac))
                raw = Environment.MachineName;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var id = Convert.ToHexString(hash).ToLowerInvariant();
            Debug.WriteLine($"[DeviceId] 设备唯一 ID: {id} (cpu={cpu}, mac={mac})");
            return id;
        }

        /// <summary>读取 CPU ID（WMI: Win32_Processor.ProcessorId）</summary>
        private static string GetCpuId()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    var id = obj["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DeviceId] 读取 CPU ID 失败: {ex.Message}");
            }
            return "";
        }

        /// <summary>读取物理网卡 MAC 地址（取第一块已启用的非回环网卡，按名称排序保证稳定）</summary>
        private static string GetMacAddress()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.GetPhysicalAddress().GetAddressBytes().Length > 0)
                    .OrderBy(n => n.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                return nic?.GetPhysicalAddress().ToString() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DeviceId] 读取 MAC 地址失败: {ex.Message}");
            }
            return "";
        }
    }
}
