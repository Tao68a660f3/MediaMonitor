using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private DispatcherTimer _uiTimer; // 用于平滑更新进度条

        public MainWindow()
        {
            InitializeComponent();

            // 初始化定时器，每秒更新一次UI进度
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateTimelineUI();

            this.Loaded += async (s, e) => await InitSmtcAsync();
        }

        private async Task InitSmtcAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += async (s, e) => await UpdateAllAsync();
            await UpdateAllAsync();
        }

        private async Task UpdateAllAsync()
        {
            var session = _manager?.GetCurrentSession();
            if (session != null)
            {
                // 1. 订阅所有事件
                session.MediaPropertiesChanged -= OnMediaChanged;
                session.MediaPropertiesChanged += OnMediaChanged;

                session.TimelinePropertiesChanged -= OnTimelineChanged;
                session.TimelinePropertiesChanged += OnTimelineChanged;

                session.PlaybackInfoChanged -= OnPlaybackChanged;
                session.PlaybackInfoChanged += OnPlaybackChanged;

                // 2. 初始刷新
                await RefreshStaticInfo(session);
                UpdateTimelineUI();
                _uiTimer.Start();
            }
            else
            {
                _uiTimer.Stop();
                Dispatcher.Invoke(() => TxtTitle.Text = "无活动会话");
            }
        }

        // 静态信息：歌名、歌手、专辑
        private async Task RefreshStaticInfo(GlobalSystemMediaTransportControlsSession session)
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;
                TxtAlbum.Text = props.AlbumTitle;
                TxtStatus.Text = $"状态: {playback.PlaybackStatus} | 倍速: {playback.PlaybackRate}x";
            });
        }

        // 事件：媒体改变
        private async void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a)
            => await RefreshStaticInfo(s);

        // 事件：播放状态改变（播放/暂停/快进）
        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a)
        {
            // 状态一切换，立即执行一次进度更新，而不是等定时器触发
            Dispatcher.Invoke(() => UpdateTimelineUI());

            var info = s.GetPlaybackInfo();
            Dispatcher.Invoke(() => TxtStatus.Text = $"状态: {info.PlaybackStatus}");
        }

        // 事件：时间线改变（手动拉进度条、切歌）
        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a)
            => UpdateTimelineUI();

        // 核心：计算并展示时间线
        private void UpdateTimelineUI()
        {
            var session = _manager?.GetCurrentSession();
            if (session == null) return;

            var timeline = session.GetTimelineProperties();
            var playbackInfo = session.GetPlaybackInfo(); // 获取播放状态

            TimeSpan actualPosition;

            // 关键逻辑：判断是否正在播放
            if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                // 正在播放：计算偏移量（基准位置 + 经历的时间 * 倍速）
                var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                double rate = playbackInfo.PlaybackRate ?? 1.0;
                actualPosition = timeline.Position + TimeSpan.FromTicks((long)(timePassed.Ticks * rate));
            }
            else
            {
                // 已暂停或停止：直接使用 Windows 记录的最后位置，不再累加时间
                actualPosition = timeline.Position;
            }

            // 边界检查：不要超过总时长，也不要小于0
            if (actualPosition > timeline.EndTime) actualPosition = timeline.EndTime;
            if (actualPosition < TimeSpan.Zero) actualPosition = TimeSpan.Zero;

            Dispatcher.Invoke(() => {
                PrgBar.Maximum = timeline.EndTime.TotalSeconds;
                PrgBar.Value = actualPosition.TotalSeconds;
                TxtTime.Text = $"{FormatTime(actualPosition)} / {FormatTime(timeline.EndTime)}";

                // 【串口预留】如果你要发给硬件，可以在这里判断：
                // if(playbackInfo.PlaybackStatus == ...Playing) SendToSerial(actualPosition);
            });
        }

        private string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }
}