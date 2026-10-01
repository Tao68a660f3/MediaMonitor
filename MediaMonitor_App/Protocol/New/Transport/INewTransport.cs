using System;
using System.Threading.Tasks;

namespace MediaMonitor.Protocol.New.Transport
{
    /// <summary>
    /// 新协议的传输抽象（规范 §19）。
    /// 只管"收发字节流 + 断线通知"，完全不知道帧格式与协议语义。
    /// 实现：<see cref="TcpTransport"/>（ESP32 是 Server、PC 是 Client）、
    /// 以及后续的 ComTransport（USB CDC，包一层现有 SerialService）。
    /// </summary>
    public interface INewTransport
    {
        bool IsConnected { get; }

        /// <summary>发送一段字节（内部串行化，调用方通常是单线程的发送调度器）</summary>
        Task SendAsync(ReadOnlyMemory<byte> data);

        /// <summary>收到一段字节（可能是半帧 / 整帧 / 多帧粘包，交给解析器处理）</summary>
        event Action<ReadOnlyMemory<byte>>? DataReceived;

        /// <summary>链路断开（上层据此复位会话并重连）</summary>
        event Action<string>? Disconnected;

        void Close();
    }
}
