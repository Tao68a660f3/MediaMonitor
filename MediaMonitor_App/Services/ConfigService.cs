using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaMonitor.Core;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 通用配置服务（**逐项容错**的读写）。
    ///
    /// <para>两种协议模式各用一个实例、各读各的文件：<c>config.json</c>（Legacy）与
    /// <c>config.new.json</c>（New）。公共项各自留一份、允许不一致 —— 规则只有一条：
    /// <i>一切从"当前模式的配置文件"读，写回同一个文件</i>。</para>
    ///
    /// <para>与"整体反序列化 + 一个 catch 全丢"的区别：</para>
    /// <list type="number">
    /// <item>先构建默认配置（<see cref="CreateDefault"/>），再用 JsonDocument **逐项**覆盖；</item>
    /// <item>某一项类型/格式非法（如 "LineLimit": "abc"）时，**只回退该项到默认值**并在控制台说明原因，其余项全部保留；</item>
    /// <item>文件里出现未知键（改名/废弃项）只提示并忽略；</item>
    /// <item>只有 JSON 结构本身损坏（括号不闭合等）才会整体回退默认配置。</item>
    /// </list>
    /// </summary>
    public class ConfigService<T> where T : class, new()
    {
        private readonly string _configPath;

        // 静态字段在泛型类里是"每个 T 一份"，两种模式的序列化选项互不影响
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            WriteIndented = true, // 生成易读的格式
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() } // 让枚举(如 TransportType)在JSON中显示为字符串
        };

        public T Current
        {
            get; private set;
        }

        /// <summary>本实例读写的配置文件全路径</summary>
        public string FilePath => _configPath;

        public ConfigService(string fileName = "config.json")
        {
            // 获取程序运行目录下的路径
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            Current = Load();
        }

        /// <summary>默认配置（派生类可覆盖，给出该模式自己的默认值）</summary>
        protected virtual T CreateDefault() => new T();

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
        public T Load()
        {
            T cfg = CreateDefault();

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
        private void ApplyJsonTo(JsonElement root, T cfg)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("[配置] config.json 顶层不是 JSON 对象，整体使用默认配置");
                return;
            }

            var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

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

        /// <summary>
        /// 按属性名（大小写不敏感）找可写的公开属性。
        /// 只跳过"彻底不参与读写"的 [JsonIgnore]（Condition = Always，如 Encoding / TargetDeviceId）；
        /// 带 Condition = WhenWritingNull 之类的项（如 WindowBounds）仍要正常读取。
        /// </summary>
        private static PropertyInfo? FindWritableProperty(PropertyInfo[] props, string jsonName)
        {
            foreach (var p in props)
            {
                if (p.SetMethod?.IsPublic != true)
                    continue;

                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
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
        public void Update(T newConfig)
        {
            Current = newConfig ?? CreateDefault();
            Save();
        }
    }

    /// <summary>
    /// Legacy 模式的配置服务（`config.json`）—— **行为与泛型化之前完全一致**：
    /// 保留原类名与用法，Legacy 侧代码一行未改（默认值仍在，逐项容错逻辑继承自泛型基类）。
    /// </summary>
    public sealed class ConfigService : ConfigService<PackageConfig>
    {
        public ConfigService(string fileName = "config.json") : base(fileName)
        {
        }

        protected override PackageConfig CreateDefault()
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