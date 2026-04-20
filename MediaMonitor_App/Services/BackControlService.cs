using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Windows.Threading;
using MediaMonitor.Tools;

namespace MediaMonitor.Services
{
    public class BackControlService
    {
        private readonly List<byte> _buffer = new List<byte>();
        private readonly object _lock = new object();

        public BackControlService(TransportManager transport)
        {
            transport.OnRawDataReceived += (data) =>
            {
                lock (_lock)
                {
                    _buffer.AddRange(data);
                    ParseBuffer();
                }
            };
        }

        private void ParseBuffer()
        {
            while (_buffer.Count >= 4)
            {
                if (_buffer[0] != 0xAB)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                byte payloadLen = _buffer[2];
                int totalPackLen = 3 + payloadLen + 1;

                if (_buffer.Count < totalPackLen)
                    break;

                byte cmd = _buffer[1];

                // 校验通过后，不直接执行，而是入队
                if (ValidateCheckSum(totalPackLen))
                {
                    // 别管什么 Dispatcher 了，直接扔进全局队列
                    MediaKeyInvoker.Instance.EnqueueCommand(cmd);
                }

                _buffer.RemoveRange(0, totalPackLen);
            }
        }

        ///// <summary>
        ///// 校验和验证：计算方式需与硬件端严格对称
        ///// </summary>
        //private bool ValidateCheckSum(int length)
        //{
        //    byte checksum = 0;
        //    // 计算除了最后一个校验字节外的所有字节之和
        //    for (int i = 0; i < length - 1; i++)
        //    {
        //        checksum += _buffer[i];
        //    }

        //    return checksum == _buffer[length - 1];
        //}

        /// <summary>
        /// 校验和验证：与 Python 调试端对齐，仅对 Payload 进行异或运算
        /// </summary>
        private bool ValidateCheckSum(int totalPackLen)
        {
            // 根据协议：[0]Head, [1]Cmd, [2]Len, [3...n-1]Payload, [n]Check
            byte payloadLen = _buffer[2];
            byte expectedCheck = _buffer[totalPackLen - 1]; // 最后一个字节是校验位

            byte actualCheck = 0;

            // 仅针对 Payload 部分进行异或计算
            // Payload 的起始索引是 3，长度是 payloadLen
            for (int i = 0; i < payloadLen; i++)
            {
                actualCheck ^= _buffer[3 + i];
            }

            return actualCheck == expectedCheck;
        }
    }
}