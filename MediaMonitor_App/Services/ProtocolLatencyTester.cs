using MediaMonitor.Core;
using MediaMonitor.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 一次测量的统计快照。
    /// Base   = 固化延迟 D_base（窗口内最小值，代表物理极速链路）
    /// Avg    = 窗口内均值
    /// Jitter = 窗口内样本偏离基线(Base)的平均偏差
    /// </summary>
    public class LatencyStats
    {
        public int SampleCount { get; init; }
        public double BaseMs { get; init; }
        public double AvgMs { get; init; }
        public double JitterMs { get; init; }
        public double LastMs { get; init; }

        public override string ToString()
            => $"Base={BaseMs:F2}ms Avg={AvgMs:F2}ms Jitter={JitterMs:F2}ms 样本={SampleCount}";
    }

    /// <summary>
    /// 全链路单向延迟测试核心（传输无关）。
    ///
    /// 链路：PC --(0x1F Ping)--> 链路 --&gt; 硬件 --(0xAF Pong)--> 链路 --&gt; PC
    ///
    /// 设计要点：
    /// 1) 本类不持有任何 socket / 串口，只依赖两个注入点：
    ///    - sendFrame 委托：决定用哪条链路发（Serial / UDP 均可）；
    ///    - <see cref="FeedRawData"/>：把底层收到的原始字节喂进来（UDP=整包，串口=任意切片）。
    /// 2) 计时使用 Stopwatch 单调时钟（100ns 精度）。协议里的 T1 仍是 uint32 毫秒（硬件原样回传），
    ///    但本地用 T1-&gt;精确发送时刻 的字典把 RTT 还原到微秒级，
    ///    彻底避开 Environment.TickCount64 在 Windows 上约 15.6ms 的量化误差。
    /// 3) 结算公式（proc_us 由 STM32 填写）：
    ///        RTT    = T2_Recv - T1_Send
    ///        OneWay = (RTT - proc_us / 1000.0) / 2
    /// </summary>
    public class ProtocolLatencyTester : IDisposable
    {
        /// <summary>
        /// 0x1F Ping 的目标设备 ID（0x01 = 串口 STM32 主设备，其他设备收到后静默丢弃）。
        /// 由上层从 config.json 的 TargetSerialMaster 注入（静默设置项，缺省 0x01）：
        /// 本类刻意不引用 App.ConfigSvc，保持"传输/配置无关"。
        /// </summary>
        public byte TargetSerialMaster { get; set; } = PackageConfig.DefaultTargetDeviceId;

        /// <summary>上行 Pong 指令码</summary>
        public const byte PongCmd = 0xAF;

        /// <summary>Len 误码保护上限（与 BackControlService 同策略）</summary>
        private const int MaxPayloadLen = 1024;

        private readonly Action<byte[]> _sendFrame;

        /// <summary>单调高精度时钟</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>T1(uint ms) -> 本地精确发送时刻(double ms)</summary>
        private readonly ConcurrentDictionary<uint, double> _pendingSends = new ConcurrentDictionary<uint, double>();

        /// <summary>流式接收缓冲（串口切片/粘包通用）</summary>
        private readonly List<byte> _rxBuffer = new List<byte>();
        private readonly object _rxLock = new object();

        /// <summary>滑动窗口</summary>
        private readonly Queue<double> _window = new Queue<double>();
        private readonly object _windowLock = new object();

        private CancellationTokenSource? _cts;

        /// <summary>最近一次收到有效 Pong 的本地 tick（超时判定用，跨线程访问）</summary>
        private long _lastReplyTicks;

        private bool _warnedNoProc;
        private bool _warnedBadProc;

        /// <summary>本轮是否已经打过"仍在等待回包"的告警（每次成功回包后复位，可再次告警）</summary>
        private volatile bool _warnedWaiting;

        /// <summary>本轮开始时刻（ticks，跨线程访问）</summary>
        private long _startTicks;

        /// <summary>发包间隔(ms)，默认 100ms</summary>
        public int PingIntervalMs { get; set; } = 100;

        /// <summary>滑动窗口容量（样本数）</summary>
        public int WindowSize { get; private set; } = 30;

        /// <summary>连续无回复多久后自动结束(ms)。默认 10s，覆盖 ESP32 唤醒首包/慢链路/主循环忙等场景。</summary>
        public int ReplyTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// 连续无回复多久后先打一条"仍在等待"的告警(ms)，**不停止测试**。
        /// 与 ReplyTimeoutMs 组成两段式：先提醒"可能不对劲"，再在超时后收工。
        /// </summary>
        public int ReplyWarnMs { get; set; } = 3000;

        /// <summary>
        /// 单次测试时长上限(ms)：回包很慢但一直有、窗口迟迟填不满时的兜底。
        /// 0 = 自动取 max(30000, ReplyTimeoutMs × 3)。
        /// </summary>
        public int MaxDurationMs { get; set; } = 0;

        /// <summary>
        /// 可选的"链路是否仍然有效"探针（由上层注入，例如 () =&gt; transport.IsConnected）。
        /// 超时时用它区分"对端不响应"与"本机链路已断开"，给出可操作的提示。
        /// </summary>
        public Func<bool>? LinkAliveProbe { get; set; }

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        /// <summary>每个有效样本触发一次（在接收线程上）</summary>
        public event Action<LatencyStats>? StatsUpdated;

        /// <summary>窗口满 / 主动停止 / 超时结束时触发一次（携带最终统计）</summary>
        public event Action<LatencyStats>? Completed;

        /// <summary>文本日志（含告警），由上层决定怎么展示</summary>
        public event Action<string>? LogMessage;

        /// <param name="sendFrame">发送一帧的委托（由调用方决定走串口还是 UDP）</param>
        public ProtocolLatencyTester(Action<byte[]> sendFrame)
        {
            _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
        }

        /// <summary>开始测量（会先清空上一轮数据）</summary>
        public void Start(int windowSize = 30)
        {
            Stop(notify: false);

            WindowSize = Math.Clamp(windowSize, 3, 500);

            lock (_windowLock)
            {
                _window.Clear();
            }
            lock (_rxLock)
            {
                _rxBuffer.Clear();
            }
            _pendingSends.Clear();
            _warnedNoProc = false;
            _warnedBadProc = false;
            _warnedWaiting = false;
            Interlocked.Exchange(ref _lastReplyTicks, _clock.ElapsedTicks);
            Interlocked.Exchange(ref _startTicks, _clock.ElapsedTicks);

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => SendLoop(_cts.Token));
        }

        /// <summary>停止测量；notify=true 时向下游抛出最终统计</summary>
        public void Stop() => Stop(notify: true);

        private void Stop(bool notify)
        {
            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }

            if (notify)
            {
                Completed?.Invoke(Snapshot());
            }
        }

        public void Dispose() => Stop(notify: false);

        /// <summary>当前统计快照（无样本时 SampleCount = 0）</summary>
        public LatencyStats Snapshot()
        {
            lock (_windowLock)
            {
                return BuildStatsLocked(_window.Count > 0 ? _window.Last() : 0);
            }
        }

        // ==== 发包循环 ====

        private async Task SendLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 精确打点：协议里的 T1 用毫秒整数（硬件原样回传），本地记下精确发送时刻
                    double t1PreciseMs = _clock.Elapsed.TotalMilliseconds;
                    uint t1Ms = (uint)((long)t1PreciseMs & 0xFFFFFFFFL);
                    _pendingSends[t1Ms] = t1PreciseMs;

                    _sendFrame(PackageBuilder.BuildLatencyPing(TargetSerialMaster, t1Ms));

                    PrunePending(t1PreciseMs);

                    long nowTicks = _clock.ElapsedTicks;
                    long idleTicks = nowTicks - Interlocked.Read(ref _lastReplyTicks);

                    // ① 等待告警（不停止）：先提醒"可能不对劲"，给用户操作/排查的窗口
                    long warnTicks = (long)Math.Max(500, ReplyWarnMs) * Stopwatch.Frequency / 1000;
                    if (idleTicks > warnTicks && !_warnedWaiting)
                    {
                        _warnedWaiting = true;
                        bool aliveNow = LinkAliveProbe?.Invoke() ?? true;
                        LogMessage?.Invoke(aliveNow
                            ? $"[延迟测试] 已等待 {ReplyWarnMs}ms 仍无 0x{PongCmd:X2} 回包…继续等待到 {ReplyTimeoutMs}ms " +
                              "（对端未启动/不认 0x1F/端口或防火墙？也可再点一次按钮手动停止）"
                            : $"[延迟测试] 已等待 {ReplyWarnMs}ms 仍无回包，且链路已断开（本机客户端已被关闭）…" +
                              "建议重新点击『开始连接』");
                    }

                    // ② 停止超时：一直没有任何回包才收工
                    long timeoutTicks = (long)ReplyTimeoutMs * Stopwatch.Frequency / 1000;
                    if (idleTicks > timeoutTicks)
                    {
                        bool linkAlive = LinkAliveProbe?.Invoke() ?? true;
                        LogMessage?.Invoke(linkAlive
                            ? $"[延迟测试] {ReplyTimeoutMs}ms 未收到 0x{PongCmd:X2} 回包，已停止 " +
                              "（检查 Target_ID/端口/防火墙、对端程序是否在运行、ESP32 是否逐字节透传）"
                            : $"[延迟测试] 链路已断开（本机 UDP 客户端已被关闭），已停止 —— 请重新点击『开始连接』后再测");
                        Stop(notify: true);
                        return;
                    }

                    // ③ 单次时长上限：回包很慢但一直有时（窗口迟迟填不满）也不至于无限等
                    long capMs = MaxDurationMs > 0 ? MaxDurationMs : Math.Max(30000, ReplyTimeoutMs * 3);
                    long capTicks = capMs * Stopwatch.Frequency / 1000;
                    if (nowTicks - Interlocked.Read(ref _startTicks) > capTicks)
                    {
                        LogMessage?.Invoke($"[延迟测试] 已达单次测试时长上限 {capMs}ms，按已采集样本结算");
                        Stop(notify: true);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[延迟测试] 发包异常: {ex.Message}");
                }

                try
                {
                    await Task.Delay(Math.Max(10, PingIntervalMs), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // ==== 收包路径（串口切片 / UDP 整包 通用） ====

        /// <summary>
        /// 把底层收到的原始字节喂进来。UDP 一次一个整包；串口则是任意切片（可能半帧、可能多帧粘连），
        /// 内部按 0xAB 搜头 + Len 校验 + 全帧异或 自行分帧，因此两种链路行为一致。
        /// </summary>
        public void FeedRawData(byte[] chunk)
        {
            if (chunk == null || chunk.Length == 0 || !IsRunning)
                return;

            // 到达瞬间打点：作为 T2（分帧耗时为微秒级，可忽略）
            double t2Ms = _clock.Elapsed.TotalMilliseconds;

            List<byte[]>? frames = null;
            lock (_rxLock)
            {
                _rxBuffer.AddRange(chunk);

                while (_rxBuffer.Count >= 5)
                {
                    // 1. 搜头：非 0xAB 一律丢弃（PC 自己回声的 0xAA 下行帧也在这里被剔掉）
                    if (_rxBuffer[0] != PackageParser.McuToPc)
                    {
                        _rxBuffer.RemoveAt(0);
                        continue;
                    }

                    int payloadLen = (_rxBuffer[2] << 8) | _rxBuffer[3];
                    if (payloadLen > MaxPayloadLen)
                    {
                        // Len 误码成巨值：丢头重搜，避免解析永久卡死
                        _rxBuffer.RemoveAt(0);
                        continue;
                    }

                    int totalLen = 4 + payloadLen + 1; // Head+Cmd+LenH+LenL+Payload+Check
                    if (_rxBuffer.Count < totalLen)
                        break; // 半包，等下一批数据

                    byte[] oneFrame = _rxBuffer.GetRange(0, totalLen).ToArray();
                    _rxBuffer.RemoveRange(0, totalLen);
                    (frames ??= new List<byte[]>()).Add(oneFrame);
                }

                // 防御：缓冲长期堆不到合法帧时清空，避免内存无限增长
                if (_rxBuffer.Count > MaxPayloadLen * 2)
                    _rxBuffer.Clear();
            }

            if (frames == null)
                return;

            foreach (var frame in frames)
                HandlePongFrame(frame, t2Ms);
        }

        // ==== 结算与统计 ====

        private void HandlePongFrame(byte[] frame, double t2Ms)
        {
            if (!PackageParser.TryParsePong(frame, out byte devId, out uint t1Ms, out uint procUs))
                return; // 不是 0xAF（比如上行按键 0xA1）或校验失败，直接忽略

            // 精确发送时刻；查不到（残留/重启）则退化为毫秒整数
            if (!_pendingSends.TryRemove(t1Ms, out double t1PreciseMs))
                t1PreciseMs = t1Ms;

            // 收到有效回包：复位"仍在等待"告警，后续若再卡住可以再次提醒
            _warnedWaiting = false;
            Interlocked.Exchange(ref _lastReplyTicks, _clock.ElapsedTicks);

            double rttMs = Math.Max(0, t2Ms - t1PreciseMs);
            double procMs = procUs / 1000.0;

            if (procUs == 0 && !_warnedNoProc)
            {
                _warnedNoProc = true;
                LogMessage?.Invoke("[延迟告警] 收到 proc_us=0，STM32 未填写处理耗时，单向结果会包含主循环排队时间");
            }
            if (procMs > rttMs && !_warnedBadProc)
            {
                _warnedBadProc = true;
                LogMessage?.Invoke($"[延迟告警] proc_us({procMs:F3}ms) 大于 RTT({rttMs:F2}ms)，本样本按 0 处理");
            }

            // 单向延迟 = (RTT - STM32 内部处理耗时) / 2
            double oneWayMs = Math.Max(0, (rttMs - procMs) / 2.0);

            LatencyStats stats;
            lock (_windowLock)
            {
                _window.Enqueue(oneWayMs);
                while (_window.Count > WindowSize)
                    _window.Dequeue();
                stats = BuildStatsLocked(oneWayMs);
            }

            LogMessage?.Invoke($"[延迟] #{stats.SampleCount} RTT={rttMs:F2}ms proc={procMs:F3}ms 单向={oneWayMs:F2}ms (Dev=0x{devId:X2})");
            StatsUpdated?.Invoke(stats);

            // 窗口满 → 自动收工并抛出最终统计
            if (stats.SampleCount >= WindowSize)
                Stop(notify: true);
        }

        /// <summary>清理长期没有回音的挂单，防止长时间运行内存缓慢增长</summary>
        private void PrunePending(double nowMs)
        {
            double limit = Math.Max(ReplyTimeoutMs * 2.0, PingIntervalMs * 20.0);
            foreach (var kv in _pendingSends)
            {
                if (nowMs - kv.Value > limit)
                    _pendingSends.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>调用方必须持有 _windowLock</summary>
        private LatencyStats BuildStatsLocked(double lastMs)
        {
            if (_window.Count == 0)
                return new LatencyStats { SampleCount = 0 };

            double[] arr = _window.ToArray();
            double baseMs = arr.Min();
            double avgMs = arr.Average();
            double jitterMs = arr.Select(v => Math.Abs(v - baseMs)).Average();

            return new LatencyStats
            {
                SampleCount = arr.Length,
                BaseMs = baseMs,
                AvgMs = avgMs,
                JitterMs = jitterMs,
                LastMs = lastMs
            };
        }
    }
}
