using System;
using System.Diagnostics;
using System.Threading;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>一帧时间轴所需的全部信息（OBS 无关，便于替换数据源与单测）</summary>
    public readonly record struct TimelineSnapshot(bool IsPlaying, TimeSpan Position, TimeSpan Duration);

    /// <summary>时间轴数据源（PC 应用里由 <c>SmtcTimelineSource</c> 适配 SmtcService）</summary>
    public interface ITimelineSource
    {
        TimelineSnapshot? GetSnapshot();
    }

    /// <summary>
    /// TIMELINE/STATE 推送（规范 §7，新协议自有编码：`STATE u8 + 3×u32 大端`）。
    ///
    /// 发送策略（对齐 Legacy 已验证的行为）：
    ///   * 常规：每 <c>intervalMs</c>（默认 500ms，= SyncIntervalMs）发一帧；
    ///   * 立即补发：**播放↔暂停切换**、**进度跳变 &gt; 1500ms（Seek / 切歌）**、**会话刚建立**。
    ///
    /// ⚠ 会话刚建立时的正确顺序是 **先 <see cref="PublishNow"/> 再 <see cref="Start"/>**，
    /// 否则 Start 的首次触发会与 PublishNow 撞在一起，连发两帧内容相同的包。
    /// </summary>
    public sealed class TimelinePump : IDisposable
    {
        /// <summary>进度跳变判定阈值（与 Legacy PackageMaster 保持一致）</summary>
        public const double SeekThresholdMs = 1500.0;

        private readonly SessionManager _session;
        private readonly ITimelineSource _source;
        private readonly int _intervalMs;
        private readonly Func<uint> _hostTick;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();

        private Timer? _timer;
        private long _lastSentMs = -1_000_000;
        private double _lastPositionMs = -1;
        private bool _lastPlaying;
        private bool _hasBaseline;      // 是否已有"上一帧"基准（避免与 PublishNow 重复发）

        public long StatTimelineTx;
        public long StatImmediateTx;

        public TimelinePump(SessionManager session, ITimelineSource source,
                            int intervalMs = 500, Func<uint>? hostTick = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _intervalMs = Math.Clamp(intervalMs, 20, 30000);
            _hostTick = hostTick ?? (() => (uint)Environment.TickCount64);
        }

        public void Start()
        {
            lock (_gate)
            {
                _timer ??= new Timer(_ => Tick(), null, 0, 10);   // 10ms 轮询，到点才真发
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>立即补发一帧（切歌 / Seek / 暂停↔播放 / 会话刚建立时调用）</summary>
        public void PublishNow()
        {
            TimelineSnapshot? snap = _source.GetSnapshot();
            if (snap == null)
            {
                return;
            }
            Send(snap.Value, immediate: true);
        }

        private void Tick()
        {
            if (!_session.IsActive)
            {
                return;
            }

            TimelineSnapshot? snap = _source.GetSnapshot();
            if (snap == null)
            {
                return;                        // 无媒体：不发（元数据的空包已表达"无媒体"）
            }

            TimelineSnapshot s = snap.Value;

            lock (_gate)
            {
                // 基准一律是"上一次真正发出去的帧"（由 Send 记录）：
                // 因此 PublishNow() 之后紧接的第一次 Tick 不会重复发同一内容。
                bool playingChanged = _hasBaseline && (s.IsPlaying != _lastPlaying);
                bool seeked = _hasBaseline &&
                              Math.Abs(s.Position.TotalMilliseconds - _lastPositionMs) > SeekThresholdMs;
                bool due = !_hasBaseline || (_clock.ElapsedMilliseconds - _lastSentMs >= _intervalMs);

                if (!playingChanged && !seeked && !due)
                {
                    return;
                }
            }

            Send(s, immediate: false);
        }

        private void Send(TimelineSnapshot s, bool immediate)
        {
            if (!_session.IsActive)
            {
                return;
            }

            _session.SendRealtime(NpType.Timeline, NpTimelineCode.State, BuildPayload(s, _hostTick()));

            lock (_gate)
            {
                // 基准 = 刚发出去的这一帧
                _hasBaseline = true;
                _lastPlaying = s.IsPlaying;
                _lastPositionMs = s.Position.TotalMilliseconds;
                _lastSentMs = _clock.ElapsedMilliseconds;
            }

            StatTimelineTx++;
            if (immediate)
            {
                StatImmediateTx++;
            }
        }

        /// <summary>
        /// 构造 payload（13 B，**全部大端**）：`STATE u8 | CURRENT_MS u32 | TOTAL_MS u32 | HOST_TICK_MS u32`
        /// </summary>
        public static byte[] BuildPayload(in TimelineSnapshot s, uint hostTickMs)
        {
            byte[] p = new byte[13];
            p[0] = s.IsPlaying ? (byte)1 : (byte)0;
            NpWriter.WriteU32(p.AsSpan(1), (uint)Math.Max(0, (long)s.Position.TotalMilliseconds));
            NpWriter.WriteU32(p.AsSpan(5), (uint)Math.Max(0, (long)s.Duration.TotalMilliseconds));
            NpWriter.WriteU32(p.AsSpan(9), hostTickMs);
            return p;
        }

        public void Dispose()
        {
            Stop();
            _clock.Stop();
        }
    }
}
