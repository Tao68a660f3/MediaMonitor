using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 歌词编排器：负责"校验元数据 → 让 <see cref="LyricMatcher"/> 找候选 →
    /// 让 <see cref="LyricParserRegistry"/> 里的解析器解析 → 原子发布快照"。
    ///
    /// <para><b>不包含任何具体格式的解析细节</b>（LRC/KRC… 都在 <see cref="ILyricParser"/> 实现里），
    /// 也不依赖固定文件名 pattern：匹配只看 SMTC 的 Title / Artist / Album。</para>
    ///
    /// <para>公开表面与此前保持一致：<see cref="Lines"/> / <see cref="Generation"/> /
    /// <see cref="CurrentLyricPath"/> / <see cref="CurrentOffsetMs"/> / <see cref="IsPlaceholder"/> /
    /// <see cref="GetLine"/> / <see cref="GetEndTime"/> / <see cref="ComposeSongInfo"/>。</para>
    /// </summary>
    public class LyricService
    {
        public string LyricFolder { get; set; } = "";

        /// <summary>
        /// 歌词格式解析器注册表。默认 <see cref="LyricParserRegistry.Shared"/>（只含 LRC）；
        /// 接入 KRC 等格式时注册进去即可，扫描扩展名与解析分派都会自动跟随。
        /// </summary>
        public LyricParserRegistry Parsers { get; set; } = LyricParserRegistry.Shared;

        /// <summary>匹配参数（递归深度、阈值、模糊开关…），有安全默认值。</summary>
        public LyricMatcherOptions MatchOptions { get; } = new LyricMatcherOptions();

        public string? CurrentLyricPath { get; private set; }

        /// <summary>本次歌词实际使用的格式名（如 "LRC" / "KRC"）；无歌词或占位时为空。</summary>
        public string CurrentParserName { get; private set; } = "";

        /// <summary>
        /// 歌词代际号：每次原子替换歌词列表时 +1。
        /// 供读取方检测"歌词是否已切换"，从而作废旧帧账本、强制重发当前行。
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>
        /// 当前歌词文件头部 [offset:N] 标签的【原始值】（毫秒；无标签或标签非法时为 0）。
        /// 注意：这里保留文件里写的符号（负值 = 歌词偏快、需延后），
        /// 真正采用的时间平移量是 -CurrentOffsetMs，且已在解析阶段应用到各行（含逐字）时间上；
        /// 本属性仅作记录、供排查问题用，读取方不要拿它再做一次时间换算。
        /// </summary>
        public int CurrentOffsetMs { get; private set; }

        /// <summary>
        /// 当前 Lines 是否是"未找到歌词"时的歌曲信息占位行：
        /// true 时 Lines 恒为 1 行（Content = 歌曲信息）、CurrentLyricPath 为 null、CurrentOffsetMs 为 0。
        /// 仅用于界面区分"真的加载到 lrc"与"只是占位"，不要拿它参与时间/发送逻辑。
        /// </summary>
        public bool IsPlaceholder { get; private set; }

        /// <summary>
        /// 本次加载实际命中的候选（文件 / 分数 / 命中原因）。占位或未命中时为 null。
        /// 仅供日志与排查使用，不参与发送逻辑。
        /// </summary>
        public LyricMatch? LastMatch { get; private set; }

        // --- 不可变快照机制 ---
        // Lines 永远指向一个"不再被修改"的列表实例：加载/解析期间只在局部 newLines 构建，
        // 完成后一次性原子替换。读取方（后台 10ms 循环）每帧捕获一次引用作为快照，
        // 整帧内所有歌词读取都基于同一快照，绝不会读到"新旧歌词混杂"的半成品 List。
        // 注意：替换的是 List 实例本身（引用赋值），旧实例不再被修改，因此读方无需加锁。
        public List<LyricLine> Lines { get; private set; } = new List<LyricLine>();

        // 安全获取指定索引的歌词，越界则返回空行对象（基于当前 Lines 快照）
        public LyricLine GetLine(int index)
        {
            return GetLine(index, Lines);
        }

        // 安全获取指定索引的歌词，越界则返回空行对象（基于调用方捕获的快照）
        public LyricLine GetLine(int index, IReadOnlyList<LyricLine> lines)
        {
            if (index < 0 || index >= lines.Count) return new LyricLine();
            return lines[index];
        }

        /// <summary>
        /// 计算某行歌词的结束时间（供 0x15 增强原文行使用）：
        /// 中间行 = 下一行的开始时间；
        /// 末行优先使用播放总时长（运行时才有），若总时长不合理
        /// （小于等于行开始时间、或与行开始时间差距过小），则退回“开始时间+5秒”兜底。
        /// </summary>
        public TimeSpan GetEndTime(int index, TimeSpan totalDuration)
        {
            return GetEndTime(index, Lines, totalDuration);
        }

        // 基于调用方捕获的快照计算结束时间
        public TimeSpan GetEndTime(int index, IReadOnlyList<LyricLine> lines, TimeSpan totalDuration)
        {
            if (index < 0 || index >= lines.Count)
                return TimeSpan.Zero;

            // 中间行：下一行的开始时间即本行结束时间
            if (index < lines.Count - 1)
                return lines[index + 1].Time;

            // 末行：优先使用播放总时长
            var start = lines[index].Time;
            if (totalDuration > start && (totalDuration - start).TotalSeconds > 1)
                return totalDuration;

            return start + TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// 拼接歌曲信息文案："歌名 - 歌手"（无歌手时只有歌名）。
        /// 「有 SMTC 会话但没找到歌词」时，占位歌词用的就是这串文本
        /// （与托盘提示里的 songInfo 同规则，但不含程序标题）。
        /// </summary>
        public static string ComposeSongInfo(string? title, string? artist)
            => string.IsNullOrEmpty(artist) ? (title ?? "") : $"{title} - {artist}";

        /// <summary>
        /// 按当前曲目的 Title / Artist / Album 加载歌词。
        ///
        /// <para>匹配策略见 <see cref="LyricMatcher"/>：标题是唯一硬条件，歌手/专辑只影响排序；
        /// 找不到任何标题命中的文件才发布占位歌词。候选按分数从高到低逐个尝试解析，
        /// 某个文件解析出 0 行（坏文件/空文件）会自动落到下一个候选。</para>
        /// </summary>
        public void LoadAndParse(string? title, string? artist, string? album = null)
        {
            Debug.WriteLine($"尝试载入歌词: {title} / {artist} / {album}");

            var newLines = new List<LyricLine>();
            LastMatch = null;
            CurrentParserName = "";

            // --- 闸门 1：拦截无效元数据 ---
            if (string.IsNullOrWhiteSpace(title))
            {
                PublishLyrics(newLines, null, 0);
                return;
            }

            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder))
            {
                PublishLyricsOrPlaceholder(newLines, null, 0, title, artist);
                return;
            }

            LyricParserRegistry parsers = Parsers ?? LyricParserRegistry.Shared;

            IReadOnlyList<LyricMatch> matches;
            try
            {
                var matcher = new LyricMatcher(MatchOptions);
                matches = matcher.FindCandidates(LyricFolder, title, artist, album, parsers.Extensions);
            }
            catch (Exception ex)
            {
                // 网络盘/权限异常不应该让歌词链路崩掉：按"没搜到"处理
                Debug.WriteLine($"歌词搜索失败: {ex.Message}");
                matches = Array.Empty<LyricMatch>();
            }

            foreach (LyricMatch match in matches)
            {
                LyricParseResult? parsed = TryParse(match.Path, parsers);
                if (parsed == null || parsed.Lines.Count == 0)
                    continue;

                LastMatch = match;
                CurrentParserName = parsed.ParserName;
                PublishLyricsOrPlaceholder(parsed.Lines, match.Path, parsed.OffsetMs, title, artist);
                return;
            }

            PublishLyricsOrPlaceholder(newLines, null, 0, title, artist);
        }

        /// <summary>按扩展名找解析器并解析；格式不支持/文件损坏/读取失败都返回 null，不影响其它候选。</summary>
        private static LyricParseResult? TryParse(string path, LyricParserRegistry parsers)
        {
            try
            {
                ILyricParser? parser = parsers.Resolve(path);
                if (parser == null)
                {
                    Debug.WriteLine($"没有可解析 {Path.GetExtension(path)} 的歌词解析器: {path}");
                    return null;
                }

                return parser.Parse(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"歌词解析失败（{Path.GetFileName(path)}）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 原子发布歌词：一次性替换共享引用并递增代际号。
        /// 这是唯一修改 Lines / CurrentLyricPath / CurrentOffsetMs / IsPlaceholder / Generation 的地方，
        /// 保证读取方永远看到"完整的新列表"或"完整的旧列表"，绝不看到半成品。
        /// </summary>
        private void PublishLyrics(List<LyricLine> newLines, string? newPath, int offsetMs, bool isPlaceholder = false)
        {
            Lines = newLines;
            CurrentLyricPath = newPath;
            CurrentOffsetMs = offsetMs;
            IsPlaceholder = isPlaceholder;
            Generation++;
        }

        /// <summary>
        /// 统一收尾发布：有歌词行就正常发布；一行都没有、但当前确实有歌曲信息时
        /// （没搜到文件 / 歌词目录不可用 / 文件里没有可解析的时间戳），退化为一行"歌曲信息占位歌词"。
        /// 占位行只写起点 0ms，终点时间交给 GetEndTime 的末行分支 = 曲目总时长，
        /// 因此占位行会从 0ms 一直显示到歌曲结束（无需在此写死终点）。
        /// </summary>
        private void PublishLyricsOrPlaceholder(
            List<LyricLine> newLines, string? newPath, int offsetMs, string? title, string? artist)
        {
            if (newLines.Count > 0)
            {
                PublishLyrics(newLines, newPath, offsetMs);
                return;
            }

            string tip = ComposeSongInfo(title, artist);
            if (string.IsNullOrEmpty(tip))
            {
                PublishLyrics(newLines, null, 0);   // 连歌名都没有（闸门 1）：保持原有的空歌词行为
                return;
            }

            var placeholder = new List<LyricLine>
            {
                new LyricLine { Time = TimeSpan.Zero, Content = tip }
            };
            PublishLyrics(placeholder, null, 0, isPlaceholder: true);
        }
    }
}
