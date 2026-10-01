namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// CRC-16/CCITT-FALSE：poly 0x1021 / init 0xFFFF / RefIn=RefOut=false / xorout 0
    /// Check Value("123456789") = 0x29B1（规范 §4.1）
    ///
    /// 位运算实现（不查表）：每帧只算一次，性能不是瓶颈。
    /// </summary>
    public static class Crc16CcittFalse
    {
        public const ushort Init = 0xFFFF;

        public static ushort Update(ushort crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
                }
            }
            return crc;
        }

        public static ushort Compute(ReadOnlySpan<byte> data) => Update(Init, data);
    }
}
