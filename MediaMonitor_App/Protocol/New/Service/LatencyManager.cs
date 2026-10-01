using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>一次延迟测量的统计快照（口径与 Legacy 的 <c>LatencyStats</c> 一致，便于 UI 复用展示）</summary>
    public sealed record NpLatencyStats(int SampleCount, double BaseMs, double AvgMs, double JitterMs, double LastMs)
    {
        public override string ToString()
            => $"Base={BaseMs:F2}ms Avg={AvgMs:F2}ms Jitter={JitterMs:F2}ms 样本={SampleCount}";
    }

    /// <summary>
    /// 延迟测量（规范 §14 / §15）。
    ///
    /// * 整轮测量用**同一个 `REQUEST_ID`** 关联（不再占用 `SESSION_ID`，见 R-01）；
    /// * `LATENCY_END` 结束本轮（窗口满或超时都由发起方发）；
    /// * **ESP32 优先**：PC 正在测量时若收到对端的 `LATENCY_REQUEST`，立即放弃本轮并转为应答方；
    /// * 计时用 `Stopwatch` + 「T1 → 精确发送时刻」映射，避免 `TickCount64` 的 ~15.6ms 量化误差
    ///   （与 Legacy `ProtocolLatencyTester` 同一套做法）；
    /// * `单向延迟 = (RTT − PROC_US/1000) / 2`。
    /// </summary>
    public sealed class LatencyManager : IDisposable
    {
        private readonly SessionManager _session;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<uint, double> _pendingSendMs = new Dictionary<uint, double>();
        private readonly Queue<double> _window = new Queue<double>();
        private readonly object _gate = new object();

        private Timer? _timer;
        private double _lastSentMs = -1e9;
        private double _lastRecvMs = -1e9;

        public int WindowSize { get; set; } = 30;
        public int PingIntervalMs { get; set; } = 100;
        public int TimeoutMs { get; set; } = 3000;

        public bool IsRunning { get; private set; }
        public uint CurrentRequestId { get; private set; }
        public NpLatencyStats? LastStats { get; private set; }

        public long StatRequestsSent, StatResponsesRecv, StatResponded, StatPreempted, StatTimeouts;
        public byte LastErrorCode;

        public event Action<NpLatencyStats>? StatsUpdated;

        public LatencyManager(SessionManager session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>开始一轮测量（分配新的 REQUEST_ID，立即发第一个 REQUEST）</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (IsRunning)
                {
                    return;
                }

                CurrentRequestId = _session.AllocRid();
                _pendingSendMs.Clear();
                _window.Clear();
                IsRunning = true;
                _lastSentMs = -1e9;
                _lastRecvMs = _clock.Elapsed.TotalMilliseconds;
            }

            SendRequest();
            _timer ??= new Timer(_ => Tick(), null, 0, 10);
        }

        public void Stop()
        {
            lock (_gate)
            {
                IsRunning = false;
            }
        }

        private void SendRequest()
        {
            if (!_session.IsActive)
            {
                return;
            }

            uint t1 = (uint)_clock.ElapsedMilliseconds;

            lock (_gate)
            {
                _pendingSendMs[t1] = _clock.Elapsed.TotalMilliseconds;   // 精确发送时刻（微秒级）
                _lastSentMs = _clock.Elapsed.TotalMilliseconds;
            }

            _session.SendLatencyRequest(CurrentRequestId, t1);
            StatRequestsSent++;
        }

        private void Tick()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            bool running;
            double sinceSent;
            double sinceRecv;

            lock (_gate)
            {
                running = IsRunning;
                sinceSent = now - _lastSentMs;
                sinceRecv = now - _lastRecvMs;
            }

            if (!running)
            {
                return;
            }

            if (sinceSent >= PingIntervalMs)
            {
                SendRequest();
            }

            if (sinceRecv >= TimeoutMs)
            {
                StatTimeouts++;
                Finish("超时：连续无回包", sendEnd: true);
            }
        }

        /* ------------------------------------------------------------------ */
        /* 收帧：与其它 Service 一样，在 SessionManager.OnFrame 之后调用即可   */
        /* （会话层对 LATENCY_* 返回 false，帧会转到这里）                      */
        /* ------------------------------------------------------------------ */

        public void OnFrame(in NpFrame f)
        {
            if (f.Type != NpType.System)
            {
                return;
            }

            switch (f.Code)
            {
                case NpSysCode.LatencyRequest:  HandleRequest(f); break;
                case NpSysCode.LatencyResponse: HandleResponse(f); break;
                case NpSysCode.LatencyEnd:      HandleEnd(f); break;
                case NpSysCode.Error:
                    if (f.PayloadLen >= 1)
                    {
                        LastErrorCode = f.Payload.Span[0];
                    }
                    break;
                default: break;
            }
        }

        public string LastPreemptReason { get; private set; } = "";
        public string LastEndReason { get; private set; } = "";

        /// <summary>作为应答方：测本端处理耗时并原样回传 T1（规范 §14.2）</summary>
        private void HandleRequest(in NpFrame f)
        {
            if (f.PayloadLen < 4u)
            {
                return;
            }

            double recvMs = _clock.Elapsed.TotalMilliseconds;
            uint t1 = NpReader.ReadU32(f.Payload.Span);
            StatResponded++;

            // ESP32 优先（规范 §15）：本端正在测量时被抢占，放弃本轮、转为应答方
            bool preempted;
            lock (_gate)
            {
                preempted = IsRunning;
                IsRunning = false;
            }
            if (preempted)
            {
                StatPreempted++;
                LastPreemptReason = "对端发起测量，本端让出（ESP32 优先）";
            }

            uint procUs = (uint)Math.Max(0.0, (_clock.Elapsed.TotalMilliseconds - recvMs) * 1000.0);
            _session.SendLatencyResponse(f.RequestId, t1, procUs);
        }

        private void HandleResponse(in NpFrame f)
        {
            if (f.PayloadLen < 8u)
            {
                return;
            }

            double t2Ms = _clock.Elapsed.TotalMilliseconds;
            uint t1 = NpReader.ReadU32(f.Payload.Span);
            uint procUs = NpReader.ReadU32(f.Payload.Span[4..]);

            lock (_gate)
            {
                if (!IsRunning || (f.RequestId != CurrentRequestId))
                {
                    return;
                }

                _lastRecvMs = t2Ms;

                double t1Precise = _pendingSendMs.TryGetValue(t1, out double v) ? v : t1;
                double rtt = Math.Max(0.0, t2Ms - t1Precise);
                double oneWay = Math.Max(0.0, (rtt - (procUs / 1000.0)) / 2.0);

                _window.Enqueue(oneWay);
                while (_window.Count > WindowSize)
                {
                    _window.Dequeue();
                }

                StatResponsesRecv++;
            }

            NpLatencyStats stats = BuildStats();
            LastStats = stats;
            StatsUpdated?.Invoke(stats);

            if (stats.SampleCount >= WindowSize)
            {
                Finish("窗口已满", sendEnd: true);
            }
        }

        private void HandleEnd(in NpFrame f)
        {
            lock (_gate)
            {
                if (!IsRunning || (f.RequestId != CurrentRequestId))
                {
                    return;
                }
            }
            Finish("对端结束本轮", sendEnd: false);
        }

        private void Finish(string reason, bool sendEnd)
        {
            NpLatencyStats? stats;
            uint rid;

            lock (_gate)
            {
                if (!IsRunning)
                {
                    return;                     // 幂等：超时与"窗口已满"可能同时到达
                }
                IsRunning = false;
                rid = CurrentRequestId;
                stats = LastStats;
            }

            if (sendEnd)
            {
                _session.SendLatencyEnd(rid, (ushort)(stats?.SampleCount ?? 0));
            }
            LastEndReason = reason;
        }

        private NpLatencyStats BuildStats()
        {
            lock (_gate)
            {
                if (_window.Count == 0)
                {
                    return new NpLatencyStats(0, 0, 0, 0, 0);
                }

                double[] arr = _window.ToArray();
                double baseMs = double.MaxValue;
                double sum = 0;

                foreach (double v in arr)
                {
                    if (v < baseMs)
                    {
                        baseMs = v;
                    }
                    sum += v;
                }

                double avg = sum / arr.Length;
                double jitter = 0;
                foreach (double v in arr)
                {
                    jitter += Math.Abs(v - baseMs);
                }
                jitter /= arr.Length;

                return new NpLatencyStats(arr.Length, baseMs, avg, jitter, arr[arr.Length - 1]);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            _clock.Stop();
        }
    }
}
