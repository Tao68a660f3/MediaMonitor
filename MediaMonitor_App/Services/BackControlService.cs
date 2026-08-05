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

        // 防御性上限：Len 误码成巨大值时丢弃当前帧头，重新搜索 0xAB
        private const int MaxFrameLen = 1024;

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
            while (_buffer.Count >= 5)
            {
                if (_buffer[0] != 0xAB)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                int payloadLen = (_buffer[2] << 8) | _buffer[3]; // LenH << 8 | LenL
                int totalPackLen = 4 + payloadLen + 1;           // Head+Cmd+LenH+LenL+Payload+Check

                // 防御：Len 误码成巨大值时，丢头重搜 0xAB，避免解析永久卡死
                if (payloadLen > MaxFrameLen)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

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

        /// <summary>
        /// 校验和验证：全帧异或，与 PC 端打包逻辑严格对称
        /// </summary>
        private bool ValidateCheckSum(int totalPackLen)
        {
            // 根据协议：[0]Head, [1]Cmd, [2]LenH, [3]LenL, [4...n-1]Payload, [n]Check
            byte expectedCheck = _buffer[totalPackLen - 1]; // 最后一个字节是校验位

            byte actualCheck = 0;

            // 全帧异或：Head ^ Cmd ^ LenH ^ LenL ^ Payload 所有字节
            for (int i = 0; i < totalPackLen - 1; i++)
            {
                actualCheck ^= _buffer[i];
            }

            return actualCheck == expectedCheck;
        }
    }
}