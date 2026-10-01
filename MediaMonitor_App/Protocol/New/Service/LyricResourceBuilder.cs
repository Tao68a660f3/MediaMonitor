using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MediaMonitor.Services;
using MediaMonitor.Tools;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// 歌词池 → **整份资源字节流**（规范 §10）。
    ///
    /// * 内容 = 歌词池全部行按 Legacy 帧格式序列化后的**连续字节流**：
    ///   逐字行走 `0x14`、普通行走 `0x15`（带结束时间）、有翻译就追加一行 `0x13`；
    /// * **不做任何裁剪**（`LineLimit` / `TransOccupies` 是显示排版参数，交付给 ESP32 决定）；
    /// * **强制 UTF-8**：不依赖 <c>PackageBuilder.UpdateEncoding</c> 的全局静态状态；
    /// * 时间戳（含 `[offset:N]` 与配置 Offset 的平移）已在 <see cref="LyricService"/> 解析阶段应用完毕。
    /// </summary>
    public static class LyricResourceBuilder
    {
        /// <summary>新协议链路固定 UTF-8（规范 §6 / §10）</summary>
        public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 把当前歌词池序列化成整份资源。
        /// </summary>
        /// <param name="lyrics">歌词服务（读取其不可变快照 <c>Lines</c>）</param>
        /// <param name="totalDuration">曲目总时长（用于末行的结束时间）</param>
        /// <param name="lineCount">输出：实际写入的行数（含翻译行）</param>
        public static byte[] Build(LyricService lyrics, TimeSpan totalDuration, out int lineCount)
        {
            lineCount = 0;
            if (lyrics == null)
            {
                return Array.Empty<byte>();
            }

            List<LyricLine> lines = lyrics.Lines;      // 不可变快照，整份构建期间不会被替换
            using var ms = new MemoryStream();

            for (int i = 0; i < lines.Count; i++)
            {
                LyricLine line = lines[i];
                if (line.IsEmpty)
                {
                    continue;
                }

                byte[] frame;
                if ((line.Words != null) && (line.Words.Count > 0))
                {
                    // 逐字行：0x14（规范 §10.1：是逐字就发逐字）
                    frame = PackageBuilder.BuildWordByWord((short)i, line.Time, line.Words, Utf8);
                }
                else
                {
                    // 普通行：0x15（带结束时间）
                    TimeSpan end = lyrics.GetEndTime(i, lines, totalDuration);
                    frame = PackageBuilder.BuildEnhancedLyricLine((short)i, line.Time, end, line.Content, Utf8);
                }

                ms.Write(frame, 0, frame.Length);
                lineCount++;

                if (!string.IsNullOrEmpty(line.Translation))
                {
                    byte[] trans = PackageBuilder.BuildTranslationLine((short)i, line.Time, line.Translation, Utf8);
                    ms.Write(trans, 0, trans.Length);
                    lineCount++;
                }
            }

            return ms.ToArray();
        }
    }
}
