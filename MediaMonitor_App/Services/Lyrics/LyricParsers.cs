using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 歌词解析器注册表：按扩展名把文件分派给对应的 <see cref="ILyricParser"/>。
    ///
    /// <para><see cref="Shared"/> 默认只注册 LRC；新增格式时注册进去即可，
    /// 匹配器的扫描扩展名会自动跟着变。读路径（<see cref="Resolve"/> / <see cref="Extensions"/>）
    /// 只看不可变快照，不加锁；注册是低频操作，用锁重建快照。</para>
    /// </summary>
    public sealed class LyricParserRegistry
    {
        private readonly object _sync = new object();
        private ILyricParser[] _parsers = Array.Empty<ILyricParser>();
        private string[] _extensions = Array.Empty<string>();

        /// <summary>全局默认注册表（LRC）</summary>
        public static LyricParserRegistry Shared { get; } = CreateShared();

        /// <summary>当前支持的全部扩展名（带点，快照）</summary>
        public IReadOnlyList<string> Extensions => Volatile.Read(ref _extensions);

        /// <summary>注册解析器；同扩展名时后注册的优先。空/重复扩展名会去重。</summary>
        public void Register(ILyricParser parser)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));

            string[] own = (parser.Extensions ?? Array.Empty<string>())
                .Select(NormalizeExtension)
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (own.Length == 0)
                throw new ArgumentException("歌词解析器必须声明至少一个扩展名", nameof(parser));

            lock (_sync)
            {
                var next = new ILyricParser[_parsers.Length + 1];
                Array.Copy(_parsers, next, _parsers.Length);
                next[next.Length - 1] = parser;

                var all = next.SelectMany(p => p.Extensions ?? Array.Empty<string>())
                              .Select(NormalizeExtension)
                              .Where(e => e.Length > 0)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToArray();

                // 先写 parsers 再写 extensions：读方看到新扩展名时一定能 Resolve 到解析器
                Volatile.Write(ref _parsers, next);
                Volatile.Write(ref _extensions, all);
            }
        }

        /// <summary>按扩展名找解析器；找不到返回 null（该文件会被跳过，不会影响其它候选）。</summary>
        public ILyricParser? Resolve(string? path)
        {
            string ext = NormalizeExtension(Path.GetExtension(path ?? ""));
            if (ext.Length == 0) return null;

            ILyricParser[] snapshot = Volatile.Read(ref _parsers);
            for (int i = snapshot.Length - 1; i >= 0; i--)   // 后注册优先
            {
                var parser = snapshot[i];
                if (parser.Extensions == null) continue;
                if (parser.Extensions.Any(e => string.Equals(NormalizeExtension(e), ext, StringComparison.OrdinalIgnoreCase)))
                    return parser;
            }
            return null;
        }

        /// <summary>统一成 ".xxx"（小写），非法输入返回空串。</summary>
        public static string NormalizeExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return "";
            ext = ext.Trim().ToLowerInvariant();
            return ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext;
        }

        private static LyricParserRegistry CreateShared()
        {
            var registry = new LyricParserRegistry();
            registry.Register(new LrcLyricParser());
            return registry;
        }
    }

    /// <summary>
    /// 歌词文本读取：BOM → 严格 UTF-8 → GB18030 回退 → UTF-8 替换字符兜底。
    /// 老 .lrc 大量是 GBK/ANSI，直接 <c>File.ReadAllLines</c>（UTF-8）会整片乱码。
    /// </summary>
    internal static class LyricTextFile
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly Encoding? Gb18030 = TryGetEncoding("GB18030");

        public static string[] ReadAllLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return Array.Empty<string>();

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Split(Encoding.UTF8, bytes, 3);

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Split(Encoding.Unicode, bytes, 2);

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Split(Encoding.BigEndianUnicode, bytes, 2);

            // 无 BOM：先按严格 UTF-8 试；失败再按中文老歌词最常见的 GB18030
            try
            {
                return Split(StrictUtf8, bytes, 0);
            }
            catch (DecoderFallbackException)
            {
                // fall through
            }

            if (Gb18030 != null)
            {
                try
                {
                    return Split(Gb18030, bytes, 0);
                }
                catch (DecoderFallbackException)
                {
                    // fall through
                }
            }

            return Split(Encoding.UTF8, bytes, 0);
        }

        private static string[] Split(Encoding encoding, byte[] bytes, int offset)
        {
            string text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static Encoding? TryGetEncoding(string name)
        {
            try { return Encoding.GetEncoding(name); }
            catch { return null; }   // 未注册 CodePagesProvider 时（如自测工程）静默放弃
        }
    }

    /// <summary>
    /// LRC 解析器：逐行时间戳、逐字标签、翻译行、<c>[offset:N]</c> 头标签。
    ///
    /// <para>本类是从原 <see cref="LyricService"/> 原样迁出的解析逻辑（行为不变，
    /// 由自测 R16 守着"空行照发 / 翻译合并 / offset 平移"）。</para>
    /// <para>解析器本身无状态，可并发调用；返回的 <see cref="LyricParseResult.Lines"/>
    /// 是本次解析独占的列表，交给 <see cref="LyricService"/> 原子发布。</para>
    /// </summary>
    public sealed class LrcLyricParser : ILyricParser
    {
        public string Name => "LRC";

        public IReadOnlyList<string> Extensions { get; } = new[] { ".lrc" };

        // LRC 头部 offset 标签：[offset:N]（毫秒，可带 +/-，允许空格与大小写差异）
        // 这里只负责取出文件原值，符号语义（负值 = 歌词偏快、需延后）见 ApplyOffset
        private static readonly Regex OffsetRegex =
            new Regex(@"^\s*\[offset\s*:\s*(?<v>[+-]?\d+)\s*\]\s*$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 宽容正则，匹配 [00:00.00] 或 <00:00.00>
        private static readonly Regex LineRegex =
            new Regex(@"[\[\<](?<t>\d{2,}:\d{2}(?:\.\d{2,3})?)[\]\>](?<c>.*)$",
                      RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 行内逐字标签：[00:00.00]字
        private static readonly Regex WordRegex =
            new Regex(@"[\[\<](?<t>\d{2,}:\d{2}\.\d{2,3})[\]\>](?<w>[^\[\<]*)",
                      RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public LyricParseResult Parse(string path)
        {
            string[] raw = LyricTextFile.ReadAllLines(path);
            var lines = new List<LyricLine>();

            // --- 头部 offset 标签预扫描：[offset:+/-毫秒] ---
            int offsetMs = ParseHeaderOffset(raw);

            foreach (string line in raw)
            {
                var m = LineRegex.Match(line.Trim());
                if (!m.Success) continue;

                if (!TimeSpan.TryParse("00:" + m.Groups["t"].Value, out TimeSpan t))
                    continue;

                string contentBody = m.Groups["c"].Value.Trim();

                // 翻译行处理：如果时间戳相同且内容不含逐字标签，视为翻译
                var existing = lines.FirstOrDefault(l => Math.Abs((l.Time - t).TotalMilliseconds) < 50);
                if (existing != null && !WordRegex.IsMatch(contentBody))
                {
                    existing.Translation = contentBody;
                    continue;
                }

                var newLine = new LyricLine { Time = t };
                var wordMatches = WordRegex.Matches(contentBody);

                if (wordMatches.Count > 0) // 逐字模式
                {
                    // 修复首字丢失：检查第一个标签前是否有文字
                    string headText = contentBody.Substring(0, wordMatches[0].Index).Trim();
                    if (!string.IsNullOrEmpty(headText))
                    {
                        // 第一个字的时间就是整行的起始时间 t（即偏移量为 0）
                        newLine.Words.Add(new WordInfo { Time = t, Word = headText });
                    }

                    foreach (Match w in wordMatches)
                    {
                        if (TimeSpan.TryParse("00:" + w.Groups["t"].Value, out TimeSpan wt))
                            newLine.Words.Add(new WordInfo { Time = wt, Word = w.Groups["w"].Value });
                    }
                    newLine.Content = string.Join("", newLine.Words.Select(x => x.Word)).Trim();
                }
                else
                {
                    // 检测 " / " 分隔符（空格-斜杠-空格），区分原文和翻译
                    int splitIdx = contentBody.IndexOf(" / ", StringComparison.Ordinal);
                    if (splitIdx > 0)
                    {
                        newLine.Content = contentBody.Substring(0, splitIdx).Trim();
                        newLine.Translation = contentBody.Substring(splitIdx + 3).Trim();
                    }
                    else
                    {
                        newLine.Content = contentBody.Trim();
                    }
                }

                lines.Add(newLine);
            }

            // --- 头部 [offset:N] 标签：整体平移时间轴（新时间 = 标签时间 - offset）---
            ApplyOffset(lines, offsetMs);
            lines.Sort((a, b) => a.Time.CompareTo(b.Time));

            return new LyricParseResult
            {
                Lines = lines,
                OffsetMs = offsetMs,
                ParserName = Name
            };
        }

        /// <summary>
        /// 预扫描歌词文件头部，取第一个有效的 [offset:N] 标签（毫秒，支持 +/-、空格与大小写）。
        /// 只扫描到第一个带时间戳的歌词行为止，避免把正文里的 [offset:..] 误当标签；
        /// 无标签、标签在正文中或数值非法时返回 0（即不偏移）。
        /// 返回值是文件里写的【原始】数值（不取反），真正的时间平移在 ApplyOffset 里完成。
        /// </summary>
        private static int ParseHeaderOffset(string[] raw)
        {
            foreach (string line in raw)
            {
                string trimmed = line.Trim().TrimStart('\uFEFF');
                if (LineRegex.IsMatch(trimmed)) break;      // 已进入歌词正文，停止扫描

                var m = OffsetRegex.Match(trimmed);
                if (m.Success && int.TryParse(m.Groups["v"].Value, out int v))
                    return v;                                // 只认第一个有效标签
            }
            return 0;
        }

        /// <summary>
        /// 应用 LRC 头部 [offset:N] 标签：把每一行（含行内逐字）的时间整体平移。
        ///
        /// <para>符号语义（Negative = 歌词比音频"快"，即唱早了，需要整体【延后】）：</para>
        /// <para>N &lt; 0 → 歌词偏快 → 时间 +|N|（延后）；N &gt; 0 → 歌词偏慢 → 时间 -N（提前）。</para>
        /// <para>统一写成：新时间 = 标签时间 - offset。</para>
        ///
        /// <para>唯一的额外保护：若某行平移后会落到 0ms 之前，则只把【该行】的平移量收敛到
        /// "让该行正好从 0ms 起"，行内逐字共用同一平移量；否则 <c>(uint)</c> 强转会变成
        /// 约 42.9 亿 ms 污染硬件。</para>
        /// </summary>
        private static void ApplyOffset(List<LyricLine> lines, int offsetMs)
        {
            if (offsetMs == 0 || lines.Count == 0) return;

            // 全程用 long 的 Tick 做整数运算：既没有 double 舍入误差，
            // 也避免 -offsetMs 在 int 域溢出。
            long shiftTicks = -(long)offsetMs * TimeSpan.TicksPerMillisecond;

            foreach (var l in lines)
            {
                long lineTicks = l.Time.Ticks;

                // 平移量默认取整轨偏移；若该行会被推到 0ms 之前，则只收敛本行到 0ms 起
                long deltaTicks = shiftTicks;
                if (lineTicks + deltaTicks < 0) deltaTicks = -lineTicks;

                if (deltaTicks == 0) continue;

                l.Time = TimeSpan.FromTicks(lineTicks + deltaTicks);

                // 行内逐字走同一个 deltaTicks，保证 (w.Time - line.Time) 的相对节奏完全不变
                foreach (var w in l.Words)
                    w.Time = TimeSpan.FromTicks(w.Time.Ticks + deltaTicks);
            }
        }
    }
}
