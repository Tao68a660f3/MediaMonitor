using System;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// 帧编码器（规范 §2 / §4.2）。
    /// 业务层不要自己拼字节 —— 一律通过这里产出完整帧。
    /// </summary>
    public static class NpEncoder
    {
        /// <summary>
        /// 编码一帧到 <paramref name="dest"/>。
        /// </summary>
        /// <returns>写入的字节数；0 表示参数非法或 <paramref name="dest"/> 空间不足。</returns>
        public static int Encode(in NpFrame frame, Span<byte> dest)
        {
            if (frame.HeaderLen != NpConstants.HeaderLenFixed)
            {
                return 0;                                        // V1.x 只支持固定头 24B
            }
            if (frame.PayloadLen > NpConstants.MaxPayloadLen)
            {
                return 0;
            }
            if (frame.Payload.Length < frame.PayloadLen)
            {
                return 0;
            }

            int need = frame.HeaderLen + (int)frame.PayloadLen + 2;
            if (dest.Length < need)
            {
                return 0;
            }

            dest[0] = NpConstants.Magic0;
            dest[1] = NpConstants.Magic1;
            NpWriter.WriteU16(dest[2..], frame.Version);
            dest[NpConstants.OffFlags] = frame.Flags;
            dest[NpConstants.OffType] = frame.Type;
            dest[NpConstants.OffCode] = frame.Code;
            dest[NpConstants.OffHeaderLen] = frame.HeaderLen;
            NpWriter.WriteU32(dest[NpConstants.OffPayloadLen..], frame.PayloadLen);
            NpWriter.WriteU32(dest[NpConstants.OffSequence..], frame.Sequence);
            NpWriter.WriteU32(dest[NpConstants.OffSessionId..], frame.SessionId);
            NpWriter.WriteU32(dest[NpConstants.OffRequestId..], frame.RequestId);

            if (frame.PayloadLen > 0)
            {
                frame.Payload.Span[..(int)frame.PayloadLen].CopyTo(dest[frame.HeaderLen..]);
            }

            // CRC 覆盖 [VERSION .. PAYLOAD 结束]，即 dest[2 .. need-2)（规范 §4.2）
            ushort crc = Crc16CcittFalse.Compute(dest[2..(need - 2)]);
            NpWriter.WriteU16(dest[(need - 2)..], crc);
            return need;
        }

        /// <summary>便捷重载：编码到新数组（返回 null 表示参数非法）。</summary>
        public static byte[]? EncodeToArray(in NpFrame frame)
        {
            byte[] buffer = new byte[NpConstants.HeaderLenFixed + (int)frame.PayloadLen + 2];
            int n = Encode(frame, buffer);
            return n == 0 ? null : buffer;
        }
    }
}
