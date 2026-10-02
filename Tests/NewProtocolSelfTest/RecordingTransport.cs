using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaMonitor.Protocol.New.Transport;

namespace NewProtocolSelfTest
{
    /// <summary>
    /// 只记录不发送的传输（自测用）：把 <c>SendAsync</c> 的字节按顺序攒起来，
    /// 供"发出去的帧长什么样"这类断言使用（例如 §5.6 约定表里的 FLAGS 检查）。
    /// </summary>
    internal sealed class RecordingTransport : INewTransport
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _sent = new List<byte[]>();

        public bool IsConnected => true;

        // 本测试不注入链路事件（空 add/remove 顺带避开 CS0067“事件从未使用”警告）
        public event Action<ReadOnlyMemory<byte>>? DataReceived { add { } remove { } }
        public event Action<string>? Disconnected { add { } remove { } }

        /// <summary>已发送的帧字节（按发送顺序）</summary>
        public IReadOnlyList<byte[]> Sent
        {
            get { lock (_gate) { return _sent.ToArray(); } }
        }

        public Task SendAsync(ReadOnlyMemory<byte> data)
        {
            lock (_gate)
            {
                _sent.Add(data.ToArray());
            }
            return Task.CompletedTask;
        }

        public void Close()
        {
        }
    }
}
