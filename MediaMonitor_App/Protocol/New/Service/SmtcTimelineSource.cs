using System;
using MediaMonitor.Services;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// 把既有 <see cref="SmtcService"/> 适配成 <see cref="ITimelineSource"/>。
    ///
    /// 只在 PC 应用（WPF）里编译：自测工程链接的是纯逻辑文件，不包含本文件，
    /// 因此 <see cref="TimelinePump"/> 可以用"假数据源"单独验证。
    /// </summary>
    public sealed class SmtcTimelineSource : ITimelineSource
    {
        private readonly SmtcService _smtc;

        public SmtcTimelineSource(SmtcService smtc)
        {
            _smtc = smtc ?? throw new ArgumentNullException(nameof(smtc));
        }

        public TimelineSnapshot? GetSnapshot()
        {
            MediaProgressInfo? p = _smtc.GetCurrentProgress();
            if (p == null)
            {
                return null;      // 无会话 / 播放器已退出：不发时间轴
            }

            return new TimelineSnapshot(p.Status == PlaybackState.Playing, p.Position, p.Duration);
        }
    }
}
