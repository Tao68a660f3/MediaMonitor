using System;
using System.Collections.Generic;

namespace MediaMonitor.Services
{
    public class WordInfo
    {
        public TimeSpan Time { get; set; }
        public string Word { get; set; } = "";
    }

    public class LyricLine
    {
        public TimeSpan Time { get; set; }
        public string Content { get; set; } = "";
        public string Translation { get; set; } = "";
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public bool IsEmpty => string.IsNullOrEmpty(Content) && string.IsNullOrEmpty(Translation);
    }

    /// <summary>
    /// 歌词格式解析器插件接口。
    ///
    /// <para>新增格式（KRC / QRC / SRT / ASS …）只需要：</para>
    /// <list type="number">
    /// <item>实现本接口（<see cref="Parse"/> 返回统一的 <see cref="LyricParseResult"/>）；</item>
    /// <item><c>LyricParserRegistry.Shared.Register(new XxxParser())</c>。</item>
    /// </list>
    /// <para><see cref="LyricService"/> 与文件匹配器都不认识具体格式：扩展名进扫描集合、
    /// <c>[offset:N]</c> 之类的格式细节只留在各自解析器内部。</para>
    /// </summary>
    public interface ILyricParser
    {
        /// <summary>展示/日志用的格式名（如 "LRC"）</summary>
        string Name { get; }

        /// <summary>本解析器负责的扩展名（带点，大小写不敏感）</summary>
        IReadOnlyList<string> Extensions { get; }

        /// <summary>
        /// 解析整个文件。文件不可读时可以抛异常，<see cref="LyricService"/> 会丢弃该候选并尝试下一个；
        /// 文件可读但没有可解析的时间轴时返回 <see cref="LyricParseResult.Lines"/> 为空的结果。
        /// </summary>
        LyricParseResult Parse(string path);
    }

    /// <summary>解析器的统一输出：时间轴已应用过格式自带的偏移，<see cref="LyricService"/> 直接发布。</summary>
    public sealed class LyricParseResult
    {
        public List<LyricLine> Lines { get; init; } = new List<LyricLine>();

        /// <summary>格式自带的偏移原始值（毫秒）。LRC 对应 <c>[offset:N]</c>，其它格式没有就给 0。</summary>
        public int OffsetMs { get; init; }

        public string ParserName { get; init; } = "";

        public static LyricParseResult Empty(string parserName)
            => new LyricParseResult { ParserName = parserName };
    }

    /// <summary>
    /// 匹配原因（诊断/排序用；是否命中标题由前四个位决定）。
    /// 注意：这里没有"版本冲突/歌手冲突"之类需要词表的标记 ——
    /// 文件名里多出来的 `(Live)`、`[320k]` 等一律按"未解释 token"参与覆盖率扣分。
    /// </summary>
    [Flags]
    public enum LyricMatchFlags
    {
        None = 0,
        TitleExact = 1,
        TitleContains = 2,
        TitleTokens = 4,
        TitleFuzzy = 8,
        ArtistMatched = 16,
        AlbumMatched = 32,
        Ambiguous = 64,
    }

    /// <summary>一条文件候选。构造后不可变，按分数从高到低交给 <see cref="LyricService"/> 逐个尝试解析。</summary>
    public sealed class LyricMatch
    {
        public string Path { get; init; } = "";
        public int Score { get; init; }
        public LyricMatchFlags Flags { get; init; }

        /// <summary>人类可读的打分明细，写 Debug 日志 / 新协议事件日志用。</summary>
        public string Detail { get; init; } = "";

        public bool HasTitleSignal
            => (Flags & (LyricMatchFlags.TitleExact | LyricMatchFlags.TitleContains |
                         LyricMatchFlags.TitleTokens | LyricMatchFlags.TitleFuzzy)) != 0;

        public bool IsExactTitle => (Flags & LyricMatchFlags.TitleExact) != 0;

        public override string ToString() => $"{Path} [score={Score}, flags={Flags}]";
    }

    /// <summary>
    /// 匹配参数。全部有安全默认值，不落配置文件；需要调参的场景（如网络盘太慢）直接改
    /// <see cref="LyricService.MatchOptions"/> 即可。
    /// </summary>
    public sealed class LyricMatcherOptions
    {
        /// <summary>是否递归子目录（`歌手/专辑/标题.lrc` 这种结构靠它）</summary>
        public bool SearchSubdirectories { get; set; } = true;

        /// <summary>最大递归深度：2 = 根目录 + 歌手 + 专辑</summary>
        public int MaxSearchDepth { get; set; } = 2;

        /// <summary>低于该分数视为不可用；标题精确命中（60）永远高于它</summary>
        public int MinimumScore { get; set; } = 25;

        /// <summary>是否允许"标题拼写有偏差"的模糊回退（只在没有精确/包含候选时生效）</summary>
        public bool EnableFuzzy { get; set; } = true;

        /// <summary>最多保留多少个候选交给解析器逐个尝试</summary>
        public int MaxCandidates { get; set; } = 5;
    }
}
