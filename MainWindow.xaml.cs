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
            ChkIncremental.Click += (s, e) => Invalidate();

            InitApp();
            LoadSettings();
        }

        private void Invalidate() { _lastSentFingerprints.Clear(); _lastProcessedCIdx = -2; }

        private async void InitApp()
        {
            await _smtc.InitializeAsync();
            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;
                _lyric.LoadAndParse(props.Title, props.Artist);
                TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "歌词已载入" : "未找到歌词";
                Invalidate();

                if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                    SendAndLog(_serial.BuildMetadata(props.Title, props.Artist, props.AlbumTitle));
            });

            ComboEncoding.SelectionChanged += (s, e) => {
                if (_serial != null)
                {
                    // 同步串口对象的编码
                    _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex;
                    // 清空预览，防止旧的乱码干扰视线
                    HexPreview.Document.Blocks.Clear();
                    // 重置增量指纹，强制触发一次全量发送
                    Invalidate();
                }
            };

            ComboSessions.ItemsSource = _smtc.GetSessions();
            _uiTimer.Start();
        }

        private void UpdateStep()
        {
            var p = _smtc.GetCurrentProgress();
            if (p == null) return;

            // UI 基础进度刷新
            PbProgress.Maximum = p.TotalSeconds;
            PbProgress.Value = p.CurrentSeconds;
            TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

            TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
            int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

            // 【补回歌词预览】刷新界面上的 TextBlock
            if (cIdx != -1)
            {
                var line = _lyric.Lines[cIdx];
                TxtLyricDisplay.Text = line.Content + (string.IsNullOrEmpty(line.Translation) ? "" : "\n" + line.Translation);
            }
            else { TxtLyricDisplay.Text = ""; }

            if (!_serial.IsOpen) return;

            // 同步包逻辑
            if (ChkAdvancedMode.IsChecked == true)
            {
                _syncTick++;
                if (_syncTick % 10 == 0)
                    _serial.SendRaw(_serial.BuildSync(p.Status == "Playing", (uint)curTime.TotalMilliseconds, (uint)p.TotalSeconds * 1000));
            }

            // 歌词处理
            if (cIdx != _lastProcessedCIdx)
            {
                _lastProcessedCIdx = cIdx;
                HandleOutput(cIdx);
            }
        }

        private void HandleOutput(int cIdx)
        {
            if (cIdx == -1) return;
            var line = _lyric.Lines[cIdx];

            // 模式 A: 纯文本模式 (独立裸发)
            if (ChkAdvancedMode.IsChecked == false)
            {
                if (ChkIncremental.IsChecked == true && _lastSentFingerprints.TryGetValue(-1, out string old) && old == line.Content) return;
                _lastSentFingerprints[-1] = line.Content;

                byte[] rawData = _serial.GetEncodedBytes(line.Content);
                SendAndLog(rawData, isRawText: true);
            }
            // 模式 B: 高级协议模式
            else
            {
                int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
                int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;
                int startIdx = Math.Max(0, cIdx - offset);
                int currentRow = 0;

                for (int i = startIdx; i < _lyric.Lines.Count && currentRow < lineLimit; i++)
                {
                    var l = _lyric.Lines[i];
                    string finger = $"R{currentRow}_{l.Content}";

                    if (ChkIncremental.IsChecked == true && _lastSentFingerprints.TryGetValue(currentRow, out string old) && old == finger)
                    {
                        currentRow++; continue;
                    }

                    _lastSentFingerprints[currentRow] = finger;
                    byte[] data = (l.Words.Count > 0)
                        ? _serial.BuildWordByWord((ushort)currentRow, l.Words, l.Time)
                        : _serial.BuildLyricWithIndex(0x12, (ushort)currentRow, l.Content);

                    SendAndLog(data);
                    currentRow++;
                }
            }
        }

        private void SendAndLog(byte[] data, bool isRawText = false)
        {
            if (data == null) return;
            _serial.SendRaw(data);

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };

                // --- 核心修复：每次发送时实时获取当前 UI 选定的编码 ---
                Encoding viewEnc;
                try
                {
                    // 根据 ComboBox 指数实时选择编码
                    viewEnc = (ComboEncoding.SelectedIndex == 1)
                        ? Encoding.GetEncoding("GB2312")
                        : Encoding.UTF8;
                }
                catch
                {
                    viewEnc = Encoding.UTF8; // 保底方案
                }

                // 1. 显示原始 HEX (无论如何不会乱码)
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run(hex + "\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                // 2. 文本解析部分
                if (isRawText)
                {
                    // 纯文本模式：直接用当前编码还原
                    string text = viewEnc.GetString(data);
                    p.Inlines.Add(new Run($"┃ [纯文本发送] {text}") { Foreground = Brushes.White, FontWeight = FontWeights.Bold });
                }
                else if (data.Length > 2 && data[0] == 0xAA)
                {
                    byte type = data[1];
                    switch (type)
                    {
                        case 0x10: // 元数据
                            p.Inlines.Add(new Run(DecodeMeta(data, viewEnc)) { Foreground = Brushes.Gold });
                            break;
                        case 0x12: // 协议歌词
                            string lyric = viewEnc.GetString(data, 5, data[2] - 2);
                            p.Inlines.Add(new Run($"┃ [协议歌词] {lyric}") { Foreground = Brushes.LimeGreen });
                            break;
                        case 0x14: // 逐字包
                            p.Inlines.Add(new Run($"┃ [协议逐字] 行{data[3]}: ") { Foreground = Brushes.Cyan });
                            p.Inlines.Add(new Run(DecodeWords(data, viewEnc)));
                            break;
                    }
                }

                HexPreview.Document.Blocks.Add(p);

                // 自动清理，防止内存占用过高
                if (HexPreview.Document.Blocks.Count > 100)
                    HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);

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

        private void RefreshSessions() { ComboSessions.ItemsSource = _smtc.GetSessions(); if (ComboSessions.Items.Count > 0) ComboSessions.SelectedIndex = 0; }
        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) => _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession);
        private void BtnSerialConn_Click(object sender, RoutedEventArgs e) { try { if (!_serial.IsOpen) { _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex; _serial.Connect(ComboPorts.Text, 115200); BtnSerialConn.Content = "断开串口"; } else { _serial.Disconnect(); BtnSerialConn.Content = "连接串口"; } } catch (Exception ex) { MessageBox.Show(ex.Message); } }
        private void LoadSettings() { var cfg = ConfigService.Load(); ComboPorts.ItemsSource = _serial.GetPortNames(); ComboPorts.Text = cfg.PortName; ComboEncoding.SelectedIndex = cfg.EncodingIndex; TxtLrcPath.Text = cfg.LyricPath; TxtPatterns.Text = cfg.Patterns; TxtScreenLines.Text = cfg.ScreenLines.ToString(); TxtOffset.Text = cfg.Offset.ToString(); ChkAdvancedMode.IsChecked = cfg.AdvancedMode; ChkIncremental.IsChecked = cfg.Incremental; ChkTransOccupies.IsChecked = cfg.TransOccupies; _lyric.LyricFolder = cfg.LyricPath; }
        private void SaveAppSettings() { ConfigService.Save(new AppConfig { PortName = ComboPorts.Text, EncodingIndex = ComboEncoding.SelectedIndex, LyricPath = TxtLrcPath.Text, Patterns = TxtPatterns.Text, ScreenLines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3, Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1, AdvancedMode = ChkAdvancedMode.IsChecked ?? true, Incremental = ChkIncremental.IsChecked ?? true, TransOccupies = ChkTransOccupies.IsChecked ?? true }); }
        private void BtnBrowse_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择目录" }; if (d.ShowDialog() == true) TxtLrcPath.Text = Path.GetDirectoryName(d.FileName); }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { SaveAppSettings(); base.OnClosing(e); }
    }
}