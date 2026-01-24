using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private readonly SmtcService _smtc = new SmtcService();
        private readonly LyricService _lyric = new LyricService();
        private readonly SerialService _serial = new SerialService();
        private DispatcherTimer _uiTimer;

        private Dictionary<int, string> _lastSentFingerprints = new Dictionary<int, string>();
        private int _lastProcessedCIdx = -2;
        private int _syncTick = 0;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateStep();

            ChkAdvancedMode.Click += (s, e) => Invalidate();
            // ChkIncremental 已经在 XAML 中隐藏

            InitApp();
            LoadSettings();
        }

        private void Invalidate() { _lastSentFingerprints.Clear(); _lastProcessedCIdx = -2; }

        private async void InitApp()
        {
            await _smtc.InitializeAsync();

            // 使用你代码中已有的 SessionsListChanged 事件
            _smtc.SessionsListChanged += () => Dispatcher.Invoke(() => RefreshSessions());

            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;
                TxtAlbum.Text = props.AlbumTitle;
                _lyric.LoadAndParse(props.Title, props.Artist);
                TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "歌词已载入" : "未找到歌词";
                Invalidate();

                if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                    SendAndLog(_serial.BuildMetadata(props.Title, props.Artist, props.AlbumTitle));
            });

            ComboEncoding.SelectionChanged += (s, e) => {
                if (_serial != null)
                {
                    _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex;
                    HexPreview.Document.Blocks.Clear();
                    Invalidate();
                }
            };

            RefreshSessions();
            _uiTimer.Start();
        }

        private void UpdateStep()
        {
            try
            {
                var p = _smtc.GetCurrentProgress(); //
                if (p == null)
                {
                    ResetUI();
                    return;
                }

                PbProgress.Maximum = p.TotalSeconds;
                PbProgress.Value = p.CurrentSeconds;
                TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

                TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
                int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

                if (cIdx != -1)
                {
                    var line = _lyric.Lines[cIdx];
                    TxtLyricDisplay.Text = line.Content + (string.IsNullOrEmpty(line.Translation) ? "" : "\n" + line.Translation);
                }
                else { TxtLyricDisplay.Text = ""; }

                if (!_serial.IsOpen) return;

                if (ChkAdvancedMode.IsChecked == true)
                {
                    _syncTick++;
                    if (_syncTick % 10 == 0)
                        _serial.SendRaw(_serial.BuildSync(p.Status == "Playing", (uint)curTime.TotalMilliseconds, (uint)p.TotalSeconds * 1000));
                }

                if (cIdx != _lastProcessedCIdx)
                {
                    _lastProcessedCIdx = cIdx;
                    HandleOutput(cIdx);
                }
            }
            catch
            {
                ResetUI();
            }
        }

        private void ResetUI()
        {
            if (TxtTitle.Text == "无媒体") return;
            Dispatcher.Invoke(() => {
                TxtTitle.Text = "无媒体";
                TxtArtist.Text = "等待播放器开启...";
                TxtLyricDisplay.Text = "";
                PbProgress.Value = 0;
                _lastProcessedCIdx = -2;
            });
        }

        private void HandleOutput(int cIdx)
        {
            if (cIdx == -1 || cIdx >= _lyric.Lines.Count) return;
            var line = _lyric.Lines[cIdx];

            if (ChkAdvancedMode.IsChecked == false)
            {
                string fullContent = line.Content;
                if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation))
                    fullContent += " " + line.Translation;

                if (ChkIncremental.IsChecked == true && _lastSentFingerprints.TryGetValue(-1, out string old) && old == fullContent) return;
                _lastSentFingerprints[-1] = fullContent;
                SendAndLog(_serial.GetEncodedBytes(fullContent), isRawText: true);
            }
            else
            {
                int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
                int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;
                int startIdx = Math.Max(0, cIdx - offset);
                int currentRow = 0;

                for (int i = startIdx; i < _lyric.Lines.Count && currentRow < lineLimit; i++)
                {
                    var l = _lyric.Lines[i];
                    string f1 = $"R{currentRow}_{l.Content}";
                    if (!(ChkIncremental.IsChecked == true && _lastSentFingerprints.TryGetValue(currentRow, out string o1) && o1 == f1))
                    {
                        _lastSentFingerprints[currentRow] = f1;
                        byte[] d1 = (l.Words.Count > 0)
                            ? _serial.BuildWordByWord((ushort)currentRow, l.Words, l.Time)
                            : _serial.BuildLyricWithIndex(0x12, (ushort)currentRow, l.Content);
                        SendAndLog(d1);
                    }
                    currentRow++;

                    if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(l.Translation))
                    {
                        string f2 = $"R{currentRow}_T_{l.Translation}";
                        if (!(ChkIncremental.IsChecked == true && _lastSentFingerprints.TryGetValue(currentRow, out string o2) && o2 == f2))
                        {
                            _lastSentFingerprints[currentRow] = f2;
                            SendAndLog(_serial.BuildLyricWithIndex(0x13, (ushort)currentRow, l.Translation));
                        }
                        currentRow++;
                    }
                }
            }
        }

        private void SendAndLog(byte[] data, bool isRawText = false)
        {
            if (data == null) return;
            _serial.SendRaw(data);

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                Encoding viewEnc = (ComboEncoding.SelectedIndex == 1) ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run(hex + "\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                if (isRawText)
                {
                    p.Inlines.Add(new Run($"┃ [纯文本] {viewEnc.GetString(data)}") { Foreground = Brushes.White });
                }
                else if (data.Length > 2 && data[0] == 0xAA)
                {
                    byte type = data[1];
                    switch (type)
                    {
                        case 0x10: p.Inlines.Add(new Run(DecodeMeta(data, viewEnc)) { Foreground = Brushes.Gold }); break;
                        case 0x12: p.Inlines.Add(new Run($"┃ [歌词] {viewEnc.GetString(data, 5, data[2] - 2)}") { Foreground = Brushes.LimeGreen }); break;
                        case 0x13: p.Inlines.Add(new Run($"┃ [翻译] {viewEnc.GetString(data, 5, data[2] - 2)}") { Foreground = Brushes.Orange }); break;
                        case 0x14: p.Inlines.Add(new Run($"┃ [逐字] {DecodeWords(data, viewEnc)}") { Foreground = Brushes.Cyan }); break;
                    }
                }
                HexPreview.Document.Blocks.Add(p);
                if (HexPreview.Document.Blocks.Count > 50) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
                HexPreview.ScrollToEnd();
            });
        }

        private string DecodeMeta(byte[] data, Encoding enc)
        {
            try
            {
                int tLen = data[3]; string t = enc.GetString(data, 4, tLen);
                int aLen = data[4 + tLen]; string r = enc.GetString(data, 5 + tLen, aLen);
                int bLen = data[5 + tLen + aLen]; string b = enc.GetString(data, 6 + tLen + aLen, bLen);
                return $"┃ [媒体更新] 🎵:{t} | 👤:{r} | 💿:{b}";
            }
            catch { return "┃ [媒体更新] 解析失败"; }
        }

        private string DecodeWords(byte[] data, Encoding enc)
        {
            StringBuilder sb = new StringBuilder();
            int count = data[5]; int ptr = 6;
            for (int i = 0; i < count; i++)
            {
                if (ptr + 2 >= data.Length) break;
                ushort offset = BitConverter.ToUInt16(data, ptr);
                byte len = data[ptr + 2];
                sb.Append(enc.GetString(data, ptr + 3, len) + $"<{offset}> ");
                ptr += (3 + len);
            }
            return sb.ToString();
        }

        private void RefreshSessions()
        {
            var sessions = _smtc.GetSessions(); //
            ComboSessions.ItemsSource = sessions;
            if (ComboSessions.SelectedIndex == -1 && sessions.Any()) ComboSessions.SelectedIndex = 0;
        }

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession); //

        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
                    _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex;
                    _serial.Connect(ComboPorts.Text, 115200);
                    BtnSerialConn.Content = "断开串口";
                }
                else
                {
                    _serial.Disconnect();
                    BtnSerialConn.Content = "连接串口";
                }
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
            if (d.ShowDialog() == true) TxtLrcPath.Text = Path.GetDirectoryName(d.FileName);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveAppSettings();
            base.OnClosing(e);
        }
    }
}