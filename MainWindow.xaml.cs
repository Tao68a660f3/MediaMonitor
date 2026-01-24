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

        // 【核心】增量快照寄存器：Key是屏幕行号(0,1,2...)，Value是该行内容的唯一识别ID
        // 识别ID由“行号+内容原文+模式”组成，不含任何随时间变化的变量！
        private Dictionary<int, string> _lastSentFingerprints = new Dictionary<int, string>();
        private int _tickCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // UI 刷新维持在 50ms 确保进度平滑
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateUIProgress();

            // 监听所有会改变显示效果的开关，一旦点击立刻清空快照，强制让下一帧重新发包同步硬件
            ChkAdvancedMode.Click += (s, e) => ResetMarkers();
            ChkIncremental.Click += (s, e) => ResetMarkers();
            ChkTransOccupies.Click += (s, e) => ResetMarkers();

            InitApp();
            LoadSettings();
        }

        private void ResetMarkers() => _lastSentFingerprints.Clear();

        private async void InitApp()
        {
            await _smtc.InitializeAsync();
            _smtc.SessionsListChanged += () => Dispatcher.Invoke(RefreshSessions);
            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;
                TxtAlbum.Text = props.AlbumTitle;
                _lyric.LoadAndParse(props.Title, props.Artist);
                UpdateLrcUIStatus();
                ResetMarkers(); // 切歌时必须清空，强制刷新

                if (_serial.IsOpen)
                {
                    var meta = _serial.BuildMetadata(props.Title, props.Artist, props.AlbumTitle);
                    SendAndLog(meta);
                }
            });
            RefreshSessions();
            _uiTimer.Start();
        }

        private void UpdateUIProgress()
        {
            var p = _smtc.GetCurrentProgress();
            if (p == null) return;

            // 1. 更新 UI 组件
            PbProgress.Maximum = p.TotalSeconds;
            PbProgress.Value = p.CurrentSeconds;
            TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

            TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
            int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

            // 2. 更新大字预览
            if (cIdx != -1)
            {
                var line = _lyric.Lines[cIdx];
                TxtLyricDisplay.Text = line.Content + (string.IsNullOrEmpty(line.Translation) ? "" : "\n" + line.Translation);
            }
            else
            {
                TxtLyricDisplay.Text = "(等待歌词...)";
            }

            // 3. 处理串口通讯（内含严密的增量逻辑）
            ProcessSerialLogic(curTime, p, cIdx);
        }

        private void ProcessSerialLogic(TimeSpan curTime, MediaProgressInfo progress, int cIdx)
        {
            if (!_serial.IsOpen) return;
            _tickCount++;

            // A. 同步包 (0x11)：每 500ms 发送一次，用于同步硬件的时钟轴
            if (_tickCount % 10 == 0)
            {
                var sync = _serial.BuildSync(progress.Status == "Playing", (uint)curTime.TotalMilliseconds, (uint)progress.TotalSeconds * 1000);
                _serial.SendRaw(sync);
            }

            // B. 计算屏幕窗口
            int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
            int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;

            if (cIdx == -1) return;
            int startIdx = Math.Max(0, cIdx - offset);
            int currentRow = 0;

            // C. 逐行扫描并根据指纹判断是否发包
            for (int i = startIdx; i < _lyric.Lines.Count && currentRow < lineLimit; i++)
            {
                var line = _lyric.Lines[i];

                // 【核心改进】构造指纹：行号 + 歌词原文 + 是否逐字模式
                // 只有这三个条件任一改变，才说明这一行需要更新
                string fingerprint = $"R{currentRow}_C{line.Content}_M{ChkAdvancedMode.IsChecked}";

                if (ChkIncremental.IsChecked == true)
                {
                    if (_lastSentFingerprints.TryGetValue(currentRow, out string old) && old == fingerprint)
                    {
                        currentRow++; // 跳过发送，但计数器增加
                        // 处理翻译占行逻辑
                        if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation))
                        {
                            currentRow++;
                        }
                        continue;
                    }
                }

                // 记录新指纹并执行发送
                _lastSentFingerprints[currentRow] = fingerprint;

                byte[] data;
                if (ChkAdvancedMode.IsChecked == true && line.Words.Count > 0)
                    data = _serial.BuildWordByWord((ushort)currentRow, line.Words, line.Time);
                else
                    data = _serial.BuildLyricWithIndex(0x12, (ushort)currentRow, line.Content);

                SendAndLog(data);
                currentRow++;

                // 处理翻译行
                if (currentRow < lineLimit && ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation))
                {
                    string tFinger = $"R{currentRow}_T{line.Translation}";
                    if (ChkIncremental.IsChecked == false || !_lastSentFingerprints.ContainsKey(currentRow) || _lastSentFingerprints[currentRow] != tFinger)
                    {
                        _lastSentFingerprints[currentRow] = tFinger;
                        var tData = _serial.BuildLyricWithIndex(0x13, (ushort)currentRow, line.Translation);
                        SendAndLog(tData);
                    }
                    currentRow++;
                }
            }
        }

        private void SendAndLog(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            _serial.SendRaw(data);

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0) };
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run(hex + " ") { Foreground = Brushes.DimGray, FontSize = 10 });

                // 解析显示
                if (data.Length > 2 && data[0] == 0xAA)
                {
                    byte type = data[1];
                    Encoding enc = ComboEncoding.SelectedIndex == 1 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;
                    switch (type)
                    {
                        case 0x10: p.Inlines.Add(new Run("┃ [元数据]") { Foreground = Brushes.Gold }); break;
                        case 0x11: p.Inlines.Add(new Run("┃ [同步]") { Foreground = Brushes.DeepSkyBlue }); break;
                        case 0x12:
                        case 0x13:
                            string txt = enc.GetString(data, 5, data[2] - 2);
                            p.Inlines.Add(new Run($"┃ [{(type == 0x12 ? "歌词" : "翻译")}] {txt}") { Foreground = Brushes.SpringGreen });
                            break;
                        case 0x14:
                            ushort row = BitConverter.ToUInt16(data, 3);
                            p.Inlines.Add(new Run($"┃ [逐字行{row}] ") { Foreground = Brushes.Cyan });
                            int ptr = 6;
                            for (int j = 0; j < data[5]; j++)
                            {
                                if (ptr + 3 > data.Length) break;
                                byte wLen = data[ptr + 2];
                                p.Inlines.Add(new Run(enc.GetString(data, ptr + 3, wLen) + "|"));
                                ptr += (3 + wLen);
                            }
                            break;
                    }
                }
                HexPreview.Document.Blocks.Add(p);
                if (HexPreview.Document.Blocks.Count > 100) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
                HexPreview.ScrollToEnd();
            });
        }

        // --- 基础UI逻辑保持完整 ---
        private void RefreshSessions() { ComboSessions.ItemsSource = _smtc.GetSessions(); if (ComboSessions.Items.Count > 0) ComboSessions.SelectedIndex = 0; }
        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) => _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession);
        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
                    _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex;
                    _serial.Connect(ComboPorts.Text, 115200);
                    BtnSerialConn.Content = "断开串口"; BtnSerialConn.Background = Brushes.IndianRed;
                }
                else
                {
                    _serial.Disconnect();
                    BtnSerialConn.Content = "连接串口"; BtnSerialConn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
        private void UpdateLrcUIStatus() { TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "找到: " + Path.GetFileName(_lyric.CurrentLyricPath) : "未找到歌词"; }
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
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { SaveAppSettings(); base.OnClosing(e); }
    }
}