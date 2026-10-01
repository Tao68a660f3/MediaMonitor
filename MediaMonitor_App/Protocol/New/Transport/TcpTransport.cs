using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MediaMonitor.Protocol.New.Transport
{
    /// <summary>
    /// 新协议的 TCP 传输：**ESP32 做 Server、PC 做 Client**（规范 §19.2）。
    ///
    /// * <see cref="ConnectAsync"/> 只负责"连一次"，重连退避由上层（协议栈）决定并调用；
    /// * 接收在独立任务里跑，收到的每一段都**复制后**再抛出（缓冲会复用）；
    /// * 发送由发送调度器单线程驱动，这里只做异常上报。
    /// </summary>
    public sealed class TcpTransport : INewTransport, IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly object _gate = new object();

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;

        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<string>? Disconnected;

        public TcpTransport(string host, int port)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
        }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return (_client != null) && (_stream != null) && _client.Connected;
                }
            }
        }

        /// <summary>连接一次；成功返回 true。超时/拒绝都只返回 false，由上层决定是否重试。</summary>
        public async Task<bool> ConnectAsync(int timeoutMs = 3000)
        {
            Close();

            var client = new TcpClient { NoDelay = true };   // 实时流禁用 Nagle，降低小帧延迟
            using var timeout = new CancellationTokenSource(Math.Max(200, timeoutMs));

            try
            {
                await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                client.Dispose();
                return false;
            }

            lock (_gate)
            {
                _client = client;
                _stream = client.GetStream();
                _cts = new CancellationTokenSource();
            }

            _ = Task.Run(() => ReceiveLoopAsync(client, _cts!.Token));
            return true;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data)
        {
            NetworkStream? stream;
            lock (_gate)
            {
                stream = _stream;
            }
            if (stream == null || data.Length == 0)
            {
                return;
            }

            try
            {
                await stream.WriteAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RaiseDisconnected($"TCP 发送失败: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken token)
        {
            byte[] buffer = new byte[4096];

            try
            {
                NetworkStream stream = client.GetStream();
                while (!token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        RaiseDisconnected("TCP 对端已关闭连接");
                        return;
                    }

                    // 必须复制：buffer 会被下一轮覆盖，而回调可能异步处理
                    byte[] chunk = new byte[n];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                    DataReceived?.Invoke(chunk);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                RaiseDisconnected($"TCP 接收失败: {ex.Message}");
            }
        }

        private void RaiseDisconnected(string reason)
        {
            lock (_gate)
            {
                _stream = null;
                _client?.Dispose();
                _client = null;
            }
            Disconnected?.Invoke(reason);
        }

        public void Close()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _stream?.Dispose();
                _stream = null;

                _client?.Dispose();
                _client = null;
            }
        }

        public void Dispose() => Close();
    }
}
