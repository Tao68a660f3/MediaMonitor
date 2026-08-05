using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaMonitor.Tools
{
    public static class PackageParser
    {
        private const byte MCU_TO_PC = 0xAB; // 回控包头

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
            if (data[0] != MCU_TO_PC)
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
    }
}