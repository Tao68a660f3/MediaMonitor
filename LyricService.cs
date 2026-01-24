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
            Lines.Clear(); CurrentLyricPath = null;
            if (!Directory.Exists(LyricFolder)) return;
            string sT = Regex.Replace(title, @"[\/?:*""<>|]", "_");
            string sA = Regex.Replace(artist ?? "", @"[\/?:*""<>|]", "_");
            var files = Directory.GetFiles(LyricFolder, "*.lrc");
            foreach (var p in FileNamePatterns)
            {
                string target = p.Replace("{Artist}", sA).Replace("{Title}", sT) + ".lrc";
                var m = files.FirstOrDefault(f => Path.GetFileName(f).Equals(target, StringComparison.OrdinalIgnoreCase));
                if (m != null) { CurrentLyricPath = m; break; }
            }
            if (CurrentLyricPath == null) return;

            var raw = File.ReadAllLines(CurrentLyricPath);
            var lRegex = new Regex(@"^\[(?<t>\d{2,}:\d{2}(?:\.\d{2,3})?)\](?<c>.*)$");
            var wRegex = new Regex(@"<(?<t>\d{2,}:\d{2}\.\d{2,3})>(?<w>[^<]*)");

            foreach (var line in raw)
            {
                var m = lRegex.Match(line.Trim());
                if (!m.Success) continue;
                if (TimeSpan.TryParse("00:" + m.Groups["t"].Value, out TimeSpan t))
                {
                    string c = m.Groups["c"].Value.Trim();
                    var exist = Lines.FirstOrDefault(l => Math.Abs((l.Time - t).TotalMilliseconds) < 50);
                    if (exist != null && !wRegex.IsMatch(c)) { exist.Translation = c; continue; }
                    var nl = new LyricLine { Time = t };
                    var wm = wRegex.Matches(c);
                    if (wm.Count > 0)
                    {
                        foreach (Match w in wm)
                        {
                            if (TimeSpan.TryParse("00:" + w.Groups["t"].Value, out TimeSpan wt))
                                nl.Words.Add(new WordInfo { Time = wt, Word = w.Groups["w"].Value });
                        }
                        nl.Content = string.Join("", nl.Words.Select(x => x.Word));
                    }
                    else { nl.Content = c; }
                    Lines.Add(nl);
                }
            }
            Lines = Lines.OrderBy(x => x.Time).ToList();
        }
    }
}