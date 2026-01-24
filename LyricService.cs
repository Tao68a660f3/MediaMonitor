using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MediaMonitor
{
    public class WordInfo
    {
        public TimeSpan Time { get; set; }
        public string Word { get; set; } = "";
    }

    public class LyricLine
    {
        public TimeSpan Time { get; set; }
        public string Content { get; set; } = "";      // 正文
        public string Translation { get; set; } = "";  // 翻译
        public List<WordInfo> Words { get; set; } = new List<WordInfo>(); // 逐字信息
    }

    public class LyricService
    {
        public string LyricFolder { get; set; } = @"C:\Lyrics";
        public string[] FileNamePatterns { get; set; } = { "{Artist} - {Title}", "{Title}" };
        public string? CurrentLyricPath { get; private set; }
        public List<LyricLine> Lines { get; private set; } = new List<LyricLine>();

        public LyricLine? CurrentLine { get; private set; }
        public LyricLine? NextLine { get; private set; }

        // 清空所有缓存
        public void Clear()
        {
            Lines.Clear();
            CurrentLyricPath = null;
            CurrentLine = null;
            NextLine = null;
        }

        public void LoadAndParse(string title, string artist)
        {
            Clear(); // 载入新歌前先彻底清空
            SearchFile(title, artist);

            if (string.IsNullOrEmpty(CurrentLyricPath) || !File.Exists(CurrentLyricPath)) return;

            var rawLines = File.ReadAllLines(CurrentLyricPath);
            // 匹配 [mm:ss.xx]
            var lineRegex = new Regex(@"^\[(?<time>\d{2,}:\d{2}(?:\.\d{2,3})?)\](?<content>.*)$");
            // 匹配 <mm:ss.xx>逐字
            var wordRegex = new Regex(@"<(?<time>\d{2,}:\d{2}\.\d{2,3})>(?<word>[^<]*)");

            foreach (var rawLine in rawLines)
            {
                var match = lineRegex.Match(rawLine.Trim());
                if (!match.Success) continue;

                if (TimeSpan.TryParse("00:" + match.Groups["time"].Value, out TimeSpan time))
                {
                    string fullContent = match.Groups["content"].Value.Trim();

                    // 处理双语：同一时间点出现多次，存入 Translation
                    var existing = Lines.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) < 10);
                    if (existing != null)
                    {
                        existing.Translation = fullContent;
                        continue;
                    }

                    var newLine = new LyricLine { Time = time };
                    var wordMatches = wordRegex.Matches(fullContent);

                    if (wordMatches.Count > 0)
                    {
                        foreach (Match wm in wordMatches)
                        {
                            if (TimeSpan.TryParse("00:" + wm.Groups["time"].Value, out TimeSpan wTime))
                                newLine.Words.Add(new WordInfo { Time = wTime, Word = wm.Groups["word"].Value });
                        }
                        newLine.Content = string.Join("", newLine.Words.Select(w => w.Word));
                    }
                    else
                    {
                        newLine.Content = fullContent;
                    }
                    Lines.Add(newLine);
                }
            }
            Lines = Lines.OrderBy(l => l.Time).ToList();
        }

        private void SearchFile(string title, string artist)
        {
            if (!Directory.Exists(LyricFolder)) return;
            string safeTitle = Regex.Replace(title, @"[\/?:*""<>|]", "_");
            string safeArtist = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_");

            var files = Directory.GetFiles(LyricFolder, "*.lrc");
            // 优先匹配模式
            foreach (var p in FileNamePatterns)
            {
                string target = p.Replace("{Artist}", safeArtist).Replace("{Title}", safeTitle) + ".lrc";
                var m = files.FirstOrDefault(f => Path.GetFileName(f).Equals(target, StringComparison.OrdinalIgnoreCase));
                if (m != null) { CurrentLyricPath = m; return; }
            }
            // 模糊匹配
            CurrentLyricPath = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).ToLower().Contains(safeTitle.ToLower()));
        }

        public void UpdateCurrentStatus(TimeSpan currentTime)
        {
            if (Lines.Count == 0) return;
            int idx = Lines.FindLastIndex(l => l.Time <= currentTime);
            if (idx != -1)
            {
                CurrentLine = Lines[idx];
                NextLine = (idx + 1 < Lines.Count) ? Lines[idx + 1] : null;
            }
        }
    }
}