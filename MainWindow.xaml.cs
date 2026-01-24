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
                // 关键：指纹包含内容和它当前在屏幕上的行号
                string f = $"R{currentRow}_{line.Content}_{ChkAdvancedMode.IsChecked}";

                // 【增量核心】如果开启增量且指纹已存在，证明这行已经在硬件屏幕对应位置了，直接跳过
                if (ChkIncremental.IsChecked == true && _fingerprints.TryGetValue(currentRow, out string last) && last == f)
                {
                    currentRow++;
                    // 翻译行同理
                    if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation)) currentRow++;
                    continue;
                }

                // 记录新快照
                _fingerprints[currentRow] = f;

                // 发送新行数据
                byte[] data = (ChkAdvancedMode.IsChecked == true && line.Words.Count > 0)
                    ? _serial.BuildWordByWord((ushort)currentRow, line.Words, line.Time)
                    : _serial.BuildLyricWithIndex(0x12, (ushort)currentRow, line.Content);

                SendAndLog(data);
                currentRow++;

                // 处理翻译行的增量发送
                if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation))
                {
                    string tf = $"R{currentRow}_T_{line.Translation}";
                    if (ChkIncremental.IsChecked == false || !_fingerprints.ContainsKey(currentRow) || _fingerprints[currentRow] != tf)
                    {
                        _fingerprints[currentRow] = tf;
                        SendAndLog(_serial.BuildLyricWithIndex(0x13, (ushort)currentRow, line.Translation));
                    }
                    currentRow++;
                }
            }
        }

        // 修复点 3：找回各种数据包的 HEX 预览和协议解析预览
        private void SendAndLog(byte[] data, bool force = false)
        {
            if (data == null || !_serial.IsOpen) return;
            _serial.SendRaw(data);

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run(hex + "\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                if (data.Length > 2 && data[0] == 0xAA)
                {
                    byte type = data[1];
                    byte len = data[2];
                    Encoding enc = ComboEncoding.SelectedIndex == 1 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

                    switch (type)
                    {
                        case 0x10: // 元数据：解析 标题、艺人、唱片集
                            try
                            {
                                int tLen = data[3];
                                string title = enc.GetString(data, 4, tLen);
                                int aLen = data[4 + tLen];
                                string artist = enc.GetString(data, 5 + tLen, aLen);
                                int albLen = data[5 + tLen + aLen];
                                string album = enc.GetString(data, 6 + tLen + aLen, albLen);
                                p.Inlines.Add(new Run($"┃ [元数据] 🎵:{title}  👤:{artist}  💿:{album}") { Foreground = Brushes.Gold});
                            }
                            catch { p.Inlines.Add(new Run("┃ [元数据] 解析失败")); }
                            break;

                        case 0x11: // 同步包
                            uint cMs = BitConverter.ToUInt16(data, 4); // 这里根据你 Smtc 的定义取值
                            p.Inlines.Add(new Run($"┃ [同步] 状态:{(data[3] == 1 ? "播放" : "暂停")} 进度:{cMs}ms") { Foreground = Brushes.DeepSkyBlue });
                            break;

                        case 0x12: // 普通歌词
                            p.Inlines.Add(new Run($"┃ [歌词] 行{data[3]}: {enc.GetString(data, 5, len - 2)}") { Foreground = Brushes.LimeGreen });
                            break;

                        case 0x13: // 翻译
                            p.Inlines.Add(new Run($"┃ [翻译] 行{data[3]}: {enc.GetString(data, 5, len - 2)}") { Foreground = Brushes.Orange });
                            break;

                        case 0x14: // 逐字深度解析
                            ushort row = BitConverter.ToUInt16(data, 3);
                            int wordCount = data[5];
                            p.Inlines.Add(new Run($"┃ [逐字{row}] ") { Foreground = Brushes.Cyan });
                            int ptr = 6;
                            for (int i = 0; i < wordCount; i++)
                            {
                                if (ptr + 3 > data.Length) break;
                                ushort offset = BitConverter.ToUInt16(data, ptr);
                                byte wLen = data[ptr + 2];
                                string wText = enc.GetString(data, ptr + 3, wLen);
                                p.Inlines.Add(new Run(wText) { Foreground = Brushes.White });
                                p.Inlines.Add(new Run($"<{offset}> ") { Foreground = Brushes.Gray, FontSize = 9 });
                                ptr += (3 + wLen);
                            }
                            break;
                    }
                }
                HexPreview.Document.Blocks.Add(p);
                if (HexPreview.Document.Blocks.Count > 50) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
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