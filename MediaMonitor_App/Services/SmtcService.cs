using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 播放状态（与系统 SMTC 状态对应的强类型枚举）
    /// </summary>
    public enum PlaybackState
    {
        Closed,
        Opened,
        Changing,
        Stopped,
        Playing,
        Paused
    }

    public record MediaProgressInfo(TimeSpan Position, TimeSpan Duration, PlaybackState Status);

    public class SmtcService
    {
        public string? CurrentTitle { get; private set; }
        public string? CurrentArtist { get; private set; }
        public string? CurrentAlbum { get; private set; }

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private GlobalSystemMediaTransportControlsSessionTimelineProperties? _lastTimeline;

        // --- 暂停→恢复播放的锚点 ---
        // Chrome 等浏览器在恢复播放瞬间只触发 PlaybackInfoChanged（状态→Playing），
        // 但 Timeline/LastUpdatedTime 仍停留在暂停时刻。若不设锚点，
        // GetCurrentProgress 会按 LastUpdatedTime 外推，把整个暂停时长虚增进首个同步包。
        // 因此用"进入 Playing 的事件瞬间"作为新外推基准，直到系统 Timeline 刷新到锚点之后。
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus _lastStatus =
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        private TimeSpan _resumeBasePosition;
        private DateTimeOffset _resumeBaseTime;
        private bool _resumeAnchorValid = false;

        // 媒体属性更新序号：切歌/切会话瞬间系统会连发多个 MediaPropertiesChanged，
        // 且 async void 中 await 的完成顺序不保证与触发顺序一致。
        // 只有"最后一次触发的事件"序号最新才允许生效，旧事件的延迟完成直接丢弃。
        private long _mediaUpdateSeq = 0;

        public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus>? PlaybackChanged;
        public Action<GlobalSystemMediaTransportControlsSessionMediaProperties>? OnMediaUpdated;
        public event Action? SessionsListChanged;

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => {
                SessionsListChanged?.Invoke();
            };
        }

        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
            => _manager?.GetSessions() ?? new List<GlobalSystemMediaTransportControlsSession>();

        public void SelectSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            // 切换会话：使旧会话的所有在途事件全部失效
            Interlocked.Increment(ref _mediaUpdateSeq);

            // 跨会话重置状态，避免用上一个会话的播放状态/锚点误判
            _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            _resumeAnchorValid = false;

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;

                try
                {
                    _lastTimeline = _currentSession.GetTimelineProperties();
                }
                catch { _lastTimeline = null; }

                // 立即触发一次更新
                Session_MediaPropertiesChanged(_currentSession, null);
            }
        }

        // ========== SMTC 直控方法（MediaKeyInvoker 主用路径，不依赖前台窗口/权限） ==========

        /// <summary>
        /// 播放/暂停：直接调用系统 SMTC 会话，绕过 keybd_event 注入的环境限制
        /// </summary>
        public async Task PlayPauseAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TryTogglePlayPauseAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 播放/暂停失败: {ex.Message}"); }
        }

        /// <summary>
        /// 下一曲：直接调用系统 SMTC 会话
        /// </summary>
        public async Task NextAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipNextAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 下一曲失败: {ex.Message}"); }
        }

        /// <summary>
        /// 上一曲：直接调用系统 SMTC 会话
        /// </summary>
        public async Task PrevAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipPreviousAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 上一曲失败: {ex.Message}"); }
        }

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                _lastTimeline = sender.GetTimelineProperties();
            }
            catch { /* SMTC 时间线获取失败时忽略 */ }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var status = sender.GetPlaybackInfo().PlaybackStatus;

                // 检测"非播放 → 播放"转换：暂停/停止后恢复（含切歌后首播）。
                // 用事件触发瞬间的 Position + 墙钟建立恢复锚点，
                // 避免用陈旧的 LastUpdatedTime 外推导致首个进度包虚增整个暂停时长。
                if (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    _resumeBasePosition = sender.GetTimelineProperties().Position;
                    _resumeBaseTime = DateTimeOffset.Now;
                    _resumeAnchorValid = true;
                }
                _lastStatus = status;

                PlaybackChanged?.Invoke(status);
            }
            catch { /* SMTC 播放信息获取失败时忽略 */ }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs? args)
        {
            try
            {
                // 核心修复：防止在切歌或关闭时因 Session 失效导致的 COM 崩溃
                long seq = Interlocked.Increment(ref _mediaUpdateSeq); // 本次事件序号

                var props = await sender.TryGetMediaPropertiesAsync();

                // 只有"最后一次触发的事件"才允许生效：
                // 切歌瞬间系统可能连发多个事件，且 await 完成顺序不保证与触发一致，
                // 旧事件的延迟完成会覆盖新歌词，必须用序号丢弃。
                if (props != null && sender == _currentSession
                    && seq == Volatile.Read(ref _mediaUpdateSeq))
                {
                    // 过滤切歌过渡期的空属性事件（Title 为空），
                    // 防止触发 LoadAndParse 清空已加载歌词、以及发出空的元数据包
                    if (string.IsNullOrEmpty(props.Title))
                        return;

                    CurrentTitle = props.Title; // 赋值
                    CurrentArtist = props.Artist; // 赋值
                    CurrentAlbum = props.AlbumTitle;
                    OnMediaUpdated?.Invoke(props);
                }
            }
            catch (Exception ex)
            {
                // 捕获 COMException (0x80030070) 等，保持程序不崩溃
                System.Diagnostics.Debug.WriteLine($"SMTC 属性获取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将系统 SMTC 播放状态映射为强类型枚举
        /// </summary>
        private static PlaybackState MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            switch (status)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed:
                    return PlaybackState.Closed;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened:
                    return PlaybackState.Opened;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                    return PlaybackState.Changing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    return PlaybackState.Stopped;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    return PlaybackState.Playing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return PlaybackState.Paused;
                default:
                    return PlaybackState.Closed;
            }
        }

        public MediaProgressInfo? GetCurrentProgress()
        {
            if (_currentSession == null) return null;

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                var playback = _currentSession.GetPlaybackInfo();
                var status = playback.PlaybackStatus;

                TimeSpan pos = timeline.Position;

                // Chrome 等浏览器不会像常规播放器那样频繁刷新 SMTC Position/LastUpdatedTime，
                // 旧的 10 秒插值窗口会让进度在播放约 10 秒后停止前进（用户观察到的"卡住"）。
                // 去掉时间窗上限，按 LastUpdatedTime + 播放速率持续外推；
                // 下方 EndTime/0 钳位兜底，确保外推永远不会越过曲目范围。
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    if (_resumeAnchorValid)
                    {
                        // 刚恢复播放：Timeline/LastUpdatedTime 仍停留在暂停时刻，
                        // 必须从"恢复锚点"（进入 Playing 事件瞬间的 Position + 墙钟）外推，
                        // 否则会把整个暂停时长虚增进第一个进度包。
                        var resumePassed = DateTimeOffset.Now - _resumeBaseTime;
                        if (resumePassed.TotalSeconds >= 0)
                        {
                            pos = _resumeBasePosition + TimeSpan.FromTicks(
                                (long)(resumePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                        }

                        // 系统 Timeline 已刷新到恢复时刻之后 → 恢复正常 LastUpdatedTime 外推
                        if (timeline.LastUpdatedTime >= _resumeBaseTime)
                        {
                            _resumeAnchorValid = false;
                            pos = timeline.Position;
                        }
                    }
                    else
                    {
                        var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                        if (timePassed.TotalSeconds >= 0)
                        {
                            pos += TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                        }
                    }
                }

                if (pos > timeline.EndTime) pos = timeline.EndTime;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

                return new MediaProgressInfo(
                    pos,
                    timeline.EndTime,
                    MapPlaybackStatus(status)
                );
            }
            catch { return null; }
        }
    }
}