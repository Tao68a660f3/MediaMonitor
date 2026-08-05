using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MediaMonitor.Services
{
    public class TransportManager : IMediaTransport
    {
        // 内部持有的真实引擎，初始为 null
        private IMediaTransport? _activeTransport;
        private readonly object _transportLock = new object();

        // --- 发送队列：业务层入队即返回，后台线程按节奏真实发送 ---
        private readonly BlockingCollection<byte[]> _sendQueue = new BlockingCollection<byte[]>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // 只有当真正挂载了引擎且引擎连接时，才返回 true
        public bool IsConnected => _activeTransport?.IsConnected ?? false;

        // 外部（Master）订阅这些事件，我们通过“中转”来发射
        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        public TransportManager()
        {
            // 常驻后台消费线程：取包 → 发送 → 按配置间隔节流
            Task.Run(SenderLoop);
        }

        public void SetTransport(IMediaTransport newTransport)
        {
            lock (_transportLock)
            {
                // 0. 切换传输时清空积压队列，避免旧连接的数据发到新连接
                while (_sendQueue.TryTake(out _)) { }

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
        }

        // --- 业务层调用不变：立即入队返回，不阻塞调用线程 ---
        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;
            try
            {
                _sendQueue.TryAdd(data);
            }
            catch (InvalidOperationException)
            {
                // 队列已被标记为完成（理论不会发生，防御性处理）
            }
        }

        public void Connect() => _activeTransport?.Connect();
        public void Disconnect() => _activeTransport?.Disconnect();

        // 转发底层信号
        private void HandleRawData(byte[] data) => OnRawDataReceived?.Invoke(data);
        private void HandleError(string msg) => OnTransportError?.Invoke(msg);

        // --- 后台消费循环 ---
        private async Task SenderLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // 阻塞等待：队列空时挂起，不空转
                    byte[] data = _sendQueue.Take(_cts.Token);

                    IMediaTransport? transport;
                    lock (_transportLock)
                    {
                        transport = _activeTransport;
                    }

                    if (transport != null && transport.IsConnected)
                    {
                        transport.Send(data);

                        // 协议日志移到“实际发出时”记录，保证日志时序与真实发送一致
                        var enc = App.ConfigSvc?.Current?.Encoding ?? System.Text.Encoding.UTF8;
                        App.LogSvc?.LogProtocol(data, enc);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Transport] 发送队列异常: {ex.Message}");
                }

                // Pacing：每发完一包强制延时，抹平突发流量峰值
                try
                {
                    await Task.Delay(GetSendInterval(), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // 从配置读取每包间隔，Clamp 0~200ms（0 表示不节流）
        private int GetSendInterval()
        {
            try
            {
                return Math.Clamp(App.ConfigSvc?.Current?.SendIntervalMs ?? 10, 0, 200);
            }
            catch
            {
                return 10;
            }
        }
    }
}