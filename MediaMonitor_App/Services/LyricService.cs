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
                PublishLyrics(newLines, newPath);
                return;
            }
            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder))
            {
                PublishLyrics(newLines, newPath);
                return;
            }

            // 1. 原有的非法字符过滤
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();

            // 2. 增强清洗
            string cT = Regex.Replace(sT, @"\.(mp3|flac|wav|m4a|ape|ogg)$", "", RegexOptions.IgnoreCase);

            // --- 闸门 2：如果清洗完标题变空了（比如原标题就是 ".mp3"），立即止损 ---
            if (string.IsNullOrWhiteSpace(cT))
            {
                PublishLyrics(newLines, newPath);
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
                        ParseInto(newLines, newPath);
                        PublishLyrics(newLines, newPath);
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

            if (newPath != null)
            {
                ParseInto(newLines, newPath);
            }
            PublishLyrics(newLines, newPath);
        }

        /// <summary>
        /// 原子发布歌词：一次性替换共享引用并递增代际号。
        /// 这是唯一修改 Lines / CurrentLyricPath / Generation 的地方，
        /// 保证读取方永远看到"完整的新列表"或"完整的旧列表"，绝不看到半成品。
        /// </summary>
        private void PublishLyrics(List<LyricLine> newLines, string? newPath)
        {
            Lines = newLines;
            CurrentLyricPath = newPath;
            Generation++;
        }

        // 在 ParseInto 方法中，确保对 Words 处理的健壮性
        // 解析进调用方传入的目标列表（局部构建），不触碰共享字段
        private void ParseInto(List<LyricLine> target, string path)
        {
            var raw = File.ReadAllLines(path);
            // 宽容正则，匹配 [00:00.00] 或 <00:00.00>
            var lRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}(?:\.\d{2,3})?)[\]\>](?<c>.*)$");
            var wRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}\.\d{2,3})[\]\>](?<w>[^\[\<]*)");

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
            target.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }
}