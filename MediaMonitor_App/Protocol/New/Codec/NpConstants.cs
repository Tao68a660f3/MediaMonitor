namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// New Protocol V1.1 的常量与枚举。
    /// 规范：protocolDesign_1.1_final.md —— §1.1 TYPE / §2 帧格式 / §3 FLAGS /
    /// §5.1 SYSTEM CODE / §5.6 ACK STATUS / §5.7 ERROR / §11.1 资源格式。
    ///
    /// ⚠ 上限必须与 C 侧（ref_c/include/np_config.h）保持一致，改动需两端同步。
    /// </summary>
    public static class NpConstants
    {
        public const byte Magic0 = 0x5A;
        public const byte Magic1 = 0xA5;

        public const ushort Version10 = 0x0100;
        public const ushort Version11 = 0x0101;

        public const int HeaderLenFixed = 24;   // 前 24B 布局永久固定（§2.3）
        public const int MaxHeaderLen = 64;
        public const int MaxPayloadLen = 4096;
        public const int MaxFrameLen = MaxHeaderLen + MaxPayloadLen + 2;

        // 字段偏移（§2.2）
        public const int OffMagic = 0;
        public const int OffVersion = 2;
        public const int OffFlags = 4;
        public const int OffType = 5;
        public const int OffCode = 6;
        public const int OffHeaderLen = 7;
        public const int OffPayloadLen = 8;
        public const int OffSequence = 12;
        public const int OffSessionId = 16;
        public const int OffRequestId = 20;
    }

    /// <summary>TYPE（§1.1）</summary>
    public static class NpType
    {
        public const byte System = 0x01;
        public const byte Media = 0x02;
        public const byte Timeline = 0x03;
        public const byte Control = 0x04;
        public const byte Lyrics = 0x05;
        public const byte AlbumCover = 0x06;
    }

    /// <summary>FLAGS（§3；bit1 的 ACK 位已在 V1.1 删除）</summary>
    public static class NpFlag
    {
        public const byte AckRequired = 0x01;
        public const byte Response = 0x04;
        public const byte Fragment = 0x08;
        public const byte Error = 0x10;
    }

    /// <summary>SYSTEM CODE（§5.1）</summary>
    public static class NpSysCode
    {
        public const byte Hello = 0x01;
        public const byte HelloAck = 0x02;
        public const byte SessionStart = 0x03;
        public const byte SessionEnd = 0x04;
        public const byte Ack = 0x05;
        public const byte Error = 0x06;
        public const byte LatencyRequest = 0x10;
        public const byte LatencyResponse = 0x11;
        public const byte LatencyEnd = 0x12;
    }

    /// <summary>资源 CODE（LYRICS §10 / ALBUMCOVER §11 共用）</summary>
    public static class NpResCode
    {
        public const byte Request = 0x01;
        public const byte Begin = 0x02;
        public const byte Data = 0x03;
        public const byte End = 0x04;
        public const byte Abort = 0x05;
    }

    /// <summary>MEDIA CODE（§6）</summary>
    public static class NpMediaCode
    {
        public const byte Metadata = 0x01;
    }

    /// <summary>TIMELINE CODE（§7）</summary>
    public static class NpTimelineCode
    {
        public const byte State = 0x01;
    }

    /// <summary>CONTROL CODE（§8）</summary>
    public static class NpCtrlCode
    {
        public const byte PlayPause = 0x01;
        public const byte Next = 0x02;
        public const byte Previous = 0x03;
    }

    /// <summary>ACK STATUS（§5.6）</summary>
    public enum NpAckStatus : byte
    {
        Ok = 0x00,
        Rejected = 0x01,
        Invalid = 0x02,
        Busy = 0x03,
        Error = 0x04,
        NotReady = 0x05
    }

    /// <summary>ERROR 码（§5.7）</summary>
    public enum NpErrCode : byte
    {
        UnsupportedCode = 0x01,
        BadLength = 0x02,
        BadCrc = 0x03,
        NotInSession = 0x04,
        ResourceFail = 0x05,
        UnsupportedVersion = 0x06,
        VersionMismatch = 0x07,
        Timeout = 0x08
    }

    /// <summary>资源格式（§11.1）</summary>
    public enum NpResFormat : byte
    {
        None = 0x00,    // 无封面 / 清除
        Jpeg = 0x01,
        Png = 0x02,
        Rgb565 = 0x10   // 2B/像素，低字节在前
    }

    /// <summary>能力位（§5.2 CAPS）</summary>
    public static class NpCapsBits
    {
        public const uint Lyrics = 1u << 0;
        public const uint AlbumCover = 1u << 1;
        public const uint Jpeg = 1u << 2;
        public const uint Png = 1u << 3;
        public const uint Rgb565 = 1u << 4;
    }
}
