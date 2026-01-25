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
            Lines.Clear();
            CurrentLyricPath = null;
            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder)) return;

            // 1. 原有的非法字符过滤
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();

            // 2. 新增：清洗标题后缀（只删除括号及其内容）
            string cT = Regex.Replace(sT, @"\s*[\(\[].*?[\)\]]\s*", "").Trim();

            var files = Directory.GetFiles(LyricFolder, "*.lrc", SearchOption.TopDirectoryOnly);

            // 3. 搜索逻辑：优先用清洗后的标题 cT，找不到再用原标题 sT
            foreach (var pattern in FileNamePatterns)
            {
                string[] titlesToTry = { cT, sT };
                foreach (var t in titlesToTry.Distinct())
                {
                    // 1. 生成预期的目标文件名
                    string targetName = pattern.Replace("{Artist}", sA).Replace("{Title}", t) + ".lrc";

                    // 2. 核心修改：匹配时忽略空格和大小写
                    // 将目标名和硬盘里的文件名都去掉空格后再对比
                    string targetNoSpace = targetName.Replace(" ", "").ToLower();

                    var match = files.FirstOrDefault(f => {
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

            // 4. 模糊匹配逻辑
            if (CurrentLyricPath == null)
                CurrentLyricPath = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(cT, StringComparison.OrdinalIgnoreCase));

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