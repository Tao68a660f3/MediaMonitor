using MediaMonitor.Core;
using MediaMonitor.Services;
using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaMonitor
{
    /// <summary>
    /// 「测延迟」按钮的完整交互封装（传输方式无关）。
    ///
    /// MainWindow 里只保留三处必要调用：
    ///   1) 构造（必须在 App.LogSvc 初始化之后）：
    ///        _latencyTest = new LatencyTestController(App.TransportMgr, BtnLatencyTest, Dispatcher,
    ///                                                 (msg, color) => App.LogSvc?.LogInfo(msg, color));
    ///   2) 按钮点击： _latencyTest?.Toggle();
    ///   3) 退出清理： _latencyTest?.Dispose();
    ///
    /// 链路行为做到"什么传输方式都能测"：
    ///   - 发帧统一走 <see cref="TransportManager.SendImmediate"/>（直发，绕过节流队列与协议日志）；
    ///   - 收帧统一从 <see cref="TransportManager.OnRawDataReceived"/> 转给
    ///     <see cref="ProtocolLatencyTester.FeedRawData"/> 自行分帧（串口切片 / UDP 整包都吃）；
    ///   所以串口与 UDP 不需要各自写一套逻辑，未来加 BLE 等链路同样即插即用。
    /// </summary>
    public class LatencyTestController
    {
        private readonly TransportManager _transport;
        private readonly Button _button;
        private readonly Dispatcher _dispatcher;
        private readonly Action<string, Brush> _log;

        private ProtocolLatencyTester? _tester;
        private Action<byte[]>? _rawHandler;
        private PackageConfig? _cfg;

        /// <summary>发包间隔(ms)</summary>
        public int PingIntervalMs { get; set; } = 100;

        /// <summary>滑动窗口容量（样本数）</summary>
        public int WindowSize { get; set; } = 30;

        /// <summary>连续无回包超时(ms)：显式 &gt;0 时优先，否则取注入配置的 LatencyTimeoutMs（缺省 10s）</summary>
        public int ReplyTimeoutMs { get; set; } = 0;

        /// <summary>仍在等待的告警阈值(ms)：显式 &gt;0 时优先，否则取注入配置的 LatencyWarnMs（缺省 3s）</summary>
        public int ReplyWarnMs { get; set; } = 0;

        /// <summary>
        /// 注入配置（启动时一次 + 每次 UI 保存时一次）。
        /// 静默项（LatencyTimeoutMs / LatencyWarnMs / TargetSerialMaster，UI 均无入口）
        /// 因此实际只受"启动注入"影响 —— 改 config.json 需重启程序。
        /// </summary>
        public void ApplyConfig(PackageConfig? cfg) => _cfg = cfg;

        /// <summary>空闲态按钮文案</summary>
        public string IdleText { get; set; } = "测延迟";

        public bool IsRunning => _tester?.IsRunning == true;

        public LatencyTestController(TransportManager transport, Button button, Dispatcher dispatcher,
                                     Action<string, Brush> log)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _button = button ?? throw new ArgumentNullException(nameof(button));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _log = log ?? ((_, _) => { });
        }

        /// <summary>按钮点击入口：空闲 → 开始测量；测量中 → 中断并把当前窗口的统计结算到按钮上</summary>
        public void Toggle()
        {
            if (IsRunning)
            {
                _tester!.Stop(); // Stop 触发 Completed，由 ShowResult 回写按钮
                return;
            }

            Start();
        }

        private void Start()
        {
            if (!_transport.IsConnected)
            {
                Log("[延迟测试] 链路未连接：请先点击『开始连接』再测延迟。", isWarn: true);
                SetButtonText(IdleText);
                return;
            }

            _tester?.Dispose();

            // 超时阈值：优先用调用方显式设置的值(>0)，否则用注入的配置（缺省 告警3s / 超时10s）
            var cfg = _cfg;
            int timeoutMs = this.ReplyTimeoutMs > 0
                ? Math.Clamp(this.ReplyTimeoutMs, 1000, 120000)
                : Math.Clamp(cfg?.LatencyTimeoutMs ?? 10000, 1000, 120000);
            int warnMs = this.ReplyWarnMs > 0
                ? Math.Clamp(this.ReplyWarnMs, 500, timeoutMs)
                : Math.Clamp(cfg?.LatencyWarnMs ?? 3000, 500, timeoutMs);

            // 0x1F Ping 的目标设备 ID：config.json 的静态项，缺省 0x01
            byte targetId = cfg?.TargetDeviceId ?? PackageConfig.DefaultTargetDeviceId;

            var tester = new ProtocolLatencyTester(frame => _transport.SendImmediate(frame))
            {
                PingIntervalMs = this.PingIntervalMs,
                TargetSerialMaster = targetId,
                ReplyTimeoutMs = timeoutMs,
                ReplyWarnMs = warnMs,
                LinkAliveProbe = () => _transport.IsConnected   // 超时时区分"对端没回"与"本机链路已断"
            };
            _tester = tester;

            // 文本日志（含告警）→ 外部分色输出
            tester.LogMessage += msg => Log(msg, IsWarnMessage(msg));

            // 每个有效样本 → 按钮进度
            tester.StatsUpdated += stats => SetButtonText($"测量中 {stats.SampleCount}/{tester.WindowSize}");

            // 窗口满 / 超时 / 手动停止 → 结果落到按钮上
            tester.Completed += stats =>
            {
                DetachRaw();
                ShowResult(stats);
            };

            // 订阅底层原始字节：UDP 一次一个整包，串口是任意切片（含粘包/半包）
            _rawHandler = tester.FeedRawData;
            _transport.OnRawDataReceived += _rawHandler;

            tester.Start(WindowSize);
            SetButtonText($"测量中 0/{tester.WindowSize}");

            Log($"[延迟测试] 开始 链路={DescribeLink()} 目标ID=0x{targetId:X2} 间隔={tester.PingIntervalMs}ms 窗口={tester.WindowSize}", isWarn: false);
        }

        public void Dispose()
        {
            DetachRaw();
            _tester?.Dispose();
            _tester = null;
        }

        private void ShowResult(LatencyStats stats)
        {
            if (stats.SampleCount == 0)
            {
                SetButtonText(IdleText);
                SetButtonTip("未采集到有效样本：请确认硬件支持 0x1F 探测帧，或先用 A_tools/mock_esp32_stm32.py 自检");
                return;
            }

            SetButtonText($"延迟 {stats.BaseMs:F2}ms");
            SetButtonTip(
                $"固化延迟 Base(窗口最小) : {stats.BaseMs:F2} ms\n" +
                $"平均延迟 Avg           : {stats.AvgMs:F2} ms\n" +
                $"网络抖动 Jitter        : {stats.JitterMs:F2} ms\n" +
                $"样本数                 : {stats.SampleCount}\n" +
                "（已扣除 STM32 上报的 proc_us；可参考 Base 手动填写「同步偏移」）");
        }

        private void DetachRaw()
        {
            if (_rawHandler != null)
            {
                _transport.OnRawDataReceived -= _rawHandler;
                _rawHandler = null;
            }
        }

        private string DescribeLink()
        {
            var cfg = _cfg;
            if (cfg == null)
                return "未知";

            return cfg.TransportMode == TransportType.Serial
                ? $"串口 {cfg.SerialPortName}@{cfg.BaudRate}"
                : $"UDP {cfg.RemoteIp}:{cfg.RemotePort}";
        }

        private static bool IsWarnMessage(string msg)
            => msg.Contains("告警") || msg.Contains("未收到") || msg.Contains("异常");

        private void Log(string msg, bool isWarn)
            => _log(msg, isWarn ? Brushes.OrangeRed : Brushes.DeepSkyBlue);

        private void SetButtonText(string text) => OnUi(() => _button.Content = text);

        private void SetButtonTip(string tip) => OnUi(() => _button.ToolTip = tip);

        /// <summary>线程安全地回 UI 线程（关闭窗口瞬间的竞态直接静默放弃）</summary>
        private void OnUi(Action action)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;

            try
            {
                _dispatcher.Invoke(action);
            }
            catch (TaskCanceledException) { }
            catch (InvalidOperationException) { }
        }
    }
}
