using System;
using System.IO;
using System.Reflection;
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
        /// 从磁盘加载配置（**逐项容错**）。
        ///
        /// 与"整体反序列化 + 一个 catch 全丢"的区别：
        ///   1) 先构建默认配置，再用 JsonDocument **逐项**覆盖；
        ///   2) 某一项类型/格式非法（如 "LineLimit": "abc"、"TargetSerialMaster": "0xZZ"）时，
        ///      **只回退该项到默认值**并在控制台说明原因，其余项全部保留；
        ///   3) config.json 里出现未知键（改名/废弃项）只提示并忽略；
        ///   4) 只有 JSON 结构本身损坏（括号不闭合等）才会整体回退默认配置。
        /// </summary>
        public PackageConfig Load()
        {
            var cfg = CreateDefault();

            if (!File.Exists(_configPath))
                return cfg;

            string json;
            try
            {
                json = File.ReadAllText(_configPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[配置] 读取 {_configPath} 失败，整体使用默认配置: {ex.Message}");
                return cfg;
            }

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,  // 容忍 // 与 /* */ 注释
                    AllowTrailingCommas = true                  // 容忍尾随逗号
                });
                ApplyJsonTo(doc.RootElement, cfg);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[配置] config.json 结构损坏，整体回退默认配置: {ex.Message}");
                return CreateDefault();
            }

            return cfg;
        }

        /// <summary>把 JSON 对象逐项套用到配置实例上：单项非法只回退该项，其余照常生效</summary>
        private static void ApplyJsonTo(JsonElement root, PackageConfig cfg)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("[配置] config.json 顶层不是 JSON 对象，整体使用默认配置");
                return;
            }

            var props = typeof(PackageConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var item in root.EnumerateObject())
            {
                var prop = FindWritableProperty(props, item.Name);
                if (prop == null)
                {
                    Console.WriteLine($"[配置] 未知配置项 \"{item.Name}\" 已忽略");
                    continue;
                }

                try
                {
                    object? value = item.Value.Deserialize(prop.PropertyType, _options);
                    if (value is null)
                    {
                        Console.WriteLine($"[配置] 配置项 {prop.Name} 为 null，已忽略（保留默认 {prop.GetValue(cfg)}）");
                        continue;
                    }

                    prop.SetValue(cfg, value);
                }
                catch (Exception ex)
                {
                    // 只回退这一项：实例里保留 CreateDefault() 给的值
                    Console.WriteLine($"[配置] 配置项 {prop.Name} 的值 {item.Value.GetRawText()} 非法，已回退默认 {prop.GetValue(cfg)}：{UnwrapMessage(ex)}");
                }
            }
        }

        /// <summary>按属性名（大小写不敏感）找可写的公开属性；[JsonIgnore] 的项（如 Encoding）跳过</summary>
        private static PropertyInfo? FindWritableProperty(PropertyInfo[] props, string jsonName)
        {
            foreach (var p in props)
            {
                if (p.SetMethod?.IsPublic != true)
                    continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                    continue;
                if (string.Equals(p.Name, jsonName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>取出反射/序列化异常的真正原因（TargetInvocationException 会把内层异常包一层）</summary>
        private static string UnwrapMessage(Exception ex)
            => ex is TargetInvocationException { InnerException: not null } wrapped
                ? wrapped.InnerException!.Message
                : ex.Message;

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
                //LyricFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Lyrics")
                LyricFolder = "M:\\Lyrics_Foobar2000"
            };
        }
    }
}