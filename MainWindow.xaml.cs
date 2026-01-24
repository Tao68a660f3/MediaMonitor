using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _selectedSession;
        private DispatcherTimer _uiTimer;

        public MainWindow()
        {
            InitializeComponent();

            // 初始化定时器：用于平滑更新进度条
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => UpdateTimelineUI();

            this.Loaded += async (s, e) => await InitSmtcAsync();
        }

        private async Task InitSmtcAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                // 当系统中媒体会话列表变化（App打开/关闭）时刷新下拉框
                _manager.SessionsChanged += async (s, e) => await RefreshSessionList();

                await RefreshSessionList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化 SMTC 失败: {ex.Message}");
            }
        }

        private async Task RefreshSessionList()
        {
            if (_manager == null) return;

            var sessions = _manager.GetSessions();

            await Dispatcher.InvokeAsync(() =>
            {
                // 记录当前选中的 ID，以便刷新后尝试恢复选择
                var currentId = (_ComboSelectedSession)?.SourceAppUserModelId;

                ComboSessions.ItemsSource = sessions;

                if (sessions.Count > 0)
                {
                    // 尝试匹配之前选中的，或者默认选第一个
                    var target = sessions.FirstOrDefault(s => s.SourceAppUserModelId == currentId) ?? sessions[0];
                    ComboSessions.SelectedItem = target;
                }
            });
        }

        private GlobalSystemMediaTransportControlsSession? _ComboSelectedSession => ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession;

        private async void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 1. 断开旧的事件订阅
            if (_selectedSession != null)
            {
                _selectedSession.MediaPropertiesChanged -= OnMediaChanged;
                _selectedSession.TimelinePropertiesChanged -= OnTimelineChanged;
                _selectedSession.PlaybackInfoChanged -= OnPlaybackChanged;
            }

            // 2. 锁定新选中的 Session
            _selectedSession = _ComboSelectedSession;

            if (_selectedSession != null)
            {
                // 3. 订阅新事件
                _selectedSession.MediaPropertiesChanged += OnMediaChanged;
                _selectedSession.TimelinePropertiesChanged += OnTimelineChanged;
                _selectedSession.PlaybackInfoChanged += OnPlaybackChanged;

                // 4. 立即刷新一次 UI
                await RefreshStaticInfo(_selectedSession);
                UpdateTimelineUI();
                _uiTimer.Start();
            }
            else
            {
                _uiTimer.Stop();
                ClearUI();
            }
        }

        // 事件响应：歌曲属性改变（切歌）
        private async void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => await RefreshStaticInfo(sender);

        // 事件响应：播放状态改变（播放/暂停）
        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => Dispatcher.Invoke(UpdateTimelineUI);

        // 事件响应：时间线跳变（用户拉进度条）
        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => Dispatcher.Invoke(UpdateTimelineUI);

        private async Task RefreshStaticInfo(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                Dispatcher.Invoke(() =>
                {
                    TxtTitle.Text = props.Title ?? "未知曲目";
                    TxtArtist.Text = props.Artist ?? "未知艺术家";
                    TxtAlbum.Text = props.AlbumTitle ?? "未知专辑";
                });
            }
            catch { /* 忽略读取冲突 */ }
        }

        private void UpdateTimelineUI()
        {
            if (_selectedSession == null) return;

            try
            {
                var timeline = _selectedSession.GetTimelineProperties();
                var playback = _selectedSession.GetPlaybackInfo();

                TimeSpan actualPos;
                if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    // 加上自上次更新以来的时间差
                    var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    actualPos = timeline.Position + TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                }
                else
                {
                    actualPos = timeline.Position;
                }

                // 修正溢出
                if (actualPos > timeline.EndTime) actualPos = timeline.EndTime;
                if (actualPos < TimeSpan.Zero) actualPos = TimeSpan.Zero;

                Dispatcher.Invoke(() =>
                {
                    TxtStatus.Text = $"状态: {playback.PlaybackStatus}";
                    PrgBar.Maximum = timeline.EndTime.TotalSeconds;
                    PrgBar.Value = actualPos.TotalSeconds;
                    TxtTime.Text = $"{FormatTime(actualPos)} / {FormatTime(timeline.EndTime)}";
                });
            }
            catch { }
        }

        private void ClearUI()
        {
            Dispatcher.Invoke(() => {
                TxtTitle.Text = "暂无播放内容";
                TxtArtist.Text = "-";
                TxtStatus.Text = "状态: Stopped";
                PrgBar.Value = 0;
            });
        }

        private string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }
}