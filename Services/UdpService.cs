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

        public bool IsConnected => _udpClient != null;

        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        public string RemoteIp { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; } = 8080;
        public int LocalPort { get; set; } = 8081;

        public void Connect()
        {
            try
            {
                Disconnect(); // 确保先清理旧资源

                _udpClient = new UdpClient(LocalPort);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(RemoteIp), RemotePort);
                _cts = new CancellationTokenSource(); // 初始化新的取消令牌

                // 将令牌传递给异步接收循环
                Task.Run(() => ReceiveLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                OnTransportError.Invoke($"UDP 初始化失败: {ex.Message}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            // 1. 发送取消信号，通知 ReceiveLoop 停止
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // 2. 释放底层 UDP 资源
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }

        public void Send(byte[] data)
        {
            if (_udpClient == null || _remoteEndPoint == null)
                return;

            try
            {
                _udpClient.Send(data, data.Length, _remoteEndPoint);
            }
            catch (Exception ex)
            {
                OnTransportError.Invoke($"UDP 发送失败: {ex.Message}");
            }
        }

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
                catch (Exception ex)
                {
                    // 只有在非取消状态下的异常才需要上报
                    if (!token.IsCancellationRequested)
                    {
                        OnTransportError.Invoke($"UDP 接收异常: {ex.Message}");
                    }

                    // === 添加以下三行，清理连接状态 ===
                    _udpClient?.Close();
                    _udpClient = null;
                    _cts?.Cancel();

                    break;
                }
            }
        }
    }
}