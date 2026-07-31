using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ChromeProtocol.Runtime.Messaging.WebSockets;

namespace SideDesk.Services;

/// <summary>
/// CDP 连接全局守卫：对同一调试端口，若已有连接尝试正在进行（Edge "是否允许远程调试" 弹窗尚未确认），
/// 则后续请求复用该进行中的尝试，而不新建连接，避免重复弹窗堆叠。
/// 连接尝试完成（成功或失败）后即从登记表移除，下次请求才会重新发起。
/// 该守卫跨 CdpBrowserService / DataPollingService 共享，是全应用唯一的建连去重点。
/// </summary>
public static class CdpConnectGuard
{
    // 端口 -> 正在进行中的连接尝试
    private static readonly Dictionary<int, Task<DefaultProtocolClient?>> _inflight = new();
    private static readonly object _lock = new();

    /// <summary>
    /// 连接到指定端口的浏览器 CDP。若该端口已有进行中的连接尝试，直接复用其结果（不新建连接、不重复弹窗）。
    /// </summary>
    /// <param name="port">浏览器调试端口</param>
    /// <param name="connectFunc">真正建立连接的委托（仅在无进行中尝试时调用）</param>
    public static Task<DefaultProtocolClient?> ConnectAsync(int port, Func<Task<DefaultProtocolClient?>> connectFunc)
    {
        lock (_lock)
        {
            if (_inflight.TryGetValue(port, out var existing))
            {
                Debug.WriteLine($"[CdpGuard] 端口 {port} 已有连接尝试进行中，复用（不重复弹窗）");
                return existing;
            }

            var task = RunAndCleanupAsync(port, connectFunc);
            _inflight[port] = task;
            return task;
        }
    }

    private static async Task<DefaultProtocolClient?> RunAndCleanupAsync(int port, Func<Task<DefaultProtocolClient?>> connectFunc)
    {
        try
        {
            return await connectFunc();
        }
        finally
        {
            lock (_lock) { _inflight.Remove(port); }
        }
    }
}
