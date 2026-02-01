using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MediaMonitor
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
        public List<LyricLine> Lines { get; private set; } = new List<LyricLine>();

        // 安全获取指定索引的歌词，越界则返回空行对象
        public LyricLine GetLine(int index)
        {
            if (index < 0 || index >= Lines.Count) return new LyricLine();
            return Lines[index];
        }

        public void LoadAndParse(string title, string artist)
        {
            // 必须首先清空状态，防止没歌词时残留上一首的显示
            Lines.Clear();
            CurrentLyricPath = null;

            // --- 闸门 1：拦截无效元数据 ---
            if (string.IsNullOrWhiteSpace(title) || title.Length < 1) return;
            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder)) return;

            // 1. 原有的非法字符过滤
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();

            // 2. 增强清洗
            string cT = Regex.Replace(sT, @"\.(mp3|flac|wav|m4a|ape|ogg)$", "", RegexOptions.IgnoreCase);

            // --- 闸门 2：如果清洗完标题变空了（比如原标题就是 ".mp3"），立即止损 ---
            if (string.IsNullOrWhiteSpace(cT)) return;

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
                        CurrentLyricPath = match;
                        ParseFile(CurrentLyricPath);
                        return;
                    }
                }
            }

            cT = Regex.Replace(cT, @"\s*[\(\[].*?[\)\]]\s*", "").Trim();

            // 4. 【第二阶段】模糊匹配（增加非空检查，防止 Contains("")）
            if (CurrentLyricPath == null && !string.IsNullOrEmpty(cT))
            {
                CurrentLyricPath = files.FirstOrDefault(f =>
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

            if (CurrentLyricPath != null) ParseFile(CurrentLyricPath);
        }

        // 在 ParseFile 方法中，确保对 Words 处理的健壮性
        private void ParseFile(string path)
        {
            Lines.Clear();
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
                    var existing = Lines.FirstOrDefault(l => Math.Abs((l.Time - t).TotalMilliseconds) < 50);
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
                        newLine.Content = string.Join("", newLine.Words.Select(x => x.Word));
                    }
                    else { newLine.Content = contentBody; }

                    Lines.Add(newLine);
                }
            }
            Lines = Lines.OrderBy(l => l.Time).ToList();
        }
    }
}