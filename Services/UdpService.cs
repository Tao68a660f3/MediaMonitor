using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MediaMonitor.Services
{
    public class UdpService : IMediaTransport
    {
        private UdpClient? _udpClient;
        private IPEndPoint? _remoteEndPoint;
        private bool _isListening;

        // 实现接口属性
        public bool IsConnected => _udpClient != null;

        // 实现接口事件
        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        // UDP 特有配置：目标 IP 和 端口
        public string RemoteIp { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; } = 8080;
        public int LocalPort { get; set; } = 8081;

        public void Connect()
        {
            try
            {
                Disconnect(); // 确保先清理旧连接

                _udpClient = new UdpClient(LocalPort);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(RemoteIp), RemotePort);

                _isListening = true;
                // 开启异步监听线程
                Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                OnTransportError.Invoke($"UDP 初始化失败: {ex.Message}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _isListening = false;
            _udpClient?.Close();
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

        private async Task ReceiveLoop()
        {
            while (_isListening && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    if (result.Buffer.Length > 0)
                    {
                        // 抛给 PackageHelper
                        OnRawDataReceived.Invoke(result.Buffer);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 正常关闭，跳出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (_isListening)
                    {
                        OnTransportError.Invoke($"UDP 接收异常: {ex.Message}");
                    }
                }
            }
        }
    }
}