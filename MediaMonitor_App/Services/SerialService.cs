using System;
using System.IO.Ports;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading; // 需要引用 WindowsBase

namespace MediaMonitor.Services
{
    public class SerialService : IMediaTransport
    {
        private readonly SerialPort _port = new SerialPort();

        // --- 新增：用于自动轮询的定时器 ---
        private readonly System.Timers.Timer _scanTimer = new System.Timers.Timer(2000); // 2000ms 周期
        private string[] _lastPorts = Array.Empty<string>();

        public bool IsConnected => _port.IsOpen;

        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string>? OnTransportError;

        // --- 新增：当串口列表发生变化时触发的事件 ---
        public event Action<string[]>? OnPortListChanged;

        public SerialService()
        {
            _port.DataReceived += SerialPort_DataReceived;
            _port.ErrorReceived += SerialPort_ErrorReceived;

            // --- 配置后台计时器 ---
            _scanTimer.Elapsed += (s, e) => ScanPorts(); // 触发时执行扫描
            _scanTimer.AutoReset = true; // 自动重置，循环执行
            _scanTimer.Enabled = true;   // 启动
        }

        // --- 核心逻辑：扫描串口列表 ---
        private void ScanPorts()
        {
            try
            {
                var currentPorts = SerialPort.GetPortNames();
                if (!currentPorts.SequenceEqual(_lastPorts))
                {
                    _lastPorts = currentPorts;
                    // 重点：这里的 Invoke 是在【后台线程】发射的
                    OnPortListChanged?.Invoke(currentPorts);
                }
            }
            catch { /* 扫描硬件偶尔异常时保持静默 */ }
        }

        // 在 SerialService.cs 中添加
        public string[] GetPortNames()
        {
            // 直接调用系统底层获取当前所有串口名
            return System.IO.Ports.SerialPort.GetPortNames();
        }

        public void Connect(string portName, int baudRate)
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();
                _port.PortName = portName;
                _port.BaudRate = baudRate;
                _port.DtrEnable = true;
                _port.RtsEnable = true;
                _port.ReceivedBytesThreshold = 1;
                _port.Open();
                _port.DiscardInBuffer();
            }
            catch (Exception ex)
            {
                OnTransportError?.Invoke($"连接失败: {ex.Message}");
            }
        }

        // 显式接口实现修改
        void IMediaTransport.Connect()
        {
            // 从全局配置中心直接拉取最新参数
            var cfg = App.ConfigSvc.Current;

            // 调用你带参数的那个 Connect 方法
            Connect(cfg.SerialPortName, cfg.BaudRate);
        }

        public void Disconnect()
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();
            }
            catch { }
        }

        public void Send(byte[] data)
        {
            if (!_port.IsOpen)
                return;
            try
            {
                _port.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                OnTransportError?.Invoke($"发送错误: {ex.Message}");
                Disconnect();
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int bytesToRead = _port.BytesToRead;
                if (bytesToRead <= 0)
                    return;
                byte[] buffer = new byte[bytesToRead];
                _port.Read(buffer, 0, bytesToRead);
                Console.WriteLine($"[Serial Receive]: {BitConverter.ToString(buffer)}");
                OnRawDataReceived?.Invoke(buffer);
            }
            catch (Exception ex) { OnTransportError?.Invoke($"读取错误: {ex.Message}"); }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            OnTransportError?.Invoke($"硬件故障: {e.EventType}");
        }
    }
}