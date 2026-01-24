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
    }

    public class LyricService
    {
        public string LyricFolder { get; set; } = "";
        public string[] FileNamePatterns { get; set; } = { "{Artist} - {Title}", "{Title}" };
        public string? CurrentLyricPath { get; private set; }
        public List<LyricLine> Lines { get; private set; } = new List<LyricLine>();

        public void LoadAndParse(string title, string artist)
        {
            Lines.Clear();
            CurrentLyricPath = null;

            if (string.IsNullOrWhiteSpace(LyricFolder) || !Directory.Exists(LyricFolder)) return;

            // 1. 寻找物理文件
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();

            var files = Directory.GetFiles(LyricFolder, "*.lrc", SearchOption.TopDirectoryOnly);

            foreach (var pattern in FileNamePatterns)
            {
                string targetName = pattern.Replace("{Artist}", sA).Replace("{Title}", sT) + ".lrc";
                var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    CurrentLyricPath = match;
                    break;
                }
            }

            // 如果模式匹配失败，尝试模糊匹配 (标题包含即可)
            if (CurrentLyricPath == null)
            {
                CurrentLyricPath = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(sT, StringComparison.OrdinalIgnoreCase));
            }

            if (CurrentLyricPath == null) return;

            // 2. 解析 LRC
            ParseFile(CurrentLyricPath);
        }

        private void ParseFile(string path)
        {
            var raw = File.ReadAllLines(path);
            // 修改点 1：去掉行首锚定 ^，并将括号匹配改为支持 [ ] 和 < >
            var lRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}(?:\.\d{2,3})?)[\]\>](?<c>.*)$");
            // 修改点 2：逐字正则同样支持 [ ] 和 < >
            var wRegex = new Regex(@"[\[\<](?<t>\d{2,}:\d{2}\.\d{2,3})[\]\>](?<w>[^\[\<]*)");

            foreach (var line in raw)
            {
                // 逻辑保持原样，仅正则表达式变得更宽容
                var m = lRegex.Match(line.Trim());
                if (!m.Success) continue;

                if (TimeSpan.TryParse("00:" + m.Groups["t"].Value, out TimeSpan t))
                {
                    string contentBody = m.Groups["c"].Value.Trim();

                    // 处理翻译逻辑保持原样
                    var existing = Lines.FirstOrDefault(l => Math.Abs((l.Time - t).TotalMilliseconds) < 50);
                    if (existing != null && !wRegex.IsMatch(contentBody))
                    {
                        existing.Translation = contentBody;
                        continue;
                    }

                    var newLine = new LyricLine { Time = t };
                    var wordMatches = wRegex.Matches(contentBody);

                    if (wordMatches.Count > 0)
                    {
                        foreach (Match w in wordMatches)
                        {
                            if (TimeSpan.TryParse("00:" + w.Groups["t"].Value, out TimeSpan wt))
                                // 保持你原始的字段名 Word 和 Time
                                newLine.Words.Add(new WordInfo { Time = wt, Word = w.Groups["w"].Value });
                        }
                        newLine.Content = string.Join("", newLine.Words.Select(x => x.Word));
                    }
                    else
                    {
                        newLine.Content = contentBody;
                    }
                    Lines.Add(newLine);
                }
            }
            Lines = Lines.OrderBy(l => l.Time).ToList();
        }
    }
}