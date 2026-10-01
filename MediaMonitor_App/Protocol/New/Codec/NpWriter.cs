namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// Big-Endian 写入助手：一律逐字节写，不使用 BitConverter（避免平台端序假设）。
    /// 规范 §1.3：New Protocol 自身所有多字节整数一律大端。
    /// </summary>
    public static class NpWriter
    {
        public static void WriteU16(Span<byte> dest, ushort value)
        {
            dest[0] = (byte)(value >> 8);
            dest[1] = (byte)value;
        }

        public static void WriteU32(Span<byte> dest, uint value)
        {
            dest[0] = (byte)(value >> 24);
            dest[1] = (byte)(value >> 16);
            dest[2] = (byte)(value >> 8);
            dest[3] = (byte)value;
        }
    }
}
