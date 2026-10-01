using System;
using System.Threading.Tasks;
using MediaMonitor.Services;

namespace MediaMonitor.Protocol.New.Transport
{
    /// <summary>
    /// 新协议的 COM 传输（规范 §19.3）。
    ///
    /// USB CDC 在 PC 侧就是一个普通 COM 口，因此这里**直接复用现有 <see cref="SerialService"/>**
    /// 的连接 / 收发 / 错误去重能力，只把它适配成 <see cref="INewTransport"/>：
    /// 协议栈只管字节流，不知道底层是串口还是 TCP。
    /// </summary>
    public sealed class ComTransport : INewTransport, IDisposable
    {
        private readonly SerialService _serial = new SerialService();

        private bool _connected;

        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<string>? Disconnected;

        /// <summary>最近一次链路错误（连接失败时由 <see cref="Connect"/> 带出）</summary>
        public string LastError { get; private set; } = "";

        public ComTransport()
        {
            _serial.OnRawDataReceived += data => DataReceived?.Invoke(data);

            _serial.OnTransportError += msg =>
            {
                LastError = msg;
                // 只有"曾经连上过"才当成断线事件；连接期错误由 Connect() 的返回值表达
                if (_connected)
                {
                    _connected = false;
                    Disconnected?.Invoke(msg);
                }
            };
        }

        public bool IsConnected => _connected && _serial.IsConnected;

        /// <summary>打开串口（同步，UI 线程可接受：Open 本身很快）</summary>
        public bool Connect(string portName, int baudRate, out string? error)
        {
            _serial.Connect(portName, baudRate);

            _connected = _serial.IsConnected;
            error = _connected ? null : (string.IsNullOrEmpty(LastError) ? $"串口 {portName} 打开失败" : LastError);

            if (_connected)
            {
                LastError = "";
            }
            return _connected;
        }

        /// <summary>
        /// 发送：<c>SerialPort.Write</c> 是阻塞调用，但本方法运行在发送调度器的后台线程上，
        /// 因此这里直接同步写（与 Legacy 的发送路径行为一致）。
        /// </summary>
        public Task SendAsync(ReadOnlyMemory<byte> data)
        {
            _serial.Send(data.ToArray());
            return Task.CompletedTask;
        }

        public void Close()
        {
            _connected = false;
            _serial.Disconnect();
        }

        public void Dispose() => Close();
    }
}
