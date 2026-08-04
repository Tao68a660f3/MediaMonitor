using System;
using System.Collections.Generic;
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
                    pos,
                    timeline.EndTime,
                    MapPlaybackStatus(status)
                );
            }
            catch { return null; }
        }
    }
}