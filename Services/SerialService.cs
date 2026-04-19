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
        private readonly DispatcherTimer _scanTimer = new DispatcherTimer();
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

            // --- 初始化定时器（每 2 秒扫描一次） ---
            _scanTimer.Interval = TimeSpan.FromSeconds(2);
            _scanTimer.Tick += (s, e) => ScanPorts();
            _scanTimer.Start();
        }

        // --- 核心逻辑：扫描串口列表 ---
        private void ScanPorts()
        {
            var currentPorts = SerialPort.GetPortNames();

            // 只有当列表真的变了（比如拔了或插了），才通知界面
            if (!currentPorts.SequenceEqual(_lastPorts))
            {
                _lastPorts = currentPorts;
                OnPortListChanged?.Invoke(currentPorts); // 发射信号
            }
        }

        public void Connect(string portName, int baudRate)
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();
                _port.PortName = portName;
                _port.BaudRate = baudRate;
                _port.Open();
            }
            catch (Exception ex)
            {
                OnTransportError?.Invoke($"连接失败: {ex.Message}");
            }
        }

        void IMediaTransport.Connect() => Connect(_port.PortName, _port.BaudRate);

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
                OnRawDataReceived.Invoke(buffer);
            }
            catch (Exception ex) { OnTransportError?.Invoke($"读取错误: {ex.Message}"); }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            OnTransportError?.Invoke($"硬件故障: {e.EventType}");
        }
    }
}