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

        // 逻辑池：存储已经发送过的 "Index_Cmd"
        private HashSet<string> _syncedSlots = new HashSet<string>();
        private int _lastProcessedCIdx = -2;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 50ms 刷新一次 UI 和检测
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => {
                UpdateStep();
                CheckPorts(); // 动态监控串口
            };

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

            // 监听系统媒体变动
            _smtc.SessionsListChanged += () => Dispatcher.Invoke(() => RefreshSessions());

            // 核心：当歌曲切换时，重新加载歌词并排空串口池
            _smtc.OnMediaUpdated += (props) => Dispatcher.Invoke(() => {
                TxtTitle.Text = props.Title;
                TxtArtist.Text = props.Artist;

                // 确保加载歌词
                _lyric.LoadAndParse(props.Title, props.Artist);
                TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? $"已载入: {System.IO.Path.GetFileName(_lyric.CurrentLyricPath)}" : "未找到歌词文件";

                Invalidate(); // 切歌必须清空同步池

                if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                    SendAndLog(_serial.BuildMetadata(props.Title, props.Artist, props.AlbumTitle));
            });

            RefreshSessions();
            _uiTimer.Start();
        }

        // 动态串口监控
        private void CheckPorts()
        {
            var currentPorts = _serial.GetPortNames();
            if (!currentPorts.SequenceEqual(_lastPorts))
            {
                _lastPorts = currentPorts;
                string currentSel = ComboPorts.Text;
                ComboPorts.ItemsSource = currentPorts;
                if (currentPorts.Contains(currentSel)) ComboPorts.Text = currentSel;
            }
        }

        private void UpdateStep()
        {
            try
            {
                var p = _smtc.GetCurrentProgress();
                if (p == null) { ResetUI(); return; }

                // 更新进度条
                PbProgress.Maximum = p.TotalSeconds;
                PbProgress.Value = p.CurrentSeconds;
                TxtTime.Text = $"{p.CurrentStr} / {p.TotalStr}";

                // 计算当前播放到哪一行
                TimeSpan curTime = TimeSpan.FromSeconds(p.CurrentSeconds);
                int cIdx = _lyric.Lines.FindLastIndex(l => l.Time <= curTime);

                // 更新 UI 歌词显示
                if (cIdx != -1)
                {
                    var l = _lyric.GetLine(cIdx);
                    TxtLyricDisplay.Text = l.Content + (string.IsNullOrEmpty(l.Translation) ? "" : "\n" + l.Translation);
                }
                else { TxtLyricDisplay.Text = "(等待歌词...)"; }

                if (!_serial.IsOpen) return;

                // 只有当行索引变化时，才尝试更新串口数据
                if (cIdx != _lastProcessedCIdx)
                {
                    _lastProcessedCIdx = cIdx;
                    HandleOutput(cIdx);
                }
            }
            catch { ResetUI(); }
        }

        private void HandleOutput(int cIdx)
        {
            int lineLimit = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3;
            int offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1;

            // 1. 确定目标槽位集合
            var targetSlots = new HashSet<string>();
            int startIdx = cIdx - offset;

            for (int i = 0; i < lineLimit; i++)
            {
                short absIdx = (short)(startIdx + i);
                targetSlots.Add($"{absIdx}_0x12"); // 可能是 0x12 或 0x14
                targetSlots.Add($"{absIdx}_0x13"); // 翻译槽位
            }

            // 2. 差分计算：找出需要发送的
            IEnumerable<string> toSend = (ChkIncremental.IsChecked == true)
                                         ? targetSlots.Except(_syncedSlots)
                                         : targetSlots;

            // 3. 执行发送
            foreach (var slot in toSend)
            {
                var parts = slot.Split('_');
                short absIdx = short.Parse(parts[0]);
                var line = _lyric.GetLine(absIdx);

                if (parts[1] == "0x12")
                {
                    // 自动判断是普通歌词还是逐字歌词
                    byte[] data = (line.Words.Count > 0)
                        ? _serial.BuildWordByWord(absIdx, line.Time, line.Words)
                        : _serial.BuildLyricLine(absIdx, line.Time, line.Content);
                    SendAndLog(data);
                }
                else // 0x13 翻译
                {
                    // 无论是否勾选“占行”，数据都会发。单片机根据指令号自己决定怎么排。
                    SendAndLog(_serial.BuildTranslationLine(absIdx, line.Time, line.Translation));
                }
            }

            // 4. 同步池子
            _syncedSlots = targetSlots;
        }

        private void SendAndLog(byte[] data)
        {
            if (data == null || data.Length < 2) return;

            // 发送数据
            _serial.SendRaw(data);

            // 过滤频繁的同步包，避免日志刷屏
            if (data[1] == 0x11) return;

            Dispatcher.Invoke(() => {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                Encoding viewEnc = (ComboEncoding.SelectedIndex == 1) ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

                // 1. Hex 原始数据 (灰字小号)
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run($" {hex}\n") { Foreground = Brushes.DimGray, FontSize = 10, FontFamily = new FontFamily("Consolas") });

                // 2. 解析逻辑
                byte cmd = data[1];
                Run tag = new Run();
                string detail = "";

                switch (cmd)
                {
                    case 0x10:
                        tag = new Run(" [元数据] ") { Background = Brushes.DarkBlue, Foreground = Brushes.White };
                        detail = DecodeMetadata(data, viewEnc);
                        break;
                    case 0x12:
                        tag = new Run(" [主体行] ") { Background = Brushes.DarkGreen, Foreground = Brushes.White };
                        detail = DecodeStandard(data, viewEnc);
                        break;
                    case 0x13:
                        tag = new Run(" [翻译行] ") { Background = Brushes.DarkSlateBlue, Foreground = Brushes.White };
                        detail = DecodeStandard(data, viewEnc);
                        break;
                    case 0x14:
                        tag = new Run(" [逐字行] ") { Background = Brushes.DarkRed, Foreground = Brushes.White };
                        detail = DecodeWordByWord(data, viewEnc);
                        break;
                    default:
                        tag = new Run($" [未知:0x{cmd:X2}] ") { Background = Brushes.Gray };
                        break;
                }

                p.Inlines.Add(tag);
                p.Inlines.Add(new Run(" " + detail) { Foreground = Brushes.White });

                HexPreview.Document.Blocks.Add(p);
                if (HexPreview.Document.Blocks.Count > 50) HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);
                HexPreview.ScrollToEnd();
            });
        }

        // 0x10 元数据全解包
        private string DecodeMetadata(byte[] data, Encoding enc)
        {
            try
            {
                int ptr = 3;
                // 依次读取 Title, Artist, Album 的 [长度 + 内容]
                List<string> parts = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    if (ptr >= data.Length) break;
                    int len = data[ptr];
                    parts.Add(enc.GetString(data, ptr + 1, len));
                    ptr += (1 + len);
                }
                return string.Join(" | ", parts);
            }
            catch { return "元数据解析失败"; }
        }

        // 0x12/0x13 格式化解析: (Index) [Time] 内容
        private string DecodeStandard(byte[] data, Encoding enc)
        {
            try
            {
                short idx = BitConverter.ToInt16(data, 3);
                uint time = BitConverter.ToUInt32(data, 5);
                string content = enc.GetString(data, 9, data.Length - 10);
                return $"({idx:D3}) [{time}ms] {(string.IsNullOrEmpty(content) ? "<擦除>" : content)}";
            }
            catch { return "解析失败"; }
        }

        // 0x14 逐字格式化解析: (Index) [Time] 词<偏移>...
        private string DecodeWordByWord(byte[] data, Encoding enc)
        {
            try
            {
                short idx = BitConverter.ToInt16(data, 3);
                uint time = BitConverter.ToUInt32(data, 5);
                int wCount = data[9];
                StringBuilder sb = new StringBuilder();
                sb.Append($"({idx:D3}) [{time}ms] ");

                int ptr = 10;
                for (int i = 0; i < wCount; i++)
                {
                    ushort offset = BitConverter.ToUInt16(data, ptr);
                    byte len = data[ptr + 2];
                    string word = enc.GetString(data, ptr + 3, len);
                    sb.Append($"{word}<{offset}ms> ");
                    ptr += (3 + len);
                }
                return sb.ToString();
            }
            catch { return "逐字解析错误"; }
        }

        private void ResetUI()
        {
            if (TxtTitle.Text == "无媒体") return;
            Dispatcher.Invoke(() => {
                TxtTitle.Text = "无媒体";
                TxtArtist.Text = "请开启播放器...";
                TxtLyricDisplay.Text = "";
                PbProgress.Value = 0;
            });
        }

        private void RefreshSessions()
        {
            var sessions = _smtc.GetSessions();
            ComboSessions.ItemsSource = sessions;
            if (ComboSessions.SelectedIndex == -1 && sessions.Any()) ComboSessions.SelectedIndex = 0;
        }

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            _smtc.SelectSession(ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession);

        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
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
            if (d.ShowDialog() == true) TxtLrcPath.Text = System.IO.Path.GetDirectoryName(d.FileName);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveAppSettings();
            base.OnClosing(e);
        }
    }
}