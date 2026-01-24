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

        // 核心状态控制
        private int _lastProcessedCIdx = -2;
        private Dictionary<int, string> _fingerprints = new Dictionary<int, string>();
        private int _syncTick = 0;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => TickLoop();

            // 开关点击强制刷新
            ChkAdvancedMode.Click += (s, e) => Invalidate();
            ChkIncremental.Click += (s, e) => Invalidate();
            ChkTransOccupies.Click += (s, e) => Invalidate();

            InitApp();
            LoadSettings();
        }

        private void Invalidate() { _lastProcessedCIdx = -2; _fingerprints.Clear(); }

        private async void InitApp()
        {
            await _smtc.InitializeAsync();

            // 修复点 1：确保切歌时重新加载歌词
            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;

                // 触发歌词解析
                _lyric.LoadAndParse(props.Title, props.Artist);
                UpdateLrcUIStatus();

                Invalidate();

                if (_serial.IsOpen)
                {
                    var meta = _serial.BuildMetadata(props.Title, props.Artist, props.AlbumTitle);
                    SendAndLog(meta, true); // 强制发送元数据
                }
            });

            _smtc.SessionsListChanged += () => Dispatcher.Invoke(RefreshSessions);
            RefreshSessions();
            _uiTimer.Start();
        }

        private void TickLoop()
        {
            var p = _smtc.GetCurrentProgress();
            if (p == null) return;

            // UI 基础进度刷新
            PbProgress.Maximum = p.TotalSeconds;
            PbProgress.Value = p.CurrentSeconds;
            TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

            TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
            int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

            // 修复点 2：实时预览大字歌词
            if (cIdx != -1)
            {
                var line = _lyric.Lines[cIdx];
                TxtLyricDisplay.Text = line.Content + (string.IsNullOrEmpty(line.Translation) ? "" : "\n" + line.Translation);
            }

            if (!_serial.IsOpen) return;

            // 1. 同步包 (0x11)：500ms 一次
            _syncTick++;
            if (_syncTick % 10 == 0)
            {
                var sync = _serial.BuildSync(p.Status == "Playing", (uint)curTime.TotalMilliseconds, (uint)p.TotalSeconds * 1000);
                _serial.SendRaw(sync); // 同步包不进 Log 以免刷屏，或者根据需要进
            }

            // 2. 核心拦截逻辑
            // 只有当行号变了，才进入发包环节
            if (cIdx == _lastProcessedCIdx) return;

            _lastProcessedCIdx = cIdx;
            UpdateSerialLyrics(cIdx, curTime);
        }

        private void UpdateSerialLyrics(int cIdx, TimeSpan curTime)
        {
            if (cIdx == -1) return;

            int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
            int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;
            int startIdx = Math.Max(0, cIdx - offset);
            int currentRow = 0;

            for (int i = startIdx; i < _lyric.Lines.Count && currentRow < lineLimit; i++)
            {
                var line = _lyric.Lines[i];
                string f = $"R{currentRow}_{line.Content}_{ChkAdvancedMode.IsChecked}";

                // 增量校验
                if (ChkIncremental.IsChecked == true && _fingerprints.TryGetValue(currentRow, out string last) && last == f)
                {
                    currentRow++;
                    if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation)) currentRow++;
                    continue;
                }

                _fingerprints[currentRow] = f;

                // 准备歌词包
                byte[] data = (ChkAdvancedMode.IsChecked == true && line.Words.Count > 0)
                    ? _serial.BuildWordByWord((ushort)currentRow, line.Words, line.Time)
                    : _serial.BuildLyricWithIndex(0x12, (ushort)currentRow, line.Content);

                SendAndLog(data, false);
                currentRow++;

                // 准备翻译包
                if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation))
                {
                    string tf = $"R{currentRow}_T_{line.Translation}";
                    if (ChkIncremental.IsChecked == false || !_fingerprints.ContainsKey(currentRow) || _fingerprints[currentRow] != tf)
                    {
                        _fingerprints[currentRow] = tf;
                        var tData = _serial.BuildLyricWithIndex(0x13, (ushort)currentRow, line.Translation);
                        SendAndLog(tData, false);
                    }
                    currentRow++;
                }
            }
        }

        // 修复点 3：找回各种数据包的 HEX 预览和协议解析预览
        private void SendAndLog(byte[] data, bool force)
        {
            if (data == null) return;
            _serial.SendRaw(data);

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                string hex = BitConverter.ToString(data).Replace("-", " ");

                // 1. 第一行：显示 16 进制原始数据
                p.Inlines.Add(new Run(hex + "\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                // 2. 第二行：显示协议深度解析
                if (data.Length > 2 && data[0] == 0xAA)
                {
                    byte type = data[1];
                    byte len = data[2];
                    Encoding enc = ComboEncoding.SelectedIndex == 1 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

                    switch (type)
                    {
                        case 0x10:
                            p.Inlines.Add(new Run("┃ [元数据] 更新标题/艺人") { Foreground = Brushes.Gold });
                            break;
                        case 0x12:
                            string lrc = enc.GetString(data, 5, len - 2);
                            p.Inlines.Add(new Run($"┃ [歌词] 行{data[3]}: {lrc}") { Foreground = Brushes.LimeGreen });
                            break;
                        case 0x13:
                            string trans = enc.GetString(data, 5, len - 2);
                            p.Inlines.Add(new Run($"┃ [翻译] 行{data[3]}: {trans}") { Foreground = Brushes.Orange });
                            break;
                        case 0x14:
                            ushort row = BitConverter.ToUInt16(data, 3);
                            int wordCount = data[5];
                            p.Inlines.Add(new Run($"┃ [逐字行{row}] 词数:{wordCount} -> ") { Foreground = Brushes.Cyan });

                            // 深度解析逐字流
                            int ptr = 6;
                            for (int i = 0; i < wordCount; i++)
                            {
                                if (ptr + 3 > data.Length) break;
                                ushort offset = BitConverter.ToUInt16(data, ptr);
                                byte wLen = data[ptr + 2];
                                if (ptr + 3 + wLen > data.Length) break;

                                string word = enc.GetString(data, ptr + 3, wLen);
                                p.Inlines.Add(new Run($"{word}") { Foreground = Brushes.White });
                                p.Inlines.Add(new Run($"({offset}ms) ") { Foreground = Brushes.Gray, FontSize = 9 });

                                ptr += (3 + wLen);
                            }
                            break;
                        default:
                            p.Inlines.Add(new Run($"┃ [未知] Type:0x{type:X2}"));
                            break;
                    }
                }

                HexPreview.Document.Blocks.Add(p);

                // 限制日志行数，防止 UI 内存溢出导致卡顿
                if (HexPreview.Document.Blocks.Count > 60)
                    HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);

                HexPreview.ScrollToEnd();
            });
        }

        // 基础 UI 方法保持不变
        private void RefreshSessions() { ComboSessions.ItemsSource = _smtc.GetSessions(); if (ComboSessions.Items.Count > 0) ComboSessions.SelectedIndex = 0; }
        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) => _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession);
        private void UpdateLrcUIStatus() { TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "歌词: " + Path.GetFileName(_lyric.CurrentLyricPath) : "未找到歌词"; }
        private void LoadSettings() { var cfg = ConfigService.Load(); ComboPorts.ItemsSource = _serial.GetPortNames(); ComboPorts.Text = cfg.PortName; ComboEncoding.SelectedIndex = cfg.EncodingIndex; TxtLrcPath.Text = cfg.LyricPath; TxtPatterns.Text = cfg.Patterns; TxtScreenLines.Text = cfg.ScreenLines.ToString(); TxtOffset.Text = cfg.Offset.ToString(); ChkAdvancedMode.IsChecked = cfg.AdvancedMode; ChkIncremental.IsChecked = cfg.Incremental; ChkTransOccupies.IsChecked = cfg.TransOccupies; _lyric.LyricFolder = cfg.LyricPath; }
        private void SaveAppSettings() { ConfigService.Save(new AppConfig { PortName = ComboPorts.Text, EncodingIndex = ComboEncoding.SelectedIndex, LyricPath = TxtLrcPath.Text, Patterns = TxtPatterns.Text, ScreenLines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3, Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1, AdvancedMode = ChkAdvancedMode.IsChecked ?? true, Incremental = ChkIncremental.IsChecked ?? true, TransOccupies = ChkTransOccupies.IsChecked ?? true }); }
        private void BtnSerialConn_Click(object sender, RoutedEventArgs e) { try { if (!_serial.IsOpen) { _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex; _serial.Connect(ComboPorts.Text, 115200); BtnSerialConn.Content = "断开串口"; } else { _serial.Disconnect(); BtnSerialConn.Content = "连接串口"; } } catch (Exception ex) { MessageBox.Show(ex.Message); } }
        private void BtnBrowse_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择目录" }; if (d.ShowDialog() == true) TxtLrcPath.Text = Path.GetDirectoryName(d.FileName); }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { SaveAppSettings(); base.OnClosing(e); }
    }
}