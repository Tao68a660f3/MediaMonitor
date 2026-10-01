using System;
using MediaMonitor.Protocol.New.Codec;
using MediaMonitor.Tools;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>一份待发送的资源（已算好资源级 CRC32）</summary>
    public sealed record NpResourceData(uint Crc32, byte[] Data, byte Format, ushort Width, ushort Height);

    /// <summary>
    /// 封面资源构造（规范 §11）：严格按**对端请求**的 W/H/FORMAT/QUALITY 产出。
    ///
    /// * **无封面** → 返回 `FORMAT=NONE`、`TOTAL_SIZE=0` 的**空资源**（正常响应，不是 NOT_READY）；
    /// * 尺寸严格等于请求值（<see cref="ArtworkProcessor"/> 等比填满 + 居中裁剪，不拉伸变形）；
    /// * `FORMAT` 不支持 / 重编码失败 → 返回 null（上层回 `ACK(REJECTED)`）。
    /// </summary>
    public static class ArtworkResourceBuilder
    {
        public static NpResourceData? Build(byte[]? smtcThumbnail, ushort width, ushort height,
                                            NpResFormat format, byte quality, out string? error)
        {
            error = null;

            if ((format != NpResFormat.Jpeg) && (format != NpResFormat.Png) && (format != NpResFormat.Rgb565))
            {
                error = $"不支持的 FORMAT 0x{(byte)format:X2}";
                return null;
            }
            if ((width == 0) || (height == 0))
            {
                error = "WIDTH/HEIGHT 不能为 0";
                return null;
            }

            // 无封面（很多播放器不给 SMTC 缩略图）：空资源 + FORMAT=NONE，对端据此清掉封面区
            if ((smtcThumbnail == null) || (smtcThumbnail.Length == 0))
            {
                return new NpResourceData(Crc32Ieee.Compute(ReadOnlySpan<byte>.Empty),
                                          Array.Empty<byte>(), (byte)NpResFormat.None, 0, 0);
            }

            int q = (quality == 0) ? ArtworkProcessor.DefaultQuality : quality;

            ArtworkImage? img = format switch
            {
                NpResFormat.Jpeg => ArtworkProcessor.ProcessToJpeg(smtcThumbnail, width, height, q),
                NpResFormat.Png => ArtworkProcessor.ProcessToPng(smtcThumbnail, width, height),
                _ => ArtworkProcessor.ProcessToRgb565(smtcThumbnail, width, height)
            };

            if (img == null)
            {
                error = "重编码失败（源图损坏或格式不支持）";
                return null;
            }

            return new NpResourceData(Crc32Ieee.Compute(img.Data), img.Data, (byte)format,
                                      (ushort)img.Width, (ushort)img.Height);
        }
    }
}
