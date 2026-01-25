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
        public string[] FileNamePatterns { get; set; } = { "{Artist} - {Title}", "{Title}" };
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

            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_").Trim();
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_").Trim();
            var files = Directory.GetFiles(LyricFolder, "*.lrc", SearchOption.TopDirectoryOnly);

            foreach (var pattern in FileNamePatterns)
            {
                string targetName = pattern.Replace("{Artist}", sA).Replace("{Title}", sT) + ".lrc";
                var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (match != null) { CurrentLyricPath = match; break; }
            }
            if (CurrentLyricPath == null)
                CurrentLyricPath = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(sT, StringComparison.OrdinalIgnoreCase));

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