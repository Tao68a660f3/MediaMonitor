using System;

namespace MediaMonitor.Core
{
    /// <summary>
    /// 新协议模式的配置（落盘 `config.new.json`）。
    ///
    /// <para><b>与 Legacy 完全分开</b>：它只管新协议需要的东西，公共项（歌词目录、窗口布局）
    /// **自己留一份**，与 `config.json` 各存各的、允许不一致 —— 读写规则只有一条：
    /// <i>一切从"当前模式的配置文件"读，写回同一个文件</i>。</para>
    ///
    /// <para><b>协议模式本身不在配置文件里</b>：模式是运行期选择（<see cref="ProtocolMode"/>），不落盘。</para>
    ///
    /// <para><b>刻意不放设置项的东西</b>：
    /// 歌词排版（缓冲区行数 / 翻译占行）由 ESP32 决定，PC 界面预览只是调试辅助；
    /// 歌词时间平移由 `.lrc` 自带的 `[offset:N]` 标签在解析阶段自动应用，无需全局偏移。</para>
    ///
    /// <para>容错约定与 <see cref="PackageConfig"/> 一致：由 <see cref="Services.ConfigService"/> 逐项校验，
    /// 某一项非法只回退该项。</para>
    /// </summary>
    public class NewProtocolConfig
    {
        // === 传输（规范 §19）===
        /// <summary>COM(USB CDC) 或 TCP；TCP 时 ESP32 是 Server、PC 是 Client</summary>
        public NewTransportType TransportMode { get; set; } = NewTransportType.Com;

        // COM（USB CDC）
        public string ComPortName { get; set; } = "COM3";
        public int BaudRate { get; set; } = 115200;

        // TCP
        public string TcpRemoteIp { get; set; } = "127.0.0.1";
        public int TcpRemotePort { get; set; } = 9100;

        // === 时间轴（规范 §7）===
        /// <summary>时间轴推送间隔(ms)：100~30000，默认 500（与 Legacy 的 SyncIntervalMs 同量级）</summary>
        public int SyncIntervalMs { get; set; } = 500;

        /// <summary>同步偏移(ms)：正值 = 提前发出，用于补偿链路单向延迟（可参考「测延迟」测出的 Base）</summary>
        public int SyncCurrentOffsetMs { get; set; } = 10;

        // === 歌词 ===
        /// <summary>歌词搜索目录（自己一份，与 Legacy 无关）</summary>
        public string LyricFolder { get; set; } = "";

        // === 静默项（UI 无入口，仅启动 / 切模式时注入）===
        /// <summary>发送队列每帧间隔(ms)，0 = 不节流</summary>
        public int SendIntervalMs { get; set; } = 10;

        /// <summary>ACK 超时(ms)：超时后原帧重传（规范 §5.6）</summary>
        public int AckTimeoutMs { get; set; } = 300;

        /// <summary>ACK 重传次数上限</summary>
        public int AckRetry { get; set; } = 3;

        /// <summary>握手重发间隔(ms)（规范 §5.4）</summary>
        public int HandshakeTimeoutMs { get; set; } = 500;

        /// <summary>资源分片大小(字节)：8B 头 + N 数据，需 &lt; MAX_PAYLOAD_LEN(4096)（规范 §9.4）</summary>
        public int ResourceChunkSize { get; set; } = 1024;

        /// <summary>TCP 断线重连退避起点(ms)</summary>
        public int TcpReconnectBackoffMs { get; set; } = 1000;

        // === 公共（自己一份）===
        /// <summary>窗口位置与大小 "left,top,width,height"；程序自动维护（见 UI/WindowPlacement.cs）</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? WindowBounds { get; set; } = null;
    }
}
