using System;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// 一帧的只读视图（规范 §2.2）。
    /// <see cref="Payload"/> 是已复制出来的独立内存 —— 解析器内部缓冲会被复用，
    /// 因此回调收到的帧可以安全地留到回调返回之后使用。
    /// </summary>
    public readonly struct NpFrame
    {
        public ushort Version { get; init; }
        public byte Flags { get; init; }
        public byte Type { get; init; }
        public byte Code { get; init; }
        public byte HeaderLen { get; init; }
        public uint PayloadLen { get; init; }
        public uint Sequence { get; init; }
        public uint SessionId { get; init; }
        public uint RequestId { get; init; }
        public ReadOnlyMemory<byte> Payload { get; init; }

        public bool HasFlag(byte flag) => (Flags & flag) != 0;
        public bool IsSystem(byte code) => Type == NpType.System && Code == code;

        /// <summary>
        /// 本帧是否要求对端回 ACK（规范 §3 / §5.6 约定表，V1.1-21）：**唯一判据是 `FLAGS.ACK_REQUIRED`**。
        ///
        /// 收到 `NeedsAck == true` 的帧就必须回 `ACK`；`STATUS` 由处理结果决定
        /// （例：资源 `END` 要等 CRC32 校验完才知道回 `OK` 还是 `ERROR`）。
        /// </summary>
        public bool NeedsAck => HasFlag(NpFlag.AckRequired);

        public override string ToString()
            => $"T=0x{Type:X2} C=0x{Code:X2} len={PayloadLen} seq={Sequence} sid=0x{SessionId:X8} rid={RequestId}";
    }

    /// <summary>解析统计（用于诊断链路质量）</summary>
    public sealed class NpParserStats
    {
        public long FramesOk { get; internal set; }
        public long CrcErrors { get; internal set; }
        public long DroppedBadLen { get; internal set; }
        public long Resyncs { get; internal set; }
        public long BytesIn { get; internal set; }

        public override string ToString()
            => $"frames={FramesOk} crcErr={CrcErrors} badLen={DroppedBadLen} resync={Resyncs} bytesIn={BytesIn}";
    }
}
