using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace MediaMonitor
{
    // 定义一个简单的结构体，方便把进度数据传给 UI
    public record MediaProgressInfo(double CurrentSeconds, double TotalSeconds, string CurrentStr, string TotalStr, string Status);

    public class SmtcService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private GlobalSystemMediaTransportControlsSessionTimelineProperties? _lastTimeline;
        private DateTimeOffset _lastHandshakeTime = DateTimeOffset.MinValue;
        private bool _isSystemValidated = false;

        public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus>? PlaybackChanged;
        public Action<GlobalSystemMediaTransportControlsSessionMediaProperties>? OnMediaUpdated;
        public event Action? SessionsListChanged;

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => SessionsListChanged?.Invoke();
        }

        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
            => _manager?.GetSessions() ?? new List<GlobalSystemMediaTransportControlsSession>();

        public void SelectSession(GlobalSystemMediaTransportControlsSession? session)
        {
            // 1. 彻底清理旧 Session 的所有订阅，防止内存泄漏和逻辑重叠
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                // 2. 状态重置：切换 Session 时，先封锁补偿逻辑，等待新 Session 的第一次事件握手
                _isSystemValidated = false;

                // 3. 统一订阅事件
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;

                // 4. 初始化快照（此时 _isSystemValidated 仍为 false，直到下一次事件触发）
                _lastTimeline = _currentSession.GetTimelineProperties();

                // 5. 立即同步一次元数据
                Session_MediaPropertiesChanged(_currentSession, null);
            }
        }

        // --- 分离出来的事件处理函数 ---

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            // 核心逻辑：系统主动推了事件，说明现在的 Position 和 LastUpdatedTime 是匹配的
            // 这解决了你说的“垃圾值”问题：只有系统更新了，我们才认为它是新鲜的
            _lastTimeline = sender.GetTimelineProperties();
            _isSystemValidated = true;
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            var status = sender.GetPlaybackInfo().PlaybackStatus;

            // 当状态变为非播放（暂停/停止）时，立即废弃验证标签
            // 这样下次点击播放时，必须等到下一次 Timeline 事件才能解锁补偿逻辑
            if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                _isSystemValidated = false;
            }

            PlaybackChanged?.Invoke(status);
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs? args)
        {
            var props = await sender.TryGetMediaPropertiesAsync();
            OnMediaUpdated?.Invoke(props);
        }

        public MediaProgressInfo? GetCurrentProgress()
        {
            if (_currentSession == null) return null;

            var timeline = _currentSession.GetTimelineProperties();
            var playback = _currentSession.GetPlaybackInfo();
            var status = playback.PlaybackStatus;

            TimeSpan pos = timeline.Position;

            // 只有在【正在播放】且【已经过系统事件握手】的情况下才预测
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && _isSystemValidated)
            {
                var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;

                // 这里的阈值可以放宽到 5 秒甚至更多，因为 _isSystemValidated 
                // 已经帮我们过滤掉了暂停期间积累的垃圾时间
                if (timePassed.TotalSeconds >= 0 && timePassed.TotalSeconds < 10)
                {
                    pos += TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                }
            }
            else
            {
                // 只要不是 Playing，或者系统还没通过事件确认新位置，就禁止预测
                _isSystemValidated = false;
            }

            // 兜底保护
            if (pos > timeline.EndTime) pos = timeline.EndTime;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            return new MediaProgressInfo(
                pos.TotalSeconds,
                timeline.EndTime.TotalSeconds,
                pos.ToString(@"mm\:ss"),
                timeline.EndTime.ToString(@"mm\:ss"),
                status.ToString()
            );
        }
    }
}