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
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            }

            _currentSession = session;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                // 立即触发一次更新
                Session_MediaPropertiesChanged(_currentSession, null);
            }
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

            // 之前的计算逻辑搬到这里...
            var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
            var pos = (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ? timeline.Position + TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)))
                : timeline.Position;

            return new MediaProgressInfo(
                pos.TotalSeconds,
                timeline.EndTime.TotalSeconds,
                pos.ToString(@"mm\:ss"),
                timeline.EndTime.ToString(@"mm\:ss"),
                playback.PlaybackStatus.ToString()
            );
        }
    }
}