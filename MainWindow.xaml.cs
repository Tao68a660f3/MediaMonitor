using System;
using System.IO;
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
        private readonly SmtcService _smtc = new SmtcService();
        private readonly LyricService _lyric = new LyricService();
        private DispatcherTimer _uiTimer;

        public MainWindow()
        {
            InitializeComponent();

            // 1. 初始化定时器 (50ms 频率以支持平滑进度和逐字歌词)
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateUIProgress();

            // 2. 窗口加载后启动 SMTC
            this.Loaded += async (s, e) => {
                await _smtc.InitializeAsync();

                // 监听 Session 列表变化
                _smtc.SessionsListChanged += async () => {
                    var sessions = _smtc.GetSessions();
                    await Dispatcher.InvokeAsync(() => {
                        ComboSessions.ItemsSource = sessions;
                        if (sessions.Count > 0 && ComboSessions.SelectedIndex == -1)
                            ComboSessions.SelectedIndex = 0;
                    });
                };

                // 初始加载一次列表
                var initialSessions = _smtc.GetSessions();
                ComboSessions.ItemsSource = initialSessions;
                if (initialSessions.Count > 0) ComboSessions.SelectedIndex = 0;
            };
        }

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession;
            if (selected == null) return;

            _smtc.SelectSession(selected);

            // 绑定媒体更新事件
            _smtc.OnMediaUpdated = (props) => {
                Dispatcher.BeginInvoke(new Action(() => {
                    // 更新基础信息
                    TxtTitle.Text = props.Title ?? "未知曲目";
                    TxtArtist.Text = props.Artist ?? "未知艺术家";
                    TxtAlbum.Text = props.AlbumTitle ?? "";

                    // 同步设置并加载歌词
                    _lyric.LyricFolder = TxtLrcPath.Text;
                    _lyric.LoadAndParse(props.Title, props.Artist);

                    // 更新歌词匹配状态 UI
                    UpdateLrcUIStatus();
                }));
            };

            _uiTimer.Start();
        }

        private void UpdateUIProgress()
        {
            // 获取包含“推算位置”的进度信息
            var info = _smtc.GetCurrentProgress();
            if (info == null) return;

            // 更新进度条和时间
            PrgBar.Maximum = info.TotalSeconds;
            PrgBar.Value = info.CurrentSeconds;
            TxtTime.Text = $"{info.CurrentStr} / {info.TotalStr}";
            TxtStatus.Text = $"状态: {info.Status}";

            // 更新歌词逻辑
            var currentPos = TimeSpan.FromSeconds(info.CurrentSeconds);
            _lyric.UpdateCurrentStatus(currentPos);

            if (_lyric.CurrentLine != null)
            {
                TxtLyricDisplay.Text = _lyric.CurrentLine.Content;
                TxtLyricTranslation.Text = _lyric.CurrentLine.Translation;
                TxtNextLyric.Text = _lyric.NextLine != null ? "下一句: " + _lyric.NextLine.Content : "";
            }
            else
            {
                // 如果没有匹配到当前时间的歌词，清空显示
                TxtLyricDisplay.Text = _lyric.Lines.Count > 0 ? "..." : "(未加载歌词)";
                TxtLyricTranslation.Text = "";
                TxtNextLyric.Text = "";
            }
        }

        private void UpdateLrcUIStatus()
        {
            if (_lyric.CurrentLyricPath != null)
            {
                TxtLrcStatus.Text = "找到: " + Path.GetFileName(_lyric.CurrentLyricPath);
                TxtLrcStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            }
            else
            {
                TxtLrcStatus.Text = "未找到歌词文件";
                TxtLrcStatus.Foreground = System.Windows.Media.Brushes.Red;
                // 没找到文件时，彻底清空歌词区
                TxtLyricDisplay.Text = "";
                TxtLyricTranslation.Text = "";
                TxtNextLyric.Text = "";
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择此文件夹" };
            if (dialog.ShowDialog() == true)
            {
                TxtLrcPath.Text = Path.GetDirectoryName(dialog.FileName);
            }
        }
    }
}