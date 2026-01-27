using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace MediaMonitor
{
    public record MediaProgressInfo(double CurrentSeconds, double TotalSeconds, string CurrentStr, string TotalStr, string Status);

    public class SmtcService
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private GlobalSystemMediaTransportControlsSessionTimelineProperties? _lastTimeline;
        private bool _isSystemValidated = false;

        public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus>? PlaybackChanged;
        public Action<GlobalSystemMediaTransportControlsSessionMediaProperties>? OnMediaUpdated;
        public event Action? SessionsListChanged;

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => {
                _isSystemValidated = false; // 会话列表变动时重置状态
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

            _currentSession = session;

            if (_currentSession != null)
            {
                _isSystemValidated = false;

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

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                _lastTimeline = sender.GetTimelineProperties();
                _isSystemValidated = true;
            }
            catch { _isSystemValidated = false; }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var status = sender.GetPlaybackInfo().PlaybackStatus;
                if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    _isSystemValidated = false;
                }
                PlaybackChanged?.Invoke(status);
            }
            catch { _isSystemValidated = false; }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs? args)
        {
            try
            {
                // 核心修复：防止在切歌或关闭时因 Session 失效导致的 COM 崩溃
                var props = await sender.TryGetMediaPropertiesAsync();
                if (props != null && sender == _currentSession)
                {
                    OnMediaUpdated?.Invoke(props);
                }
            }
            catch (Exception ex)
            {
                // 捕获 COMException (0x80030070) 等，保持程序不崩溃
                System.Diagnostics.Debug.WriteLine($"SMTC 属性获取失败: {ex.Message}");
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

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && _isSystemValidated)
                {
                    var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    if (timePassed.TotalSeconds >= 0 && timePassed.TotalSeconds < 10)
                    {
                        pos += TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                    }
                }
                else
                {
                    _isSystemValidated = false;
                }

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
            catch { return null; }
        }
    }
}