using System;

namespace NewProtocolSelfTest
{
    /// <summary>一条测试向量（来源：protocolDesign_1.1_final.md 附录 A，与 C 侧 np_vectors.h 同源）</summary>
    internal sealed class NpVector
    {
        public string Name = "";
        public byte[] Bytes = Array.Empty<byte>();
        public ushort Version;
        public byte Flags;
        public byte Type;
        public byte Code;
        public uint PayloadLen;
        public uint Sequence;
        public uint SessionId;
        public uint RequestId;
        public byte[] Payload = Array.Empty<byte>();
    }

    internal static class Vectors
    {
        public static byte[] FromHex(string hex)
        {
            string[] parts = hex.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                bytes[i] = Convert.ToByte(parts[i], 16);
            }
            return bytes;
        }

        private static NpVector Make(string name, string hex, ushort version, byte flags, byte type, byte code,
                                     uint payloadLen, uint sequence, uint sessionId, uint requestId, string payloadHex)
        {
            return new NpVector
            {
                Name = name,
                Bytes = FromHex(hex),
                Version = version,
                Flags = flags,
                Type = type,
                Code = code,
                PayloadLen = payloadLen,
                Sequence = sequence,
                SessionId = sessionId,
                RequestId = requestId,
                Payload = FromHex(payloadHex)
            };
        }

        // 以下 hex 逐字节抄自规范附录 A.2 ~ A.6，**不要手改**
        public static readonly NpVector[] All =
        {
            Make("HELLO",
                 "5A A5 01 01 00 01 01 18 00 00 00 0A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 13 02 58 00 01 00 00 89 9B",
                 0x0101, 0x00, 0x01, 0x01, 10, 0, 0x00000000, 0,
                 "00 00 00 13 02 58 00 01 00 00"),

            Make("SESSION_START",
                 "5A A5 01 01 01 01 03 18 00 00 00 04 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00 2A FF 83",
                 0x0101, 0x01, 0x01, 0x03, 4, 1, 0x00000000, 0,
                 "00 00 00 2A"),

            Make("TIMELINE",
                 "5A A5 01 01 00 03 01 18 00 00 00 0D 00 00 00 03 00 00 00 2A 00 00 00 00 01 00 01 E2 40 00 04 E2 00 00 01 2A 7B F4 AC",
                 0x0101, 0x00, 0x03, 0x01, 13, 3, 0x0000002A, 0,
                 "01 00 01 E2 40 00 04 E2 00 00 01 2A 7B"),

            Make("MEDIA",
                 "5A A5 01 01 00 02 01 18 00 00 00 0D 00 00 00 04 00 00 00 2A 00 00 00 00 00 05 48 65 6C 6C 6F 00 01 41 00 01 42 BF 5C",
                 0x0101, 0x00, 0x02, 0x01, 13, 4, 0x0000002A, 0,
                 "00 05 48 65 6C 6C 6F 00 01 41 00 01 42"),

            Make("ACK",
                 "5A A5 01 01 04 01 05 18 00 00 00 05 00 00 00 05 00 00 00 2A 00 00 03 E9 00 00 00 01 00 F1 B2",
                 0x0101, 0x04, 0x01, 0x05, 5, 5, 0x0000002A, 1001,
                 "00 00 00 01 00")
        };

        public static string ToHex(ReadOnlySpan<byte> data)
        {
            return string.Join(" ", System.Linq.Enumerable.Select(data.ToArray(), b => b.ToString("X2")));
        }
    }
}
