using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaMonitor.Tools
{
    public static class PackageParser
    {
        private const byte MCU_TO_PC = 0xAB; // 回控包头

        /// <summary>
        /// 尝试解析回控指令包：AB [Cmd] [Len] [Payload] [Check]
        /// </summary>
        public static bool TryParse(byte[] data, out byte cmd, out byte[] payload)
        {
            cmd = 0;
            payload = null;

            // 1. 基础长度校验 (Header + Cmd + Len + Check = 4 bytes)
            if (data == null || data.Length < 4)
                return false;

            // 2. 查找包头
            if (data[0] != MCU_TO_PC)
                return false;

            cmd = data[1];
            byte len = data[2];

            // 3. 完整性校验：确保声明的长度与实际收到的数据匹配
            if (data.Length < 3 + len + 1)
                return false;

            // 4. 提取 Payload
            payload = new byte[len];
            Array.Copy(data, 3, payload, 0, len);

            // 5. 异或校验 (CheckSum)
            byte check = 0;
            foreach (var b in payload)
                check ^= b;

            if (check != data[3 + len])
                return false;

            return true;
        }
    }
}