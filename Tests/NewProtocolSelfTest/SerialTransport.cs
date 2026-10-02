using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using MediaMonitor.Protocol.New.Transport;

namespace NewProtocolSelfTest
{
    /// <summary>
    /// 自测工具专用的极简串口传输（COM 端到端模式）。
    ///
    /// 为什么不用应用里的 <c>ComTransport</c>：它包着 <c>SerialService</c>，而后者依赖 WPF 的 <c>App</c> 单例，
    /// 本测试工程刻意不引入应用层。串口参数与上位机一致（8N1、无流控、DTR/RTS 关闭、115200），
    /// 因此对端（<c>mock_esp32_new.py --transport com</c>）看到的字节流完全一样。
    /// </summary>
    internal sealed class SerialTransport : INewTransport, IDisposable
    {
        private readonly SerialPort _port = new SerialPort();
        private Thread? _reader;
        private volatile bool _running;

        public event Action<ReadOnlyMemory<byte>>? DataReceived;
        public event Action<string>? Disconnected;

        public bool IsConnected => _port.IsOpen;

        public bool Connect(string portName, int baudRate, out string? error)
        {
            try
            {
                _port.PortName = portName;
                _port.BaudRate = baudRate;
                _port.Parity = Parity.None;
                _port.DataBits = 8;
                _port.StopBits = StopBits.One;
                _port.Handshake = Handshake.None;
                _port.DtrEnable = false;
                _port.RtsEnable = false;
                _port.ReadTimeout = 100;            // 读超时 → 由线程循环轮询（不需要事件回调）
                _port.WriteTimeout = 500;

                _port.Open();

                _running = true;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "np-com-reader" };
                _reader.Start();

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];

            while (_running)
            {
                try
                {
                    int n = _port.Read(buf, 0, buf.Length);
                    if (n > 0)
                    {
                        DataReceived?.Invoke(new ReadOnlyMemory<byte>(buf, 0, n).ToArray());
                    }
                }
                catch (TimeoutException)
                {
                    // 正常：这一段没数据
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _running = false;
                        Disconnected?.Invoke($"串口读失败：{ex.Message}");
                    }
                    return;
                }
            }
        }

        public Task SendAsync(ReadOnlyMemory<byte> data)
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Write(data.ToArray(), 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke($"串口写失败：{ex.Message}");
            }
            return Task.CompletedTask;
        }

        public void Close()
        {
            _running = false;
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch
            {
                // 关闭失败无需处理（进程即将退出）
            }
        }

        public void Dispose() => Close();
    }
}
