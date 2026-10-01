using System;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// Big-Endian 读取助手（调用方保证 span 长度足够；不足时抛 <see cref="ArgumentOutOfRangeException"/>）。
    /// 解析器内部的所有读取都先做过长度校验，因此这里是"信任调用方"的薄封装。
    /// </summary>
    public static class NpReader
    {
        public static ushort ReadU16(ReadOnlySpan<byte> src)
        {
            if (src.Length < 2) throw new ArgumentOutOfRangeException(nameof(src));
            return (ushort)((src[0] << 8) | src[1]);
        }

        public static uint ReadU32(ReadOnlySpan<byte> src)
        {
            if (src.Length < 4) throw new ArgumentOutOfRangeException(nameof(src));
            return ((uint)src[0] << 24) | ((uint)src[1] << 16) | ((uint)src[2] << 8) | src[3];
        }
    }
}
