using System;

namespace MediaMonitor.Services
{
    // 这是一个“空壳”传输层，用于在未连接硬件时让程序跑起来
    public class DummyTransport : IMediaTransport
    {
        // 永远返回未连接
        public bool IsConnected => false;

        // 实现接口要求的事件（虽然永远不会触发）
        public event Action<byte[]> OnRawDataReceived = _ => { };
        public event Action<string> OnTransportError = _ => { };

        // 所有的操作都是空的，不会产生任何副作用
        public void Connect()
        {
        }
        public void Disconnect()
        {
        }
        public void Send(byte[] data)
        {
        }
    }
}