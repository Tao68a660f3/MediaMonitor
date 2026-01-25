using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO.Ports;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private readonly SmtcService _smtc = new SmtcService();
        private readonly LyricService _lyric = new LyricService();
        private readonly SerialService _serial = new SerialService();
        private DispatcherTimer _uiTimer;
        private string[] _lastPorts = Array.Empty<string>();

        // 逻辑同步池：存储已经同步过的逻辑槽位 ID
        private HashSet<string> _syncedSlots = new HashSet<string>();
        private int _lastProcessedCIdx = -2;
        private int _syncTick = 0;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateStep();

            // 绑定 UI 交互事件，触发同步池重置
            ChkAdvancedMode.Click += (s, e) => Invalidate();
            ChkIncremental.Click += (s, e) => Invalidate();
            ChkTransOccupies.Click += (s, e) => Invalidate();

            InitApp();
            LoadSettings();
        }

        private void Invalidate() { _syncedSlots.Clear(); _lastProcessedCIdx = -2; }

        private async void InitApp()
        {
            await _smtc.InitializeAsync();
            _smtc.SessionsListChanged += () => Dispatcher.Invoke(() => RefreshSessions());

            // 播放状态改变监听
            _smtc.PlaybackChanged += (status) => Dispatcher.Invoke(() => SyncPlaybackState(status));

            // 媒体内容改变监听
            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;
                TxtAlbum.Text = props.AlbumTitle; // 重新接回专辑显示

                _lyric.LoadAndParse(props.Title, props.Artist);
                TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "歌词载入成功" : "未找到歌词";

                Invalidate();
                SyncMetadata(props.Title, props.Artist, props.AlbumTitle);
            });

            RefreshSessions();
            _uiTimer.Start();
        }

        private void SyncMetadata(string title, string artist, string album)
        {
            if (!_serial.IsOpen) return;
            if (ChkAdvancedMode.IsChecked == true)
            {
                SendAndLog(_serial.BuildMetadata(title, artist, album));
            }
            else
            {
                // 纯文本退化：发送带有前缀的媒体信息
                string raw = $">> {title} / {artist} / {album}\n";
                _serial.SendRaw(_serial.GetEncodedBytes(raw));
                LogToPreview("[RAW META] " + raw, Brushes.Yellow);
            }
        }

        private void SyncPlaybackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            if (!_serial.IsOpen || ChkAdvancedMode.IsChecked == false) return;
            var p = _smtc.GetCurrentProgress();
            if (p != null)
            {
                bool isPlaying = (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                _serial.SendRaw(_serial.BuildSync(isPlaying, (uint)(p.CurrentSeconds * 1000), (uint)(p.TotalSeconds * 1000)));
            }
        }

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboSessions.SelectedItem is GlobalSystemMediaTransportControlsSession session)
            {
                _smtc.SelectSession(session);
                Invalidate(); // 切换会话时，清空同步池重新发送新歌信息
            }
        }

        private void UpdateStep()
        {
            CheckPorts();
            var p = _smtc.GetCurrentProgress();
            if (p == null) { ResetUI(); return; }

            PbProgress.Maximum = p.TotalSeconds;
            PbProgress.Value = p.CurrentSeconds;
            TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

            TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
            int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

            if (cIdx != -1)
            {
                var l = _lyric.GetLine(cIdx);
                TxtLyricDisplay.Text = l.Content + (string.IsNullOrEmpty(l.Translation) ? "" : "\n" + l.Translation);
            }

            if (!_serial.IsOpen) return;

            // 定期进度同步 (0x11)
            if (ChkAdvancedMode.IsChecked == true && ++_syncTick % 10 == 0)
            {
                _serial.SendRaw(_serial.BuildSync(p.Status == "Playing", (uint)(curTime.TotalMilliseconds), (uint)(p.TotalSeconds * 1000)));
            }

            if (cIdx != _lastProcessedCIdx)
            {
                _lastProcessedCIdx = cIdx;
                HandleOutput(cIdx);
            }
        }

        private void HandleOutput(int cIdx)
        {
            int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
            int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;
            bool isAdvanced = ChkAdvancedMode.IsChecked == true;
            bool transOccupies = ChkTransOccupies.IsChecked == true;

            var targetSlots = new HashSet<string>();
            var dataToSync = new Dictionary<string, (byte[] AdvData, string RawText)>();

            int startIdx = cIdx - offset;

            // 根据是否占行，计算需要生成的逻辑槽位
            if (transOccupies)
            {
                for (int i = 0; i < lineLimit; i++)
                {
                    int lyricIdx = startIdx + (i / 2);
                    bool isTransRow = (i % 2 != 0);
                    string type = isTransRow ? "0x13" : "0x12";
                    string key = $"{lyricIdx}_{type}";
                    targetSlots.Add(key);

                    var line = _lyric.GetLine(lyricIdx);
                    if (isTransRow)
                        dataToSync[key] = (_serial.BuildTranslationLine((short)lyricIdx, line.Time, line.Translation), line.Translation);
                    else
                        dataToSync[key] = (line.Words.Count > 0 ? _serial.BuildWordByWord((short)lyricIdx, line.Time, line.Words) : _serial.BuildLyricLine((short)lyricIdx, line.Time, line.Content), line.Content);
                }
            }
            else
            {
                for (int i = 0; i < lineLimit; i++)
                {
                    int lyricIdx = startIdx + i;
                    var line = _lyric.GetLine(lyricIdx);

                    string mKey = $"{lyricIdx}_0x12";
                    targetSlots.Add(mKey);
                    dataToSync[mKey] = (line.Words.Count > 0 ? _serial.BuildWordByWord((short)lyricIdx, line.Time, line.Words) : _serial.BuildLyricLine((short)lyricIdx, line.Time, line.Content), line.Content);

                    string tKey = $"{lyricIdx}_0x13";
                    targetSlots.Add(tKey);
                    dataToSync[tKey] = (_serial.BuildTranslationLine((short)lyricIdx, line.Time, line.Translation), line.Translation);
                }
            }

            // 差分计算
            IEnumerable<string> toNotify = (ChkIncremental.IsChecked == true) ? targetSlots.Except(_syncedSlots) : targetSlots;

            foreach (var slot in toNotify)
            {
                if (!dataToSync.TryGetValue(slot, out var pack)) continue;

                if (isAdvanced)
                {
                    SendAndLog(pack.AdvData);
                }
                else
                {
                    // 纯文本模式：行号不足时通过空字符串清理，行偏移由 startIdx 决定
                    string raw = pack.RawText + "\n";
                    _serial.SendRaw(_serial.GetEncodedBytes(raw));
                    LogToPreview($"[RAW] {raw.Trim()}", Brushes.Yellow);
                }
            }
            _syncedSlots = targetSlots;
        }

        private void SendAndLog(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            _serial.SendRaw(data);
            if (data[1] == 0x11) return; // 忽略同步包日志

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run($"{hex}\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                Encoding enc = (ComboEncoding.SelectedIndex == 1) ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;
                byte cmd = data[1];
                Run tag = new Run { Foreground = Brushes.White };
                string detail = "";

                if (cmd == 0x10)
                {
                    tag.Text = " [元数据] "; tag.Background = Brushes.DarkBlue;
                    detail = DecodeMeta(data, enc);
                }
                else if (cmd == 0x12 || cmd == 0x13)
                {
                    tag.Text = cmd == 0x12 ? " [主体行] " : " [翻译行] ";
                    tag.Background = cmd == 0x12 ? Brushes.DarkGreen : Brushes.DarkSlateBlue;
                    detail = DecodeStandard(data, enc);
                }
                else if (cmd == 0x14)
                {
                    tag.Text = " [逐字行] "; tag.Background = Brushes.DarkRed;
                    detail = DecodeWordByWord(data, enc);
                }

                p.Inlines.Add(tag);
                p.Inlines.Add(new Run(" " + detail) { Foreground = Brushes.White });
                HexPreview.Document.Blocks.Add(p);
                if (HexPreview.Document.Blocks.Count > 50) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
                HexPreview.ScrollToEnd();
            });
        }

        private string DecodeMeta(byte[] data, Encoding enc)
        {
            try
            {
                int ptr = 3; List<string> res = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    int len = data[ptr]; res.Add(enc.GetString(data, ptr + 1, len));
                    ptr += (1 + len);
                }
                return string.Join(" | ", res);
            }
            catch { return "解析失败"; }
        }

        private string DecodeStandard(byte[] data, Encoding enc)
        {
            short idx = BitConverter.ToInt16(data, 3);
            uint time = BitConverter.ToUInt32(data, 5);
            string txt = enc.GetString(data, 9, data.Length - 10);
            return $"({idx:D3}) [{time}ms] {txt}";
        }

        private string DecodeWordByWord(byte[] data, Encoding enc)
        {
            short idx = BitConverter.ToInt16(data, 3);
            uint time = BitConverter.ToUInt32(data, 5);
            StringBuilder sb = new StringBuilder($"({idx:D3}) [{time}ms] ");
            int ptr = 10;
            for (int i = 0; i < data[9]; i++)
            {
                ushort off = BitConverter.ToUInt16(data, ptr);
                byte len = data[ptr + 2];
                sb.Append($"{enc.GetString(data, ptr + 3, len)}<{off}ms> ");
                ptr += (3 + len);
            }
            return sb.ToString();
        }

        private void LogToPreview(string msg, Brush color)
        {
            Dispatcher.Invoke(() => {
                HexPreview.Document.Blocks.Add(new Paragraph(new Run(msg) { Foreground = color }));
                HexPreview.ScrollToEnd();
            });
        }

        private void CheckPorts()
        {
            var p = _serial.GetPortNames();
            if (!p.SequenceEqual(_lastPorts))
            {
                _lastPorts = p; string old = ComboPorts.Text;
                ComboPorts.ItemsSource = p; if (p.Contains(old)) ComboPorts.Text = old;
            }
        }

        private void ResetUI()
        {
            if (TxtTitle.Text == "无媒体") return;
            TxtTitle.Text = "无媒体"; TxtArtist.Text = "等待播放..."; TxtAlbum.Text = ""; TxtLyricDisplay.Text = "";
        }

        private void RefreshSessions() => ComboSessions.ItemsSource = _smtc.GetSessions();

        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen) { _serial.Connect(ComboPorts.Text, 115200); BtnSerialConn.Content = "断开"; }
                else { _serial.Disconnect(); BtnSerialConn.Content = "连接"; }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void LoadSettings()
        {
            var cfg = ConfigService.Load();
            ComboPorts.ItemsSource = _serial.GetPortNames();
            ComboPorts.Text = cfg.PortName;
            ComboEncoding.SelectedIndex = cfg.EncodingIndex;
            TxtLrcPath.Text = cfg.LyricPath;
            TxtPatterns.Text = cfg.Patterns;
            TxtScreenLines.Text = cfg.ScreenLines.ToString();
            TxtOffset.Text = cfg.Offset.ToString();
            ChkAdvancedMode.IsChecked = cfg.AdvancedMode;
            ChkIncremental.IsChecked = cfg.Incremental;
            ChkTransOccupies.IsChecked = cfg.TransOccupies;
            _lyric.LyricFolder = cfg.LyricPath;
        }

        private void SaveAppSettings()
        {
            ConfigService.Save(new AppConfig
            {
                PortName = ComboPorts.Text,
                EncodingIndex = ComboEncoding.SelectedIndex,
                LyricPath = TxtLrcPath.Text,
                Patterns = TxtPatterns.Text,
                ScreenLines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3,
                Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1,
                AdvancedMode = ChkAdvancedMode.IsChecked ?? true,
                Incremental = ChkIncremental.IsChecked ?? true,
                TransOccupies = ChkTransOccupies.IsChecked ?? true
            });
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择目录" };
            if (d.ShowDialog() == true) TxtLrcPath.Text = System.IO.Path.GetDirectoryName(d.FileName);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveAppSettings();
            base.OnClosing(e);
        }
    }
}