using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MediaMonitor.Services
{
    public class WordInfo { public TimeSpan Time { get; set; } public string Word { get; set; } = ""; }

    public class LyricLine
    {
        public TimeSpan Time { get; set; }
        public string Content { get; set; } = "";
        public string Translation { get; set; } = "";
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public bool IsEmpty => string.IsNullOrEmpty(Content) && string.IsNullOrEmpty(Translation);
    }

    public class LyricService
    {
        public string LyricFolder { get; set; } = "";
        public string[] FileNamePatterns { get; set; } = { "{Artist} - {Title}", "{Title} - {Artist}", "{Title}" };
        public string? CurrentLyricPath { get; private set; }

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

        // LRC 头部 offset 标签：[offset:N]（毫秒，可带 +/-，允许空格与大小写差异）
        // 这里只负责取出文件原值，符号语义（负值 = 歌词偏快、需延后）见 ApplyOffset
        private static readonly Regex OffsetRegex =
            new Regex(@"^\s*\[offset\s*:\s*(?<v>[+-]?\d+)\s*\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        public void LoadAndParse(string title, string artist)
        {
            Debug.WriteLine($"尝试载入歌词{title}-{artist}");
            string? newPath = null;
            var newLines = new List<LyricLine>();

            // --- 闸门 1：拦截无效元数据 ---
            if (string.IsNullOrWhiteSpace(title) || title.Length < 1)
            {
                PublishLyrics(newLines, newPath, 0);
                return;
            }
            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder))
            {
                PublishLyrics(newLines, newPath, 0);
                return;
            }

            // 1. 原有的非法字符过滤
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();

            // 2. 增强清洗
            string cT = Regex.Replace(sT, @"\.(mp3|flac|wav|m4a|ape|ogg|dsf)$", "", RegexOptions.IgnoreCase);

            // --- 闸门 2：如果清洗完标题变空了（比如原标题就是 ".mp3"），立即止损 ---
            if (string.IsNullOrWhiteSpace(cT))
            {
                PublishLyrics(newLines, newPath, 0);
                return;
            }

            var files = Directory.GetFiles(LyricFolder, "*.lrc", SearchOption.TopDirectoryOnly);

            // 3. 【第一阶段】绝对精准匹配
            foreach (var pattern in FileNamePatterns)
            {
                string[] titlesToTry = { cT, sT };
                foreach (var t in titlesToTry.Distinct().Where(x => !string.IsNullOrEmpty(x)))
                {
                    string targetName = pattern.Replace("{Artist}", sA).Replace("{Title}", t) + ".lrc";
                    string targetNoSpace = targetName.Replace(" ", "").ToLower();

                    var match = files.FirstOrDefault(f =>
                    {
                        string actualName = Path.GetFileName(f).Replace(" ", "").ToLower();
                        return actualName == targetNoSpace;
                    });

                    if (match != null)
                    {
                        newPath = match;
                        int offsetMs = ParseInto(newLines, newPath);
                        PublishLyrics(newLines, newPath, offsetMs);
                        return;
                    }
                }
            }

            cT = Regex.Replace(cT, @"\s*[\(\[].*?[\)\]]\s*", "").Trim();

            // 4. 【第二阶段】模糊匹配（增加非空检查，防止 Contains("")）
            if (newPath == null && !string.IsNullOrEmpty(cT))
            {
                newPath = files.FirstOrDefault(f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    // 只有当歌手名也不为空时才做双重匹配
                    bool artistMatch = !string.IsNullOrEmpty(sA) && name.Contains(sA, StringComparison.OrdinalIgnoreCase);
                    return artistMatch && name.Contains(cT, StringComparison.OrdinalIgnoreCase);
                }) ?? files.FirstOrDefault(f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    return name.Contains(cT, StringComparison.OrdinalIgnoreCase);
                });
            }

            int parsedOffsetMs = 0;
            if (newPath != null)
            {
                parsedOffsetMs = ParseInto(newLines, newPath);
            }
            PublishLyrics(newLines, newPath, parsedOffsetMs);
        }

        /// <summary>
        /// 原子发布歌词：一次性替换共享引用并递增代际号。
        /// 这是唯一修改 Lines / CurrentLyricPath / CurrentOffsetMs / Generation 的地方，
        /// 保证读取方永远看到"完整的新列表"或"完整的旧列表"，绝不看到半成品。
        /// </summary>
        private void PublishLyrics(List<LyricLine> newLines, string? newPath, int offsetMs)
        {
            Lines = newLines;
            CurrentLyricPath = newPath;
            CurrentOffsetMs = offsetMs;
            Generation++;
        }

        // 在 ParseInto 方法中，确保对 Words 处理的健壮性
        // 解析进调用方传入的目标列表（局部构建），不触碰共享字段
        // 返回文件头部 [offset:N] 标签的原始值（毫秒，无标签或标签非法时为 0；符号语义见 ApplyOffset）
        private int ParseInto(List<LyricLine> target, string path)
        {
            var raw = File.ReadAllLines(path);
            // 宽容正则，匹配 [00:00.00] 或 <00:00.00>
            var lRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}(?:\.\d{2,3})?)[\]\>](?<c>.*)$");
            var wRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}\.\d{2,3})[\]\>](?<w>[^\[\<]*)");

            // --- 头部 offset 标签预扫描：[offset:+/-毫秒] ---
            int offsetMs = ParseHeaderOffset(raw, lRegex);

            foreach (var line in raw)
            {
                var m = lRegex.Match(line.Trim());
                if (!m.Success) continue;

                if (TimeSpan.TryParse("00:" + m.Groups["t"].Value, out TimeSpan t))
                {
                    string contentBody = m.Groups["c"].Value.Trim();

                    // 翻译行处理：如果时间戳相同且内容不含逐字标签，视为翻译
                    var existing = target.FirstOrDefault(l => Math.Abs((l.Time - t).TotalMilliseconds) < 50);
                    if (existing != null && !wRegex.IsMatch(contentBody))
                    {
                        existing.Translation = contentBody;
                        continue;
                    }

                    var newLine = new LyricLine { Time = t };
                    var wordMatches = wRegex.Matches(contentBody);

                    if (wordMatches.Count > 0) // 逐字模式
                    {
                        // --- 修复首字丢失：检查第一个标签前是否有文字 ---
                        string headText = contentBody.Substring(0, wordMatches[0].Index).Trim();
                        if (!string.IsNullOrEmpty(headText))
                        {
                            // 第一个字的时间就是整行的起始时间 t (即偏移量为0)
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

                    target.Add(newLine);
                }
            }

            // --- 头部 [offset:N] 标签：整体平移时间轴（新时间 = 标签时间 - offset，详见 ApplyOffset 注释）---
            ApplyOffset(target, offsetMs);

            target.Sort((a, b) => a.Time.CompareTo(b.Time));
            return offsetMs;
        }

        /// <summary>
        /// 预扫描歌词文件头部，取第一个有效的 [offset:N] 标签（毫秒，支持 +/-、空格与大小写）。
        /// 只扫描到第一个带时间戳的歌词行为止，避免把正文里的 [offset:..] 误当标签；
        /// 与 [ti:]/[ar:]/[by:] 等元数据行任意先后顺序都能正确识别。
        /// 无标签、标签在正文中或数值非法时返回 0（即不偏移，保持原有行为）。
        /// 返回值是文件里写的【原始】数值（不取反），真正的时间平移在 ApplyOffset 里完成。
        /// </summary>
        private static int ParseHeaderOffset(string[] raw, Regex lRegex)
        {
            foreach (var line in raw)
            {
                string trimmed = line.Trim().TrimStart('\uFEFF');
                if (lRegex.IsMatch(trimmed)) break;      // 已进入歌词正文，停止扫描

                var m = OffsetRegex.Match(trimmed);
                if (m.Success && int.TryParse(m.Groups["v"].Value, out int v))
                    return v;                            // 只认第一个有效标签
            }
            return 0;
        }

        /// <summary>
        /// 应用 LRC 头部 [offset:N] 标签：把每一行（含行内逐字）的时间整体平移，使歌词与音频重新对齐。
        ///
        /// 符号语义（Negative = 歌词比音频"快"，即唱早了，需要整体【延后】）：
        ///     N &lt; 0 → 歌词偏快 → 时间 +|N|（延后）
        ///     N &gt; 0 → 歌词偏慢 → 时间 -N  （提前）
        /// 统一写成：新时间 = 标签时间 - offset。
        /// 例：[offset:-2800] 且首行是 [00:00.20]，结果是 00:03.00（整行延后 2.8 秒）。
        ///
        /// 唯一的额外保护：若某行平移后会落到 0ms 之前（说明这一行本应在曲目开始前就唱完），
        /// 则只把【该行】的平移量收敛到"让该行正好从 0ms 起"，行内逐字共用同一平移量。
        /// 原因：PackageBuilder 里是 (uint)startTime.TotalMilliseconds，负数会翻转成约 42.9 亿 ms
        /// 直接污染硬件端；而逐字偏移是相对量，必须和整行共用同一平移量才不会打乱逐字节奏。
        /// 该收敛只影响这一行，其余行仍按完整 offset 平移，整体对齐量不受影响。
        /// </summary>
        private static void ApplyOffset(List<LyricLine> lines, int offsetMs)
        {
            if (offsetMs == 0 || lines.Count == 0) return;

            // 全程用 long 的 Tick 做整数运算：既没有 double 舍入误差，
            // 也避免 -offsetMs 在 int 域溢出（[offset:-2147483648] 这类离谱值不会把符号又翻回去）
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