using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaMonitor.Tools
{
    public static class PackageParser
    {
        /// <summary>硬件 -> PC 包头（分帧搜索用，供 ProtocolLatencyTester 等复用）</summary>
        public const byte McuToPc = 0xAB;

        /// <summary>
        /// 尝试解析回控指令包：AB [Cmd] [LenH] [LenL] [Payload] [Check]
        /// </summary>
        public static bool TryParse(byte[] data, out byte cmd, out byte[] payload)
        {
            cmd = 0;
            payload = null;

            // 1. 基础长度校验 (Header + Cmd + LenH + LenL + Check = 5 bytes)
            if (data == null || data.Length < 5)
                return false;

            // 2. 查找包头
            if (data[0] != McuToPc)
                return false;

            cmd = data[1];
            int len = (data[2] << 8) | data[3]; // LenH << 8 | LenL

            // 3. 完整性校验：确保声明的长度与实际收到的数据匹配
            if (data.Length < 4 + len + 1)
                return false;

            // 4. 提取 Payload
            payload = new byte[len];
            Array.Copy(data, 4, payload, 0, len);

            // 5. 全帧异或校验 (CheckSum)：Head ^ Cmd ^ LenH ^ LenL ^ Payload 所有字节
            byte check = 0;
            for (int i = 0; i < data.Length - 1; i++)
                check ^= data[i];

            if (check != data[data.Length - 1])
                return false;

            return true;
        }

        /// <summary>
        /// 尝试解析 0xAF 延迟回包（Pong）：
        /// AB AF 00 09 [1B DevID] [4B C#_T1_ms 原样透传] [4B STM32_Proc_us] [Check]
        /// </summary>
        /// <param name="data">必须是"恰好一帧"的数据（调用方先自行分帧）</param>
        public static bool TryParsePong(byte[] data, out byte devId, out uint t1Ms, out uint procUs)
        {
            devId = 0;
            t1Ms = 0;
            procUs = 0;

            if (!TryParse(data, out byte cmd, out byte[] payload))
                return false;

            if (cmd != 0xAF || payload.Length != 9)
                return false;

            devId = payload[0];
            t1Ms = BitConverter.ToUInt32(payload, 1);   // C# 下发的时间戳，原样透传
            procUs = BitConverter.ToUInt32(payload, 5); // STM32 从 DMA 中断到组包发送的微秒数
            return true;
        }
    }
}