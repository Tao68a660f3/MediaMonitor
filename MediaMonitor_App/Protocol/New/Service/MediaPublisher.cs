using System;
using System.Text;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// MEDIA/METADATA 推送（规范 §6，新协议自有编码：u16 大端长度 + UTF-8 文本）。
    ///
    /// * 空元数据（三个长度全 0，payload 恰 6 字节）= "当前没有在播放" → 硬件清屏复位；
    /// * 实时帧：`REQUEST_ID = 0`、不要求 ACK；
    /// * 发送时机见 §6：元数据变化时、会话建立后、传输重连后各推一次。
    /// </summary>
    public sealed class MediaPublisher
    {
        private readonly SessionManager _session;

        public long StatMediaTx;
        public long StatTruncated;
        public string LastTitle { get; private set; } = "";
        public string LastArtist { get; private set; } = "";
        public string LastAlbum { get; private set; } = "";

        public MediaPublisher(SessionManager session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>推送一条元数据（会话未建立时忽略）</summary>
        public void Publish(string? title, string? artist, string? album)
        {
            if (!_session.IsActive)
            {
                return;
            }

            LastTitle = title ?? "";
            LastArtist = artist ?? "";
            LastAlbum = album ?? "";

            _session.SendRealtime(NpType.Media, NpMediaCode.Metadata, BuildPayload(title, artist, album));
            StatMediaTx++;
        }

        /// <summary>推送"无媒体"（规范 §6 的复位语义）</summary>
        public void PublishEmpty() => Publish("", "", "");

        /// <summary>
        /// 构造 payload：`TITLE_LEN u16 | TITLE | ARTIST_LEN u16 | ARTIST | ALBUM_LEN u16 | ALBUM`
        /// 单字段上限 65535 字节，超出时**截断到完整的 UTF-8 字符边界**并计数。
        /// </summary>
        public byte[] BuildPayload(string? title, string? artist, string? album)
        {
            byte[] t = TrimField(Encoding.UTF8.GetBytes(title ?? ""));
            byte[] a = TrimField(Encoding.UTF8.GetBytes(artist ?? ""));
            byte[] b = TrimField(Encoding.UTF8.GetBytes(album ?? ""));

            byte[] p = new byte[2 + t.Length + 2 + a.Length + 2 + b.Length];
            int off = 0;
            WriteField(p, ref off, t);
            WriteField(p, ref off, a);
            WriteField(p, ref off, b);
            return p;
        }

        private static void WriteField(byte[] dest, ref int off, byte[] data)
        {
            NpWriter.WriteU16(dest.AsSpan(off), (ushort)data.Length);
            off += 2;
            Buffer.BlockCopy(data, 0, dest, off, data.Length);
            off += data.Length;
        }

        /// <summary>超过 u16 上限时截断（按 UTF-8 字符边界回退，避免半个汉字）</summary>
        private byte[] TrimField(byte[] data)
        {
            if (data.Length <= ushort.MaxValue)
            {
                return data;
            }

            StatTruncated++;
            int end = ushort.MaxValue;
            while (end > 0 && (data[end] & 0xC0) == 0x80)
            {
                end--;                       // 回退到非续字节（UTF-8 续字节是 10xxxxxx）
            }
            return data[..end];
        }
    }
}
