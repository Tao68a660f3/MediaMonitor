using System;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// 时间轴同步偏移包装：把上报位置整体**前移** <c>offsetMs</c>（正值 = 提前发出），
    /// 用来补偿"PC 发出 → ESP32 收到"的单向延迟，对应配置项 <c>SyncCurrentOffsetMs</c>。
    ///
    /// <para>算法本身不碰：只改 <see cref="TimelineSnapshot.Position"/>；跳变判定
    /// （<see cref="TimelinePump.SeekThresholdMs"/>）在 <see cref="TimelinePump"/> 里按偏移后的位置比较，
    /// 与 Legacy 的"同步偏移"语义保持一致。</para>
    ///
    /// <para>纯逻辑、不依赖 WPF，因此自测工程直接链接本文件（见 Tests/NewProtocolSelfTest）。</para>
    /// </summary>
    public sealed class OffsetTimelineSource : ITimelineSource
    {
        private readonly ITimelineSource _inner;
        private readonly int _offsetMs;

        public OffsetTimelineSource(ITimelineSource inner, int offsetMs)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _offsetMs = offsetMs;
        }

        /// <summary>当前偏移量（ms）</summary>
        public int OffsetMs => _offsetMs;

        public TimelineSnapshot? GetSnapshot()
        {
            TimelineSnapshot? s = _inner.GetSnapshot();
            if ((s == null) || (_offsetMs == 0))
            {
                return s;
            }

            double posMs = s.Value.Position.TotalMilliseconds + _offsetMs;
            if (posMs < 0)
            {
                posMs = 0;                                   // 开头附近不做负偏移
            }

            return s.Value with { Position = TimeSpan.FromMilliseconds(posMs) };
        }
    }
}
