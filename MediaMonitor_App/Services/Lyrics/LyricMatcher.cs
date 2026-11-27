using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 文件名/元数据规范化：所有比较都在"NFKC + 小写 + 标点折叠"的同一域里进行，
    /// 因此 `A - B`、`A－B`、`Ａ　Ｂ`、`A_B` 视为同一种写法。
    /// </summary>
    internal static class LyricTextNormalizer
    {
        // 播放器偶尔会把音频扩展名混进 SMTC 元数据（Title="Song.mp3"），
        // 也可能出现双扩展名文件（"Song.mp3.lrc"）。这里统一剥掉尾部的音频扩展名。
        private static readonly Regex MediaExtension = new(
            @"\.(?:mp3|mp2|mpga|flac|wav|wave|m4a|m4b|aac|ogg|oga|opus|ape|wma|wv|tak|tta|dsf|dff|aiff?|alac|mka|spx)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 前导音轨号：`01. ` / `01 - ` / `01_` / `[01] `。
        // 数字后必须跟分隔符，避免误伤 "7 Rings"、"24K Magic" 这类正常标题。
        private static readonly Regex TrackPrefix = new(
            @"^\s*(?:\[\s*\d{1,3}\s*\]|\d{1,3}\s*[.\-_、)])\s*",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 括号整体（含括号本身）：`(Live)` `[320k]` `【官方】`
        private static readonly Regex BracketContent = new(
            @"[\(（\[【「『][^\)）\]】」』]*[\)）\]】」』]",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 多歌手/多字段分隔符：`A & B`、`A / B`、`A feat. B`
        private static readonly Regex ArtistSeparators = new(
            @"\s*(?:;|/|&|,|、|×)\s*|\s+(?:feat\.?|ft\.?|vs\.?)\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>去掉元数据尾部混入的音频扩展名（不改变其它内容）。</summary>
        public static string CleanMetadata(string? value)
            => string.IsNullOrWhiteSpace(value) ? "" : MediaExtension.Replace(value.Trim(), "");

        /// <summary>把任意文本折叠成比较域：全角→半角、小写、非字母数字汉字→空格。</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = true;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>剥掉括号内容后再规范化（用于标题/文件名的"主干"比较）。</summary>
        public static string NormalizeCore(string? value)
            => string.IsNullOrWhiteSpace(value) ? "" : Normalize(BracketContent.Replace(value, " "));

        public static string[] Tokens(string? normalized)
            => string.IsNullOrEmpty(normalized)
                ? Array.Empty<string>()
                : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>文件名去扩展名、去前导音轨号（仍保留括号内容）。</summary>
        public static string FileStem(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            name = TrackPrefix.Replace(name, "");
            return MediaExtension.Replace(name.Trim(), "");
        }

        /// <summary>歌手变体：整串 + 按 `& / feat.` 等拆分出的每个名字。</summary>
        public static string[] ArtistVariants(string? artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<string>();

            var set = new HashSet<string>(StringComparer.Ordinal);
            string whole = NormalizeCore(artist);
            if (whole.Length > 0) set.Add(whole);

            foreach (string part in ArtistSeparators.Split(artist))
            {
                string n = NormalizeCore(part);
                if (n.Length > 0) set.Add(n);
            }

            return set.ToArray();
        }

        /// <summary>needle 是否作为连续 token 出现在 haystack 中。</summary>
        public static bool ContainsSequence(string[] haystack, string[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return false;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>文件相对搜索根目录的各级目录名（规范化后）。</summary>
        public static string[] DirectorySegments(string root, string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return Array.Empty<string>();

            string relative;
            try { relative = Path.GetRelativePath(root, dir); }
            catch { return Array.Empty<string>(); }

            if (relative == "." || relative.Length == 0) return Array.Empty<string>();

            string[] parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var result = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                string n = NormalizeCore(part);
                if (n.Length > 0) result.Add(n);
            }
            return result.ToArray();
        }

        public static bool IsCjk(string token)
        {
            foreach (char c in token)
            {
                if (c >= '\u4E00' && c <= '\u9FFF') return true;   // 汉字
                if (c >= '\u3400' && c <= '\u4DBF') return true;   // 汉字扩展 A
                if (c >= '\u3040' && c <= '\u30FF') return true;   // 平假名 / 片假名
            }
            return false;
        }

        /// <summary>1.0 = 完全相同；按编辑距离归一化。</summary>
        public static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1;
            int max = Math.Max(a.Length, b.Length);
            return 1.0 - (double)Levenshtein(a, b) / max;
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length, m = b.Length;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[m];
        }
    }

    /// <summary>
    /// 按 SMTC 元数据（标题 / 歌手 / 专辑）给歌词文件打分，**不再依赖固定文件名 pattern**。
    ///
    /// <para>核心原则：</para>
    /// <list type="number">
    /// <item><b>标题是唯一硬条件</b>：标题没有任何信号的文件直接淘汰；</item>
    /// <item>歌手 / 专辑只影响排序：完全匹配不到时退化为"标题命中"，而不是放弃；</item>
    /// <item>歌手 / 专辑按“桶”统计完整字段出现次数；同一桶内同名值重复按 1/2、1/4… 几何衰减，标题只做硬锚点；</item>
    /// <item><b>不做任何歌词词表</b>：文件名里多出来的 `(Live)`、`[320k]`、`feat` 等，
    /// 统一按"没有被 Title/Artist/Album 解释的 token"参与覆盖率扣分；</item>
    /// <item>模糊匹配只在没有任何精确/包含候选时启用，作为最后兜底。</item>
    /// </list>
    ///
    /// <para>元数据尾部混入的音频扩展名（`Song.mp3`）在规范化阶段统一剥掉。</para>
    /// </summary>
    public sealed class LyricMatcher
    {
        private readonly LyricMatcherOptions _options;

        public LyricMatcher(LyricMatcherOptions? options = null)
            => _options = options ?? new LyricMatcherOptions();

        /// <summary>
        /// 返回按可信度从高到低排列的候选文件（已过滤掉没有标题信号/低于阈值的）。
        /// 调用方按顺序尝试解析即可：某个文件解析出 0 行时自动落到下一个候选。
        /// </summary>
        public IReadOnlyList<LyricMatch> FindCandidates(
            string? folder,
            string? title,
            string? artist,
            string? album,
            IReadOnlyCollection<string>? extensions = null)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return Array.Empty<LyricMatch>();

            extensions ??= LyricParserRegistry.Shared.Extensions;
            Query query = BuildQuery(title, artist, album, extensions);
            if (query.TitleTokens.Length == 0 || query.ExtensionSet.Count == 0)
                return Array.Empty<LyricMatch>();

            var candidates = new List<LyricMatch>();
            foreach (string file in EnumerateFiles(folder!, query))
            {
                LyricMatch? match = Score(query, file, folder!);
                if (match != null) candidates.Add(match);
            }

            return Rank(candidates);
        }

        private IEnumerable<string> EnumerateFiles(string folder, Query query)
        {
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = _options.SearchSubdirectories,
                MaxRecursionDepth = Math.Max(1, _options.MaxSearchDepth),
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            foreach (string file in Directory.EnumerateFiles(folder, "*", enumeration))
            {
                string ext = Path.GetExtension(file);
                if (query.ExtensionSet.Contains(ext))
                    yield return file;
            }
        }

        private IReadOnlyList<LyricMatch> Rank(List<LyricMatch> candidates)
        {
            if (candidates.Count == 0) return Array.Empty<LyricMatch>();

            // 模糊候选只在没有任何精确/包含候选时才允许参与，避免"拼写相近"压过"标题精确"
            var nonFuzzy = candidates.Where(c => (c.Flags & LyricMatchFlags.TitleFuzzy) == 0).ToList();
            if (nonFuzzy.Count > 0) candidates = nonFuzzy;

            candidates.Sort(Compare);

            if (candidates.Count > 1 &&
                candidates[0].Score - candidates[1].Score <= 2 &&
                candidates[0].IsExactTitle == candidates[1].IsExactTitle)
            {
                candidates[0] = WithFlag(candidates[0], LyricMatchFlags.Ambiguous);
                Debug.WriteLine($"[歌词匹配] 歧义（取字典序靠前者）：{candidates[0]} vs {candidates[1]}");
            }

            int limit = Math.Max(1, _options.MaxCandidates);
            if (candidates.Count > limit)
                candidates = candidates.Take(limit).ToList();

            return candidates;
        }

        private static int Compare(LyricMatch a, LyricMatch b)
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;

            c = Flag(b, LyricMatchFlags.TitleExact).CompareTo(Flag(a, LyricMatchFlags.TitleExact));
            if (c != 0) return c;

            c = Flag(b, LyricMatchFlags.ArtistMatched).CompareTo(Flag(a, LyricMatchFlags.ArtistMatched));
            if (c != 0) return c;

            c = Flag(b, LyricMatchFlags.AlbumMatched).CompareTo(Flag(a, LyricMatchFlags.AlbumMatched));
            if (c != 0) return c;

            c = Path.GetFileName(a.Path).Length.CompareTo(Path.GetFileName(b.Path).Length);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        }

        private static int Flag(LyricMatch match, LyricMatchFlags flag)
            => ((match.Flags & flag) != 0) ? 1 : 0;

        private static LyricMatch WithFlag(LyricMatch match, LyricMatchFlags flag)
            => new LyricMatch
            {
                Path = match.Path,
                Score = match.Score,
                Flags = match.Flags | flag,
                Detail = match.Detail + ", ambiguous",
            };

        /// <summary>一次搜索期间复用的元数据快照（每条候选只做文件侧规范化）。</summary>
        private sealed class Query
        {
            public string TitleCore = "";
            public string TitleFull = "";
            public string[] TitleTokens = Array.Empty<string>();

            /// <summary>标题主干之外的限定 token（如 `Song (Live)` 里的 live）：文件名命中 +4、缺失 −4。</summary>
            public string[] TitleDetailTokens = Array.Empty<string>();

            public string ArtistCore = "";
            public string ArtistFull = "";
            public string[] ArtistTokens = Array.Empty<string>();
            public string[] ArtistVariants = Array.Empty<string>();

            public string AlbumCore = "";
            public string[] AlbumTokens = Array.Empty<string>();

            /// <summary>Title/Artist/Album 的 token 并集：文件名里这些 token 视为"已解释"。</summary>
            public HashSet<string> KnownTokens = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>KnownTokens 里长度 ≥2 的 CJK token（用于"歌手歌名"这类无空格文件名的子串解释）。</summary>
            public string[] KnownCjkTokens = Array.Empty<string>();

            public HashSet<string> ExtensionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Query BuildQuery(
            string? title, string? artist, string? album, IReadOnlyCollection<string>? extensions)
        {
            var query = new Query();

            string titleText = LyricTextNormalizer.CleanMetadata(title);
            query.TitleFull = LyricTextNormalizer.Normalize(titleText);
            query.TitleCore = LyricTextNormalizer.NormalizeCore(titleText);
            if (query.TitleCore.Length == 0) query.TitleCore = query.TitleFull;
            query.TitleTokens = LyricTextNormalizer.Tokens(query.TitleCore);
            query.TitleDetailTokens = LyricTextNormalizer.Tokens(query.TitleFull)
                .Where(t => !query.TitleTokens.Contains(t) && !IsNumericToken(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            string artistText = LyricTextNormalizer.CleanMetadata(artist);
            query.ArtistFull = LyricTextNormalizer.Normalize(artistText);
            query.ArtistCore = LyricTextNormalizer.NormalizeCore(artistText);
            query.ArtistTokens = LyricTextNormalizer.Tokens(query.ArtistFull);
            query.ArtistVariants = LyricTextNormalizer.ArtistVariants(artistText);

            string albumText = LyricTextNormalizer.CleanMetadata(album);
            query.AlbumCore = LyricTextNormalizer.NormalizeCore(albumText);
            query.AlbumTokens = LyricTextNormalizer.Tokens(query.AlbumCore);

            foreach (string token in query.TitleTokens) query.KnownTokens.Add(token);
            foreach (string token in query.TitleDetailTokens) query.KnownTokens.Add(token);
            foreach (string token in query.ArtistTokens) query.KnownTokens.Add(token);
            foreach (string token in query.AlbumTokens) query.KnownTokens.Add(token);

            query.KnownCjkTokens = query.KnownTokens
                .Where(t => t.Length >= 2 && LyricTextNormalizer.IsCjk(t))
                .ToArray();

            if (extensions != null)
            {
                foreach (string ext in extensions)
                {
                    string normalized = LyricParserRegistry.NormalizeExtension(ext);
                    if (normalized.Length > 0) query.ExtensionSet.Add(normalized);
                }
            }

            return query;
        }

        /// <summary>
        /// 给单个文件打分。标题没有信号直接返回 null（不参与排序）；
        /// 其余维度只加分/按未解释 token 扣分，保证"标题命中但歌手/专辑对不上"仍有回退。
        /// </summary>
        private LyricMatch? Score(Query query, string file, string root)
        {
            string stemRaw = LyricTextNormalizer.FileStem(file);
            string stemFull = LyricTextNormalizer.Normalize(stemRaw);
            string stemCore = LyricTextNormalizer.NormalizeCore(stemRaw);
            if (stemCore.Length == 0) stemCore = stemFull;
            if (stemCore.Length == 0) return null;

            string[] stemFullTokens = LyricTextNormalizer.Tokens(stemFull);
            string[] stemCoreTokens = LyricTextNormalizer.Tokens(stemCore);
            string[] segments = LyricTextNormalizer.DirectorySegments(root, file);

            // ---------- 标题（唯一硬条件） ----------
            var flags = LyricMatchFlags.None;
            string[] coreExtras = Unexplained(stemCoreTokens, query);
            int titleScore;

            if (string.Equals(stemCore, query.TitleCore, StringComparison.Ordinal))
            {
                titleScore = 60;
                flags |= LyricMatchFlags.TitleExact;
            }
            else if (ContainsTitle(stemCore, stemCoreTokens, query.TitleCore, query.TitleTokens))
            {
                titleScore = coreExtras.Length == 0 ? 55 : 50;
                flags |= LyricMatchFlags.TitleContains;
            }
            else if (AllTokensPresent(stemCoreTokens, query.TitleTokens))
            {
                titleScore = coreExtras.Length == 0 ? 45 : 40;
                flags |= LyricMatchFlags.TitleTokens;
            }
            else if (_options.EnableFuzzy)
            {
                double similarity = BestTitleSimilarity(stemCore, stemCoreTokens, query.TitleCore);
                if (similarity >= 0.85)
                {
                    titleScore = 30;
                    flags |= LyricMatchFlags.TitleFuzzy;
                }
                else if (similarity >= 0.72)
                {
                    titleScore = 22;
                    flags |= LyricMatchFlags.TitleFuzzy;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            // ---------- 歌手：只加分，匹配不到不淘汰；同值重复按几何衰减 ----------
            int artistScore = 0;
            bool artistMatched = query.ArtistVariants.Length > 0 &&
                (MatchesArtist(stemFull, stemFullTokens, query.ArtistVariants) ||
                 segments.Any(s => MatchesArtist(s, LyricTextNormalizer.Tokens(s), query.ArtistVariants)));

            if (artistMatched)
            {
                artistScore = 25;
            }
            else if (query.ArtistCore.Length >= 2 &&
                     (stemFull.Contains(query.ArtistCore, StringComparison.Ordinal) ||
                      segments.Any(s => s.Contains(query.ArtistCore, StringComparison.Ordinal))))
            {
                artistMatched = true;
                artistScore = 20;
            }

            int artistHits = CountArtistHits(stemFull, stemFullTokens, query);
            int artistRepeat = RepeatBonus(artistScore, artistHits);
            artistScore += artistRepeat;

            if (artistMatched) flags |= LyricMatchFlags.ArtistMatched;

            // ---------- 专辑：基础分 + 同值重复几何衰减 ----------
            int albumScore = 0;
            bool albumMatched = false;
            bool albumInPath = false;
            int albumHits = CountAlbumHits(stemCore, stemCoreTokens, query);
            int albumRepeat = 0;

            if (query.AlbumTokens.Length > 0)
            {
                albumInPath = segments.Any(s => string.Equals(s, query.AlbumCore, StringComparison.Ordinal) ||
                                                LyricTextNormalizer.ContainsSequence(
                                                    LyricTextNormalizer.Tokens(s), query.AlbumTokens));

                if (albumInPath)
                {
                    albumMatched = true;
                    albumScore = 20;
                }

                if (albumHits > 0)
                {
                    albumMatched = true;
                    albumScore = albumScore == 0 ? 15 : albumScore + 5; // 目录 + 文件名独立证据
                }

                albumRepeat = RepeatBonus(15, albumHits);
                albumScore += albumRepeat;
            }

            if (albumMatched) flags |= LyricMatchFlags.AlbumMatched;

            bool artistInPath = query.ArtistVariants.Length > 0 &&
                segments.Any(s => MatchesArtist(s, LyricTextNormalizer.Tokens(s), query.ArtistVariants));
            int pathBonus = (albumInPath || artistInPath) ? 8 : 0;

            // ---------- 标题限定词（Song (Live) 里的 live）：命中 +4 / 缺失 −4 ----------
            int detailScore = 0;
            if (query.TitleDetailTokens.Length > 0)
            {
                int hit = 0, missing = 0;
                foreach (string token in query.TitleDetailTokens)
                {
                    if (stemFullTokens.Contains(token)) hit++;
                    else missing++;
                }
                detailScore = Math.Max(-8, Math.Min(8, (hit - missing) * 4));
            }

            // ---------- 覆盖率：文件名（含括号内容）里没被三个元数据解释的 token ----------
            string[] fullExtras = Unexplained(stemFullTokens, query);
            int penalty = -Math.Min(10, fullExtras.Length * 2);

            int score = Math.Max(0, titleScore + artistScore + albumScore + pathBonus + detailScore + penalty);
            if (score < _options.MinimumScore) return null;

            string detail =
                $"title={TitleKind(flags)}, artist={(artistMatched ? "hit" : "-")}(hits={artistHits},repeat={artistRepeat}), " +
                $"album={(albumMatched ? (albumInPath ? "dir" : "name") : "-")}(hits={albumHits},repeat={albumRepeat}), " +
                $"detail={detailScore}, extras={fullExtras.Length}, pathBonus={pathBonus}";

            return new LyricMatch
            {
                Path = file,
                Score = score,
                Flags = flags,
                Detail = detail,
            };
        }

        /// <summary>专辑完整值在文件名中的出现次数；同一值重复出现才触发几何奖励。</summary>
        private static int CountAlbumHits(string stemCore, string[] stemTokens, Query query)
        {
            if (query.AlbumTokens.Length == 0) return 0;

            int hits = CountOccurrences(stemTokens, query.AlbumTokens);
            if (hits == 0)
            {
                string compact = query.AlbumCore.Replace(" ", "");
                if (compact.Length >= 2 && LyricTextNormalizer.IsCjk(compact))
                {
                    hits = CountSubstringOccurrences(stemCore, compact);
                }
            }
            return hits;
        }

        /// <summary>歌手完整值在文件名中的最大出现次数（多歌手变体取最匹配的一个，避免变体相加）。</summary>
        private static int CountArtistHits(string stemFull, string[] stemTokens, Query query)
        {
            int best = 0;
            foreach (string variant in query.ArtistVariants)
            {
                string[] tokens = LyricTextNormalizer.Tokens(variant);
                if (tokens.Length == 0) continue;

                int hits = CountOccurrences(stemTokens, tokens);
                if (hits == 0 && variant.Length >= 2 && LyricTextNormalizer.IsCjk(variant))
                {
                    hits = CountSubstringOccurrences(stemFull, variant);
                }
                if (hits > best) best = hits;
            }
            return best;
        }

        /// <summary>非重叠统计完整 token 序列出现次数（多词字段算一次）。</summary>
        private static int CountOccurrences(string[] haystack, string[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return 0;

            int count = 0;
            int i = 0;
            while (i <= haystack.Length - needle.Length)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    count++;
                    i += needle.Length;
                }
                else
                {
                    i++;
                }
            }
            return count;
        }

        /// <summary>非重叠统计紧凑 CJK 子串出现次数。</summary>
        private static int CountSubstringOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;

            int count = 0;
            int start = 0;
            while (start <= text.Length - needle.Length)
            {
                int found = text.IndexOf(needle, start, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                start = found + needle.Length;
            }
            return count;
        }

        /// <summary>
        /// 第 1 次命中拿完整 base；同一值第 2 次 +base/2、第 3 次 +base/4……
        /// 等比收敛到 2×base；额外奖励限制为不超过 base。
        /// </summary>
        private static int RepeatBonus(int baseScore, int hits)
        {
            if (baseScore <= 0 || hits <= 1) return 0;

            int bonus = 0;
            int add = baseScore / 2;
            for (int n = 1; n < hits && add > 0; n++)
            {
                bonus += add;
                add /= 2;
            }
            return Math.Min(bonus, baseScore);
        }

        /// <summary>标题是否作为连续 token / CJK 子串出现在文件名主干里。</summary>
        private static bool ContainsTitle(
            string stemCore, string[] stemTokens, string titleCore, string[] titleTokens)
        {
            if (titleTokens.Length == 0) return false;

            if (LyricTextNormalizer.ContainsSequence(stemTokens, titleTokens)) return true;

            // 中文标题通常没有空格：允许"歌名"是"歌手歌名"这类紧凑文件名的子串
            if (titleTokens.Length == 1 &&
                titleTokens[0].Length >= 2 &&
                LyricTextNormalizer.IsCjk(titleTokens[0]) &&
                stemCore.Contains(titleTokens[0], StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool AllTokensPresent(string[] stemTokens, string[] titleTokens)
        {
            if (titleTokens.Length == 0) return false;

            var set = new HashSet<string>(stemTokens, StringComparer.Ordinal);
            return titleTokens.All(set.Contains);
        }

        /// <summary>歌手变体是否作为连续 token / CJK 子串出现在给定文本里。</summary>
        private static bool MatchesArtist(string normalized, string[] tokens, string[] variants)
        {
            foreach (string variant in variants)
            {
                if (variant.Length == 0) continue;

                string[] variantTokens = LyricTextNormalizer.Tokens(variant);
                if (variantTokens.Length == 0) continue;

                if (LyricTextNormalizer.ContainsSequence(tokens, variantTokens)) return true;

                if (variantTokens.Length == 1 &&
                    LyricTextNormalizer.IsCjk(variantTokens[0]) &&
                    normalized.Contains(variantTokens[0], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 文件名里"没有被 Title/Artist/Album 解释"的 token。
        /// 不是黑名单：任何多出来的 `live`、`320k`、`feat` 都会自然落进来。
        /// 纯数字（年份/音轨号残渣）不算噪声，直接忽略。
        /// </summary>
        private static string[] Unexplained(string[] tokens, Query query)
        {
            if (tokens.Length == 0) return Array.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string token in tokens)
            {
                if (!seen.Add(token)) continue;
                if (IsExplained(token, query)) continue;
                if (IsNumericToken(token)) continue;
                result.Add(token);
            }

            return result.ToArray();
        }

        private static bool IsExplained(string token, Query query)
        {
            if (query.KnownTokens.Contains(token)) return true;

            // 紧凑 CJK 文件名（"歌手歌名.lrc"）没有分隔符，允许用已知 CJK token 做子串解释
            if (LyricTextNormalizer.IsCjk(token))
            {
                foreach (string known in query.KnownCjkTokens)
                {
                    if (token.Contains(known, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        private static bool IsNumericToken(string token)
        {
            if (token.Length == 0) return false;
            foreach (char c in token)
            {
                if (!char.IsDigit(c)) return false;
            }
            return true;
        }

        /// <summary>模糊回退：整串相似度 + 每个 token 对标题的相似度，取最大值。</summary>
        private static double BestTitleSimilarity(string stemCore, string[] stemTokens, string titleCore)
        {
            double best = LyricTextNormalizer.Similarity(stemCore, titleCore);

            foreach (string token in stemTokens)
            {
                double similarity = LyricTextNormalizer.Similarity(token, titleCore);
                if (similarity > best) best = similarity;
            }

            return best;
        }

        private static string TitleKind(LyricMatchFlags flags)
        {
            if ((flags & LyricMatchFlags.TitleExact) != 0) return "exact";
            if ((flags & LyricMatchFlags.TitleContains) != 0) return "contains";
            if ((flags & LyricMatchFlags.TitleTokens) != 0) return "tokens";
            if ((flags & LyricMatchFlags.TitleFuzzy) != 0) return "fuzzy";
            return "-";
        }
    }
}
