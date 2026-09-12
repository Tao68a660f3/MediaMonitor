using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MediaMonitor.Services
{
    public class UdpService : IMediaTransport
    {
        private UdpClient? _udpClient;
        private IPEndPoint? _remoteEndPoint;
        private CancellationTokenSource? _cts; // 控制异步循环退出的令牌

        private bool _isConnected;
        // 修改接口属性实现，由我们手动控制
        public bool IsConnected => _isConnected;

        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        public string RemoteIp { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; } = 8080;
        public int LocalPort { get; set; } = 8081;

        // --- 报错去重：链路异常时避免 10 次/秒刷屏（延迟测试期间尤其明显）---
        private string? _lastError;
        private long _lastErrorTicks;

        public void Connect()
        {
            try
            {
                Disconnect();
                // 获取 UI 最新的配置
                var cfg = App.ConfigSvc.Current;

                if (!TryBuildRemoteEndPoint(cfg.RemoteIp, cfg.RemotePort, out IPEndPoint? endPoint, out string reason))
                {
                    _isConnected = false;
                    ReportError($"UDP连接失败: {reason}");
                    return;
                }

                // 关键：用与"目标地址"一致的地址族建 socket，并按目标挑本地绑定地址。
                // 若固定用 IPv4 socket 去发 IPv6 目标，sendto 会报 10047/10049。
                // 目标是本机回环时，必须绑到 127.0.0.1 / ::1：
                //   某些 Windows 环境（多网卡、安全过滤驱动、Hyper-V/VMware 虚拟网卡等）不会把
                //   "发往 127.0.0.1 的报文"投递给绑在 0.0.0.0 的 socket，
                //   表现就是"对端明明收到了包、回包却永远收不到"（延迟测试直接 3 秒超时）。
                IPAddress localAddr = endPoint!.AddressFamily == AddressFamily.InterNetwork
                    ? (endPoint.Address.Equals(IPAddress.Loopback) ? IPAddress.Loopback : IPAddress.Any)
                    : (endPoint.Address.Equals(IPAddress.IPv6Loopback) ? IPAddress.IPv6Loopback : IPAddress.IPv6Any);

                _udpClient = new UdpClient(new IPEndPoint(localAddr, 0)); // 随机本地端口避免冲突

                // 允许发广播（255.255.255.255 或 x.x.x.255）：不开的话 sendto 同样是 10049。
                // 对普通单播目标没有任何副作用。
                if (endPoint.AddressFamily == AddressFamily.InterNetwork)
                    _udpClient.EnableBroadcast = true;

                _remoteEndPoint = endPoint;
                _isConnected = true; // 只有执行到这里才算真正成功

                _cts = new CancellationTokenSource();
                Task.Run(() => ReceiveLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                _isConnected = false;
                ReportError($"UDP连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建目标端点并做"可发送性"校验。
        /// 最常见的坑：把 0.0.0.0（本机监听地址）填成发送目标 → sendto 报 10049 地址无效。
        /// </summary>
        private static bool TryBuildRemoteEndPoint(string? ipText, int port, out IPEndPoint? endPoint, out string reason)
        {
            endPoint = null;
            reason = "";

            if (!IPAddress.TryParse((ipText ?? "").Trim(), out IPAddress? ip))
            {
                reason = $"远程 IP「{ipText}」不是合法地址（同机程序填 127.0.0.1，硬件填其局域网 IP）";
                return false;
            }

            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                reason = "远程 IP 不能是 0.0.0.0 / :: —— 那是【本机监听地址】，不能作为发送目标。" +
                         "同机运行的程序（如 A_tools/mock_esp32_stm32.py）请填 127.0.0.1，硬件请填它的局域网 IP";
                return false;
            }

            if (port is < 1 or > 65535)
            {
                reason = $"远程端口 {port} 非法（必须在 1~65535）";
                return false;
            }

            endPoint = new IPEndPoint(ip, port);
            return true;
        }

        public void Disconnect()
        {
            _isConnected = false; // 先切断状态
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient = null;
        }

        public void Send(byte[] data)
        {
            if (_udpClient == null || _remoteEndPoint == null)
            {
                // 旧实现在这里是静默 return：链路一旦"哑死"，用户完全看不到原因，
                // 「测延迟」也只会拿到一个 3 秒超时。现在改成明确报错。
                ReportError("UDP 未连接或链路已断开，数据被丢弃 —— 请重新点击『开始连接』");
                return;
            }

            try
            {
                _udpClient.Send(data, data.Length, _remoteEndPoint);
            }
            catch (SocketException ex)
            {
                // 带上目标端点与 SocketError 码，便于一眼定位（10049=地址无效 / 10051=网络不可达 / 10065=主机不可达）
                ReportError($"UDP 发送失败 -> {_remoteEndPoint} [{ex.SocketErrorCode}:{ex.ErrorCode}] {ex.Message}");
            }
            catch (Exception ex)
            {
                ReportError($"UDP 发送失败 -> {_remoteEndPoint}: {ex.Message}");
            }
        }

        /// <summary>同类错误 2 秒内只报一次，防止刷屏与日志框被冲掉</summary>
        private void ReportError(string msg)
        {
            long now = Environment.TickCount64;
            if (msg == _lastError && now - _lastErrorTicks < 2000)
                return;

            _lastError = msg;
            _lastErrorTicks = now;
            OnTransportError.Invoke(msg);
        }

        /// <summary>
        /// UDP 语义下"可以继续收发"的 SocketException：这些码基本都是对端 ICMP 通知
        /// （端口不可达/主机不可达等），收到后 socket 仍然可用，不应关闭链路。
        /// </summary>
        private static bool IsRecoverable(SocketError err) => err switch
        {
            SocketError.ConnectionReset => true,    // 10054：ICMP 端口不可达（对端没程序监听）
            SocketError.ConnectionRefused => true,  // 10061：对端拒绝
            SocketError.MessageSize => true,        // 10040：收到的报文超过缓冲
            SocketError.Interrupted => true,        // 10004：被信号打断
            SocketError.NetworkReset => true,       // 10052
            SocketError.HostUnreachable => true,    // 10065
            SocketError.NetworkUnreachable => true, // 10051
            _ => false
        };

        private async Task ReceiveLoop(CancellationToken token)
        {
            // 只要令牌没被取消，就一直运行
            while (!token.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    // 使用支持 CancellationToken 的 ReceiveAsync 版本
                    // 当 Disconnect() 被调用时，这里会立即抛出 OperationCanceledException 从而退出
                    var result = await _udpClient.ReceiveAsync(token);

                    if (result.Buffer.Length > 0)
                    {
                        OnRawDataReceived.Invoke(result.Buffer);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常的退出路径，由 Disconnect 发出信号
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // 物理连接已被销毁
                    break;
                }
                catch (SocketException ex) when (IsRecoverable(ex.SocketErrorCode))
                {
                    // UDP 语义下的"可恢复"异常。最常见的 10054 ConnectionReset 其实是：
                    // 目标端口没有程序监听时对方回 ICMP 端口不可达，Windows 在下一次 recv 时把它抛出来。
                    // 此时 socket 依然可用 —— 绝不能关闭并退出循环，否则整条链路会"哑死"：
                    // 界面仍显示已连接、但之后所有数据都被静默丢弃（测延迟只会得到一个 3 秒超时）。
                    ReportError($"UDP 收到 ICMP 端口不可达({(int)ex.SocketErrorCode})：目标 {_remoteEndPoint} " +
                                "没有程序在监听（对端可能未启动/已退出），本程序会继续尝试接收");
                    continue;
                }
                catch (Exception ex)
                {
                    // 只有在非取消状态下的异常才需要上报
                    if (!token.IsCancellationRequested)
                    {
                        ReportError($"UDP 接收异常: {ex.Message}");
                    }

                    // 致命错误：必须把连接状态也改掉，否则 IsConnected 会一直停在 true，
                    // 上层（按钮状态 / 延迟测试前置检查）会误判链路仍然可用。
                    _isConnected = false;
                    _udpClient?.Close();
                    _udpClient = null;
                    _cts?.Cancel();

                    break;
                }
            }
        }
    }
}