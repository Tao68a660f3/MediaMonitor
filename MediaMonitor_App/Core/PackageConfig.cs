using System;
using System.Globalization;
using System.Text;

namespace MediaMonitor.Core
{
    public enum TransportType
    {
        Serial, UDP
    }

    /// <summary>
    /// config.json 的强类型映射，读写统一走 <see cref="MediaMonitor.Services.ConfigService"/>。
    ///
    /// <para><b>生效时机（全局约定）</b>：配置在程序启动时读取一次并注入各消费者；
    /// <b>界面上有设置入口</b>的项会在每次界面保存时再次注入，因此改完立即生效；
    /// <b>界面上没有入口的项（下文分区标注的「静默项」）只在启动时生效，改完 config.json 必须重启程序。</b>
    /// 另注意：程序运行中手改 config.json 是无效的 —— 任何一次界面保存都会把内存里的配置整体写回覆盖它。</para>
    ///
    /// <para><b>容错（全局约定）</b>：加载时逐项校验，某一项非法只把<b>该项</b>回退到默认值并打日志，
    /// 不影响其他项；只有 JSON 结构本身损坏（括号不闭合等）才整份回退默认配置。</para>
    ///
    /// <para><b>落盘约定</b>：只有"公开且可写"的属性参与读写；标了
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> 的属性是<b>代码内视图</b>，
    /// 由对应的字符串属性驱动、不直接落盘：
    /// <see cref="Encoding"/> ← <see cref="EncodingName"/>，<see cref="TargetDeviceId"/> ← <see cref="TargetSerialMaster"/>。
    /// 若该特性带 <c>Condition</c>（如 <see cref="WindowBounds"/> 的 WhenWritingNull），则只表示"值为 null 时不写盘"，读写照常。</para>
    /// </summary>
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

        // === 编码 ===
        /// <summary>代码内使用的编码对象（落盘键是 <see cref="EncodingName"/>）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Encoding Encoding { get; set; } = Encoding.UTF8;

        /// <summary>编码名的落盘形态；名字写错时保底 UTF8</summary>
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

        /// <summary>
        /// 同步包 (0x11) 中 currentMs 的时间偏移量(ms)，用于修正传输链路时间偏移。
        /// 正值表示提前（发出的 currentMs = 实际进度 + 偏移），负值表示延后。
        /// 默认 +10ms（提前 10ms）；设为 0 表示无偏移。
        /// 零点保护：真实进度落在 [0, |偏移量|] 区间内时不加偏移，保持起点锚点语义。
        /// 可在界面「同步偏移(ms)」(TxtSyncOffset) 热改并落盘 config.json；
        /// 该值可参考「测延迟」按钮测出的固化延迟(Base) 手动填写。
        /// </summary>
        public int SyncCurrentOffsetMs { get; set; } = 10;

        // === 静默项：仅 config.json 可调，UI 无入口 ===
        // 因为没有界面入口，这些值只会被"启动注入"一次带到运行期（见类注释的「生效时机」）。

        /// <summary>
        /// 发送队列每包间隔(ms)，用于抹平突发流量峰值、避免超过 BLE/UART 物理吞吐上限。
        /// 推荐 5~15，默认 10；设 0 表示不节流（如 UDP 直连场景）。
        /// 消费方：<see cref="MediaMonitor.Services.TransportManager"/>。
        /// </summary>
        public int SendIntervalMs { get; set; } = 10;

        /// <summary>
        /// 「测延迟」连续无回包的**停止**超时(ms)，默认 10000：
        /// 覆盖 ESP32 从 Wi-Fi 休眠唤醒的首包、链路抖动、STM32 主循环正忙等场景。
        /// 消费方：<see cref="MediaMonitor.LatencyTestController"/>（若外部显式设置了更大的
        /// ReplyTimeoutMs，则以显式值为准）。
        /// </summary>
        public int LatencyTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// 「测延迟」连续无回包的**等待告警**阈值(ms)，默认 3000：
        /// 到点只在日志里提示"仍在等待"，不停止测试；真正停止由 <see cref="LatencyTimeoutMs"/> 决定。
        /// </summary>
        public int LatencyWarnMs { get; set; } = 3000;

        /// <summary>
        /// 0x1F 延迟探测 Ping 的目标设备 ID 的**文本形态**（落盘项，代码内请用 <see cref="TargetDeviceId"/>）。
        /// 0x01 = 串口 STM32 主设备，其他设备收到后静默丢弃。
        /// 只认十六进制的一个字节 = 1~2 位（可带 0x/0X 前缀，如 "0x01" / "1" / "ff"；"12" 按十六进制解释为 0x12）；
        /// 超长（"0x001"）、非法字符（"0xZZ"）、空值等一律回退 0x01。
        /// </summary>
        public string TargetSerialMaster
        {
            get => $"0x{TargetDeviceId:X2}";
            set => TargetDeviceId = ParseTargetDeviceId(value);
        }

        // === 窗口布局（静默项：UI 无入口；程序在关闭时写入、启动时校验后读取） ===

        /// <summary>
        /// 窗口位置与大小，格式 "left,top,width,height"（四个十进制整数，逗号分隔，允许空格）。
        /// null = 尚无记录（首装或键被手删），此时启动不做任何设置，窗口位置交给系统；
        /// 值非法（段数不对、含非数字、宽高 ≤ 0、坐标离谱、位置不在当前屏幕内）同样按"无记录"处理，只影响本项。
        /// 解析与校验由 UI/WindowPlacement.cs 负责；本项是程序自动维护的 —— 手改可临时生效，
        /// 但下次关闭窗口会被实际布局覆盖（自我修复）。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? WindowBounds { get; set; } = null;

        // === 内部视图与常量（不落盘，见类注释的「落盘约定」） ===

        /// <summary>代码内使用的目标设备 ID（0x01 = 串口 STM32 主设备）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public byte TargetDeviceId { get; set; } = DefaultTargetDeviceId;

        /// <summary>目标设备 ID 的默认值：0x01 = 串口 STM32 主设备</summary>
        public const byte DefaultTargetDeviceId = 0x01;

        /// <summary>解析 <see cref="TargetSerialMaster"/>：只接受十六进制的一个字节，其余回退默认值，不抛异常</summary>
        internal static byte ParseTargetDeviceId(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DefaultTargetDeviceId;

            string hex = text.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);

            // 一个字节 = 最多两位十六进制；"0x001"、""（只剩前缀）等一律回退
            if (hex.Length is < 1 or > 2)
                return DefaultTargetDeviceId;

            return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
                ? value
                : DefaultTargetDeviceId;
        }

        // === 预留项（暂未启用） ===
        // 界面上的 TxtUpdateRate 输入框已存在，但被 Visibility="Collapsed" 折叠；
        // 启用该逻辑时取消下面注释即可（届时它属于"界面有入口的动态项"）。
        //public int UpdateIntervalMs { get; set; } = 50;

        // 浅拷贝：值类型/字符串成员会被复制到新实例；Encoding 是引用类型，两个实例共享同一个 Encoding 对象
        //（该对象只读使用，无副作用）。注意：全工程当前无调用点，保留作为 API。
        public PackageConfig Clone() => (PackageConfig)this.MemberwiseClone();
    }
}

