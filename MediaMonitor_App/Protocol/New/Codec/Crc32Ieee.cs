using System;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// CRC-32/IEEE 802.3：init 0xFFFFFFFF / RefIn=RefOut=true / xorout 0xFFFFFFFF
    /// Check Value("123456789") = 0xCBF43926（规范 §12.1）
    ///
    /// Update 采用"内部态"语义（与 C 侧 np_crc32_update 一致）：
    ///     uint st = Crc32Ieee.Init;
    ///     st = Crc32Ieee.Update(st, data);
    ///     uint crc = Crc32Ieee.Final(st);
    /// </summary>
    public static class Crc32Ieee
    {
        public const uint Init = 0xFFFFFFFF;

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1u) != 0
                        ? (crc >> 1) ^ 0xEDB88320u
                        : crc >> 1;
                }
            }
            return crc;
        }

        public static uint Final(uint crc) => crc ^ 0xFFFFFFFF;

        public static uint Compute(ReadOnlySpan<byte> data) => Final(Update(Init, data));
    }
}
