using System;
using System.IO.Ports;
using System.Collections.Generic;

namespace MediaMonitor.Services
{
    public class SerialService : IMediaTransport
    {
        private readonly SerialPort _port = new SerialPort();

        // 映射接口属性
        public bool IsConnected => _port.IsOpen;

        // 接口定义的事件，在构造函数中初始化防止 null 引用
        public event Action<byte[]> OnRawDataReceived = _ => { };

        // 新增：通知外部链路已断开（如拔线）
        public event Action<string>? OnTransportError;

        public SerialService()
        {
            _port.DataReceived += SerialPort_DataReceived;
            // 监控硬件层面的错误（如帧溢出、奇偶校验错）
            _port.ErrorReceived += SerialPort_ErrorReceived;
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
            catch { /* 强制关闭时忽略错误 */ }
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
                // 如果发送时物理断开，会抛出 IOException
                OnTransportError?.Invoke($"发送数据失败: {ex.Message}");
                Disconnect(); // 既然发不动了，主动切断连接状态
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int bytesToRead = _port.BytesToRead;
                if (bytesToRead > 0)
                {
                    byte[] buffer = new byte[bytesToRead];
                    _port.Read(buffer, 0, bytesToRead);
                    OnRawDataReceived.Invoke(buffer);
                }
            }
            catch (Exception ex)
            {
                OnTransportError?.Invoke($"读取数据错误: {ex.Message}");
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            OnTransportError?.Invoke($"串口硬件故障: {e.EventType}");
        }

        public string[] GetPortNames() => SerialPort.GetPortNames();
    }
}