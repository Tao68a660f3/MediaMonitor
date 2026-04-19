using MediaMonitor.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MediaMonitor.Tools
{
    public enum EncodingType
    {
        UTF8, GB2312
    }

    public static class PackageBuilder
    {
        // 协议包头定义
        private const byte PC_TO_MCU = 0xAA;

        public static EncodingType SelectedEncoding { get; set; } = EncodingType.UTF8;

        private static Encoding CurrentEncoding =>
            SelectedEncoding == EncodingType.GB2312 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

        public static byte[] GetEncodedBytes(string text) => CurrentEncoding.GetBytes(text);

        // 通用打包逻辑
        // 修改点：固定传入 0xAA 作为 Header
        // 基础打包逻辑：AA [Cmd] [Len] [Payload] [Check]
        public static byte[] BuildPacket(byte cmd, byte[] payload)
        {
            List<byte> frame = new List<byte> { PC_TO_MCU, cmd, (byte)payload.Length };
            frame.AddRange(payload);
            byte check = 0;
            foreach (var b in payload)
                check ^= b; // 异或校验
            frame.Add(check);
            return frame.ToArray();
        }

        // 统一 Header 构建：Index(2B) + StartTime(4B)
        private static List<byte> BuildHeader(short absIdx, uint startTimeMs)
        {
            var header = new List<byte>();
            header.AddRange(BitConverter.GetBytes(absIdx));
            header.AddRange(BitConverter.GetBytes(startTimeMs));
            return header;
        }

        // 0x20: 对时包
        public static byte[] BuildTimeSync()
        {
            var now = DateTime.Now;
            byte week = (byte)(now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek);
            byte[] payload = new byte[] {
                (byte)(now.Year % 100), (byte)now.Month, (byte)now.Day,
                (byte)now.Hour, (byte)now.Minute, (byte)now.Second, week
            };
            return BuildPacket(0x20, payload);
        }

        // 0x10: 媒体元数据
        public static byte[] BuildMetadata(string title, string artist, string album)
        {
            List<byte> p = new List<byte>();
            byte[] t = GetEncodedBytes(title);
            p.Add((byte)t.Length);
            p.AddRange(t);
            byte[] r = GetEncodedBytes(artist);
            p.Add((byte)r.Length);
            p.AddRange(r);
            byte[] b = GetEncodedBytes(album);
            p.Add((byte)b.Length);
            p.AddRange(b);
            return BuildPacket(0x10, p.ToArray());
        }

        // 0x11: 同步包
        public static byte[] BuildSync(bool isPlaying, uint currentMs, uint totalMs)
        {
            List<byte> p = new List<byte> { (byte)(isPlaying ? 1 : 0) };
            p.AddRange(BitConverter.GetBytes(currentMs));
            p.AddRange(BitConverter.GetBytes(totalMs));
            return BuildPacket(0x11, p.ToArray());
        }

        // 0x12: 原文包
        public static byte[] BuildLyricLine(short absIdx, TimeSpan startTime, string content)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.AddRange(GetEncodedBytes(content));
            return BuildPacket(0x12, p.ToArray());
        }

        // 0x13: 翻译包
        public static byte[] BuildTranslationLine(short absIdx, TimeSpan startTime, string translation)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.AddRange(GetEncodedBytes(translation));
            return BuildPacket(0x13, p.ToArray());
        }

        // 0x14: 逐字包
        public static byte[] BuildWordByWord(short absIdx, TimeSpan startTime, List<WordInfo> words)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.Add((byte)words.Count); // 词数 (1B)
            foreach (var w in words)
            {
                ushort offset = (ushort)Math.Max(0, (w.Time - startTime).TotalMilliseconds);
                byte[] wordBytes = GetEncodedBytes(w.Word);
                p.AddRange(BitConverter.GetBytes(offset)); // 偏移 (2B)
                p.Add((byte)wordBytes.Length);             // 长度 (1B)
                p.AddRange(wordBytes);                     // 文本
            }
            return BuildPacket(0x14, p.ToArray());
        }
    }
}