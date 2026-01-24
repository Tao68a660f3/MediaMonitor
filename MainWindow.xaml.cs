using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private readonly SmtcService _smtc = new SmtcService();
        private readonly LyricService _lyric = new LyricService();
        private readonly SerialService _serial = new SerialService();
        private DispatcherTimer _uiTimer;
        private Dictionary<int, string> _lastRowId = new Dictionary<int, string>();
        private int _tickCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateStep();

            // 监听设置项改变，立即重置增量标记
            ChkAdvancedMode.Click += (s, e) => _lastRowId.Clear();
            ChkIncremental.Click += (s, e) => _lastRowId.Clear();
            ChkTransOccupies.Click += (s, e) => _lastRowId.Clear();

            Init();
        }

        private async void Init()
        {
            // 1. 初始化串口列表
            ComboPorts.ItemsSource = _serial.GetPortNames();

            // 2. 初始化媒体源 (SMTC)
            await _smtc.InitializeAsync();

            // 当系统中有新的播放器启动或关闭时，自动刷新列表
            _smtc.SessionsListChanged += () => Dispatcher.Invoke(RefreshMediaSessions);

            _smtc.OnMediaUpdated = (p) => Dispatcher.Invoke(() => {
                TxtTitle.Text = p.Title;
                TxtArtist.Text = p.Artist;
                TxtAlbum.Text = p.AlbumTitle;

                // 加载歌词
                _lyric.LoadAndParse(p.Title, p.Artist);
                _lastRowId.Clear(); // 切换歌曲必须重置增量

                // 发送 0x10 元数据包
                if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                {
                    var d = _serial.BuildMetadata(p.Title, p.Artist, p.AlbumTitle);
                    DoSendAndLog(d, true);
                }
                TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "歌词: " + System.IO.Path.GetFileName(_lyric.CurrentLyricPath) : "未找到歌词";
            });

            RefreshMediaSessions();
            LoadAppSettings(); // 加载保存的设置
            _uiTimer.Start();
        }

        // --- 媒体源管理 ---
        private void RefreshMediaSessions()
        {
            var sessions = _smtc.GetSessions();
            ComboSessions.ItemsSource = sessions;
            if (sessions.Count > 0 && ComboSessions.SelectedIndex == -1)
                ComboSessions.SelectedIndex = 0;
        }

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession);
            _lastRowId.Clear();
        }

        // --- 设置保存与加载 ---
        private void LoadAppSettings()
        {
            var cfg = ConfigService.Load();
            ComboPorts.Text = cfg.PortName;
            foreach (ComboBoxItem item in ComboBaud.Items)
                if (item.Content.ToString() == cfg.BaudRate) item.IsSelected = true;

            ComboEncoding.SelectedIndex = cfg.EncodingIndex;
            TxtLrcPath.Text = cfg.LyricPath;
            TxtPatterns.Text = cfg.Patterns;
            TxtScreenLines.Text = cfg.ScreenLines.ToString();
            TxtOffset.Text = cfg.Offset.ToString();
            ChkAdvancedMode.IsChecked = cfg.AdvancedMode;
            ChkIncremental.IsChecked = cfg.Incremental;
            ChkTransOccupies.IsChecked = cfg.TransOccupies;

            _lyric.LyricFolder = cfg.LyricPath;
            _lyric.FileNamePatterns = cfg.Patterns.Split(';');
        }

        private void SaveAppSettings()
        {
            ConfigService.Save(new AppConfig
            {
                PortName = ComboPorts.Text,
                BaudRate = ((ComboBoxItem)ComboBaud.SelectedItem)?.Content.ToString() ?? "115200",
                EncodingIndex = ComboEncoding.SelectedIndex,
                LyricPath = TxtLrcPath.Text,
                Patterns = TxtPatterns.Text,
                ScreenLines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3,
                Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1,
                AdvancedMode = ChkAdvancedMode.IsChecked ?? true,
                Incremental = ChkIncremental.IsChecked ?? true,
                TransOccupies = ChkTransOccupies.IsChecked ?? true
            });
            // 同时更新当前歌词服务的配置
            _lyric.LyricFolder = TxtLrcPath.Text;
            _lyric.FileNamePatterns = TxtPatterns.Text.Split(';');
        }

        // 窗口关闭前自动保存设置
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveAppSettings();
            base.OnClosing(e);
        }

        // --- 核心逻辑循环 ---
        private void UpdateStep()
        {
            var p = _smtc.GetCurrentProgress();
            if (p == null) return;
            PbProgress.Maximum = p.TotalSeconds;
            PbProgress.Value = p.CurrentSeconds;
            TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";
            TimeSpan cur = TimeSpan.FromSeconds(p.CurrentSeconds);

            // 1. 界面大字预览
            int idx = _lyric.Lines.FindLastIndex(l => l.Time <= cur);
            if (idx != -1) TxtLyricDisplay.Text = _lyric.Lines[idx].Content + "\n" + _lyric.Lines[idx].Translation;

            // 2. 串口协议逻辑
            ProcessLogic(cur);

            // 3. 500ms 同步包 (不进预览区)
            _tickCount = (_tickCount + 1) % 10;
            if (_tickCount == 0 && _serial.IsOpen && ChkAdvancedMode.IsChecked == true)
            {
                var syncData = _serial.BuildSync(p.Status.Contains("Playing"), (uint)cur.TotalMilliseconds, (uint)(p.TotalSeconds * 1000));
                _serial.SendRaw(syncData);
            }
        }

        private void ProcessLogic(TimeSpan cur)
        {
            int lines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
            int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;
            bool isAdv = ChkAdvancedMode.IsChecked == true;

            var view = new List<(LyricLine line, string text, bool isTrans)>();
            int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= cur);

            if (cIdx != -1)
            {
                for (int i = Math.Max(0, cIdx - offset); i < _lyric.Lines.Count && view.Count < lines; i++)
                {
                    var l = _lyric.Lines[i];
                    view.Add((l, l.Content, false));
                    if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(l.Translation) && view.Count < lines)
                        view.Add((l, l.Translation, true));
                }
            }

            for (int i = 0; i < lines; i++)
            {
                string content = i < view.Count ? view[i].text : "";
                string rowId = $"{i}_{content}";

                // 增量逻辑判断
                if (!_lastRowId.ContainsKey(i) || _lastRowId[i] != rowId)
                {
                    _lastRowId[i] = rowId;
                    if (string.IsNullOrEmpty(content)) continue;

                    if (isAdv)
                    {
                        var v = view[i]; byte[] d;
                        if (v.line.Words.Count > 0 && !v.isTrans)
                            d = _serial.BuildWordByWord((ushort)i, v.line.Words, v.line.Time);
                        else
                            d = _serial.BuildLyricWithIndex((byte)(v.isTrans ? 0x13 : 0x12), (ushort)i, content);
                        DoSendAndLog(d, true);
                    }
                    else
                    {
                        // 普通模式直接发原始编码字节
                        DoSendAndLog(_serial.GetEncodedBytes(content), true);
                    }
                }
            }
        }

        private void DoSendAndLog(byte[] data, bool log)
        {
            if (_serial.IsOpen) _serial.SendRaw(data);
            if (log) Dispatcher.Invoke(() => LogPacket(data));
        }

        // --- 物理层解析监控器 ---
        private void LogPacket(byte[] data)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            Encoding enc = (ComboEncoding.SelectedIndex == 0) ? Encoding.UTF8 : Encoding.GetEncoding("GB2312");

            if (ChkAdvancedMode.IsChecked == true && data.Length >= 4 && data[0] == 0xAA)
            {
                // 1. 十六进制分色高亮
                p.Inlines.Add(new Run(data[0].ToString("X2")) { Foreground = Brushes.Gray }); // Header
                p.Inlines.Add(new Run(" " + data[1].ToString("X2")) { Foreground = Brushes.Gold, FontWeight = FontWeights.Bold }); // Type
                p.Inlines.Add(new Run(" " + data[2].ToString("X2")) { Foreground = Brushes.White }); // Len

                byte[] pay = data.Skip(3).Take(data[2]).ToArray();
                p.Inlines.Add(new Run(" " + BitConverter.ToString(pay).Replace("-", " ")) { Foreground = Brushes.Cyan }); // Payload
                p.Inlines.Add(new Run(" " + data.Last().ToString("X2")) { Foreground = Brushes.IndianRed }); // Checksum
                p.Inlines.Add(new Run("\n"));

                // 2. 协议字段还原
                string info = $"指令: 0x{data[1]:X2} | ";
                if (data[1] == 0x10)
                {
                    var s = enc.GetString(pay).Split('\0');
                    info += $"[元数据] 标题:{s.ElementAtOrDefault(0)} 歌手:{s.ElementAtOrDefault(1)} 专辑:{s.ElementAtOrDefault(2)}";
                }
                else if (data[1] == 0x14)
                {
                    ushort r = BitConverter.ToUInt16(pay, 0); int count = pay[2];
                    info += $"[逐字] 行:{r} 词数:{count} | 文本:{enc.GetString(pay).Substring(3, Math.Min(5, pay.Length - 3))}...";
                }
                else
                {
                    ushort r = BitConverter.ToUInt16(pay, 0);
                    string t = enc.GetString(pay, 2, pay.Length - 2);
                    info += $"[{(data[1] == 0x12 ? "正文" : "翻译")}] 行:{r} 内容: {t}";
                }
                p.Inlines.Add(new Run(info) { Foreground = Brushes.LightGreen });
            }
            else
            {
                // 普通模式解析
                p.Inlines.Add(new Run(BitConverter.ToString(data).Replace("-", " ")) { Foreground = Brushes.Cyan });
                p.Inlines.Add(new Run($"\n[RAW文本] {enc.GetString(data)}") { Foreground = Brushes.White });
            }

            HexPreview.Document.Blocks.Add(p);
            if (HexPreview.Document.Blocks.Count > 50) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
            HexPreview.ScrollToEnd();
        }

        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
                    _serial.SelectedEncoding = (EncodingType)ComboEncoding.SelectedIndex;
                    string baud = ((ComboBoxItem)ComboBaud.SelectedItem).Content.ToString();
                    _serial.Connect(ComboPorts.Text, int.Parse(baud));
                    BtnSerialConn.Content = "断开串口";
                    BtnSerialConn.Background = Brushes.IndianRed;
                    SaveAppSettings(); // 连接成功时顺便保存一下设置
                }
                else
                {
                    _serial.Disconnect();
                    BtnSerialConn.Content = "连接串口";
                    BtnSerialConn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                }
            }
            catch (Exception ex) { MessageBox.Show("串口操作失败: " + ex.Message); }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择目录" };
            if (d.ShowDialog() == true) TxtLrcPath.Text = System.IO.Path.GetDirectoryName(d.FileName);
        }
    }
}