using System.Text;

namespace MediaMonitor.Core
{
    public enum TransportType
    {
        Serial, UDP
    }

    public class PackageConfig
    {
        // === 传输物理配置 (关键：不能漏) ===
        public TransportType TransportMode { get; set; } = TransportType.Serial;

        // 串口相关
        public string SerialPortName { get; set; } = "COM3";
        public int BaudRate { get; set; } = 115200;

        // UDP 相关
        public string RemoteIp { get; set; } = "192.168.1.100";
        public int RemotePort { get; set; } = 8080;
        public int LocalPort { get; set; } = 8081;

        // 编码
        [System.Text.Json.Serialization.JsonIgnore]
        public Encoding Encoding { get; set; } = Encoding.UTF8;

        public string EncodingName
        {
            get => Encoding?.WebName ?? "utf-8";
            set
            {
                try
                {
                    Encoding = Encoding.GetEncoding(value);
                }
                catch
                {
                    Encoding = Encoding.UTF8; // 万一配置文件里的名字写错了，保底用 UTF8
                }
            }
        }
        // === 协议行为配置 ===
        /// <summary>
        /// True: 发送 0x12/13/14 协议包
        /// False: 直接发送 Raw 文本 (Encoding.GetBytes)
        /// </summary>
        public bool IsAdvancedMode { get; set; } = true;

        /// <summary>
        /// 差分模式：是否只发送新进入视野或内容变动的行
        /// </summary>
        public bool IsIncremental { get; set; } = true;

        // === 排版与逻辑配置 ===
        public int LineLimit { get; set; } = 2;             // 屏幕显示的行数限制
        public int Offset { get; set; } = 0;                // 歌词行偏移
        public bool TransOccupies { get; set; } = true;     // 翻译是否占独立行
        public string LyricFolder { get; set; } = "";       // 歌词搜索路径

        // === 时控配置 ===
        /// <summary>
        /// 进度同步包 (0x11) 的发送间隔
        /// </summary>
        public int SyncIntervalMs { get; set; } = 500;

        // 深拷贝方法，确保 UI 修改配置时不影响后台正在运行的实例
        public PackageConfig Clone() => (PackageConfig)this.MemberwiseClone();
    }
}

