using System;

namespace MediaMonitor.Services
{
    public class TransportManager : IMediaTransport
    {
        // 内部持有的真实引擎，初始为 null
        private IMediaTransport? _activeTransport;

        // 只有当真正挂载了引擎且引擎连接时，才返回 true
        public bool IsConnected => _activeTransport?.IsConnected ?? false;

        // 外部（Master）订阅这些事件，我们通过“中转”来发射
        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        // --- 核心切换逻辑 ---
        public void SetTransport(IMediaTransport newTransport)
        {
            // 1. 彻底清理旧引擎（如果有）
            if (_activeTransport != null)
            {
                _activeTransport.Disconnect();
                _activeTransport.OnRawDataReceived -= HandleRawData;
                _activeTransport.OnTransportError -= HandleError;
            }

            // 2. 换上新引擎并绑定信号
            _activeTransport = newTransport;
            _activeTransport.OnRawDataReceived += HandleRawData;
            _activeTransport.OnTransportError += HandleError;
        }

        // --- 健壮的转发操作 ---
        public void Send(byte[] data)
        {
            // 如果还没选模式（_activeTransport 为空），这里就静默跳过，不会报错
            if (_activeTransport != null && _activeTransport.IsConnected)
            {
                _activeTransport.Send(data);
            }
        }

        public void Connect() => _activeTransport?.Connect();
        public void Disconnect() => _activeTransport?.Disconnect();

        // 转发底层信号
        private void HandleRawData(byte[] data) => OnRawDataReceived?.Invoke(data);
        private void HandleError(string msg) => OnTransportError?.Invoke(msg);
    }
}