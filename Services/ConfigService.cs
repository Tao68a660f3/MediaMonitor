using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaMonitor.Core;

namespace MediaMonitor.Services
{
    public class ConfigService
    {
        private readonly string _configPath;
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            WriteIndented = true, // 生成易读的格式
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() } // 让枚举(如 TransportType)在JSON中显示为字符串
        };

        public PackageConfig Current
        {
            get; private set;
        }

        public ConfigService(string fileName = "config.json")
        {
            // 获取程序运行目录下的路径
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            Current = Load();
        }

        /// <summary>
        /// 从磁盘加载配置，如果文件不存在或损坏则返回默认配置
        /// </summary>
        public PackageConfig Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<PackageConfig>(json, _options);
                    return config ?? CreateDefault();
                }
            }
            catch (Exception ex)
            {
                // 这里可以记录日志，暂时返回默认值保证程序不崩溃
                Console.WriteLine($"配置加载失败: {ex.Message}");
            }

            return CreateDefault();
        }

        /// <summary>
        /// 将当前内存中的配置持久化到磁盘
        /// </summary>
        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, _options);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"配置保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 覆盖当前配置
        /// </summary>
        public void Update(PackageConfig newConfig)
        {
            Current = newConfig ?? CreateDefault();
            Save();
        }

        private PackageConfig CreateDefault()
        {
            return new PackageConfig
            {
                TransportMode = TransportType.Serial,
                SerialPortName = "COM3",
                BaudRate = 115200,
                Encoding = System.Text.Encoding.UTF8,
                IsAdvancedMode = true,
                IsIncremental = true,
                LineLimit = 2,
                Offset = 0,
                SyncIntervalMs = 500,
                LyricFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Lyrics")
            };
        }
    }
}