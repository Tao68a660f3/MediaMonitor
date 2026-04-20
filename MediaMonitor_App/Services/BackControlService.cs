using System;
using System.Collections.Generic;
using MediaMonitor.Tools;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 回控服务：负责监听硬件上传的指令并触发对应的系统操作
    /// </summary>
    public class BackControlService
    {
        private readonly TransportManager _transport;
        private readonly List<byte> _buffer = new List<byte>();
        private readonly object _lock = new object();

        public BackControlService(TransportManager transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

            // 订阅传输层的原始数据接收事件
            _transport.OnRawDataReceived += HandleRawData;
        }

        private void HandleRawData(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            lock (_lock)
            {
                // 将新收到的字节塞入缓冲区
                _buffer.AddRange(data);
                ParseBuffer();
            }
        }

        private void ParseBuffer()
        {
            // 上行包格式: 0xAB [Cmd] [Len] [Payload] [Check]
            // 最小包长：1(头) + 1(命令) + 1(长度) + 0(负载) + 1(校验) = 4 字节
            while (_buffer.Count >= 4)
            {
                // 1. 寻找包头 0xAB
                if (_buffer[0] != 0xAB)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                // 2. 获取有效载荷长度
                byte payloadLen = _buffer[2];
                int totalPackLen = 3 + payloadLen + 1;

                // 3. 检查缓冲区是否已经包含完整的包
                if (_buffer.Count < totalPackLen)
                {
                    break; // 数据不足，等待下次接收
                }

                // 4. 提取包内容
                byte cmd = _buffer[1];

                // --- 校验逻辑 ---
                // 这里我们采用与下行包对称的校验逻辑
                if (ValidateCheckSum(totalPackLen))
                {
                    // 校验通过，执行对应的媒体按键动作
                    MediaKeyInvoker.Execute(cmd);
                }

                // 5. 无论校验是否通过，都从缓冲区移除处理过的包
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