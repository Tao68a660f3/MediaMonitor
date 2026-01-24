using System;
using System.IO;
using System.Text.Json;

namespace MediaMonitor
{
    public class AppConfig
    {
        public string PortName { get; set; } = "";
        public string BaudRate { get; set; } = "115200";
        public int EncodingIndex { get; set; } = 0;
        public string LyricPath { get; set; } = @"C:\Lyrics";
        public string Patterns { get; set; } = "{Artist} - {Title};{Title} - {Artist};{Title}";
        public int ScreenLines { get; set; } = 3;
        public int Offset { get; set; } = 1;
        public bool AdvancedMode { get; set; } = true;
        public bool Incremental { get; set; } = true;
        public bool TransOccupies { get; set; } = true;
    }

    public static class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppConfig Load()
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            try
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch { return new AppConfig(); }
        }

        public static void Save(AppConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}