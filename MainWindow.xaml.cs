using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
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

        private Dictionary<int, string> _lastSentContent = new Dictionary<int, string>();
        private int _tickCount = 0;
        private const int MaxLogLines = 50;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateUIProgress();

            InitApp();
            LoadSettings(); // 启动时加载配置

            // 窗口关闭前保存配置
            this.Closing += (s, e) => SaveSettings();
        }

        private void InitApp()
        {
            ComboPorts.ItemsSource = _serial.GetPortNames();
            this.Loaded += async (s, e) => {
                await _smtc.InitializeAsync();
                _smtc.SessionsListChanged += async () => {
                    var sessions = _smtc.GetSessions();
                    await Dispatcher.InvokeAsync(() => {
                        ComboSessions.ItemsSource = sessions;
                        if (sessions.Count > 0 && ComboSessions.SelectedIndex == -1) ComboSessions.SelectedIndex = 0;
                    });
                };
                var initSessions = _smtc.GetSessions();
                ComboSessions.ItemsSource = initSessions;
                if (initSessions.Count > 0) ComboSessions.SelectedIndex = 0;
            };
        }

        #region 配置保存与加载
        private void LoadSettings()
        {
            var cfg = ConfigService.Load();
            TxtLrcPath.Text = cfg.LyricPath;
            TxtPatterns.Text = cfg.Patterns;
            TxtScreenLines.Text = cfg.ScreenLines.ToString();
            TxtOffset.Text = cfg.Offset.ToString();
            ChkAdvancedMode.IsChecked = cfg.AdvancedMode;
            ChkIncremental.IsChecked = cfg.Incremental;
            ChkTransOccupies.IsChecked = cfg.TransOccupies;
            ComboEncoding.SelectedIndex = cfg.EncodingIndex;

            // 尝试匹配保存的串口和波特率
            if (!string.IsNullOrEmpty(cfg.PortName)) ComboPorts.Text = cfg.PortName;
            foreach (ComboBoxItem item in ComboBaud.Items)
            {
                if (item.Content.ToString() == cfg.BaudRate) { item.IsSelected = true; break; }
            }
        }

        private void SaveSettings()
        {
            var cfg = new AppConfig
            {
                PortName = ComboPorts.Text,
                BaudRate = (ComboBaud.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "115200",
                EncodingIndex = ComboEncoding.SelectedIndex,
                LyricPath = TxtLrcPath.Text,
                Patterns = TxtPatterns.Text,
                ScreenLines = int.TryParse(TxtScreenLines.Text, out int sl) ? sl : 3,
                Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 1,
                AdvancedMode = ChkAdvancedMode.IsChecked ?? true,
                Incremental = ChkIncremental.IsChecked ?? true,
                TransOccupies = ChkTransOccupies.IsChecked ?? true
            };
            ConfigService.Save(cfg);
        }
        #endregion

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ComboSessions.SelectedItem as GlobalSystemMediaTransportControlsSession;
            if (selected == null) return;
            _smtc.SelectSession(selected);
            _smtc.OnMediaUpdated = (props) => {
                Dispatcher.BeginInvoke(new Action(() => {
                    TxtTitle.Text = props.Title;
                    TxtArtist.Text = props.Artist;
                    TxtAlbum.Text = props.AlbumTitle;

                    // 应用界面上的歌词路径和模式
                    _lyric.LyricFolder = TxtLrcPath.Text;
                    _lyric.FileNamePatterns = TxtPatterns.Text.Split(';');
                    _lyric.LoadAndParse(props.Title, props.Artist);
                    UpdateLrcUIStatus();

                    _lastSentContent.Clear(); // 切歌重刷
                    if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                    {
                        var p = _serial.BuildMetadata(props.Title, props.Artist);
                        AddLogPreview(p, "切歌", props.Title);
                        _serial.SendRaw(p);
                    }
                }));
            };
            _uiTimer.Start();
        }

        private void UpdateUIProgress()
        {
            var info = _smtc.GetCurrentProgress();
            if (info == null) return;

            PrgBar.Maximum = info.TotalSeconds;
            PrgBar.Value = info.CurrentSeconds;
            TxtTime.Text = $"{info.CurrentStr} / {info.TotalStr}";
            TxtStatus.Text = $"状态: {info.Status}";

            TimeSpan curPos = TimeSpan.FromSeconds(info.CurrentSeconds);
            _lyric.UpdateCurrentStatus(curPos);

            // 更新预览 UI
            if (_lyric.CurrentLine != null)
            {
                TxtLyricDisplay.Text = _lyric.CurrentLine.Content;
                TxtLyricTranslation.Text = _lyric.CurrentLine.Translation;
                TxtNextLyric.Text = _lyric.NextLine != null ? "下句: " + _lyric.NextLine.Content : "";
            }

            // 1. 进度发送 (500ms)
            _tickCount++;
            if (_tickCount >= 10)
            {
                if (_serial.IsOpen && ChkAdvancedMode.IsChecked == true)
                {
                    var p = _serial.BuildProgress(info.Status.Contains("Playing"), (uint)curPos.TotalMilliseconds, (uint)(info.TotalSeconds * 1000));
                    _serial.SendRaw(p);
                }
                _tickCount = 0;
            }

            // 2. 核心窗口逻辑
            ProcessLyricWindow(curPos);
        }

        private void ProcessLyricWindow(TimeSpan curPos)
        {
            if (!int.TryParse(TxtScreenLines.Text, out int screenLines)) return;
            if (!int.TryParse(TxtOffset.Text, out int centerOffset)) return;

            var physicalRows = new List<(ushort idx, string text, byte type, LyricLine source)>();
            if (_lyric.Lines.Count > 0 && _lyric.CurrentLine != null)
            {
                int curIdx = _lyric.Lines.IndexOf(_lyric.CurrentLine);
                int scanIdx = curIdx, linesBack = 0;
                while (scanIdx > 0 && linesBack < centerOffset)
                {
                    scanIdx--; linesBack++;
                    if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(_lyric.Lines[scanIdx].Translation)) linesBack++;
                }
                for (int i = scanIdx; i < _lyric.Lines.Count && physicalRows.Count < screenLines; i++)
                {
                    var line = _lyric.Lines[i];
                    physicalRows.Add(((ushort)i, line.Content, (byte)0x12, line));
                    if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation) && physicalRows.Count < screenLines)
                        physicalRows.Add(((ushort)i, line.Translation, (byte)0x13, line));
                }
            }

            for (int i = 0; i < screenLines; i++)
            {
                string contentHash = "EMPTY";
                byte[] dataToSend = null;
                string label = $"Row {i}";
                string rawText = "---";

                if (i < physicalRows.Count)
                {
                    var row = physicalRows[i];
                    rawText = row.text;
                    contentHash = $"{row.idx}_{row.type}_{row.text}";

                    if (ChkAdvancedMode.IsChecked == true)
                    {
                        if (row.source == _lyric.CurrentLine && row.type == 0x12 && row.source.Words.Count > 0)
                            dataToSend = _serial.BuildWordByWord(row.idx, row.source.Words, row.source.Time);
                        else
                            dataToSend = _serial.BuildLyricWithIndex(row.type, row.idx, row.text);
                    }
                    else
                    {
                        dataToSend = SelectedEncodingBytes(row.text + "\n");
                        label = "简单模式";
                    }
                }

                bool isChanged = !_lastSentContent.ContainsKey(i) || _lastSentContent[i] != contentHash;
                if (isChanged || ChkIncremental.IsChecked == false)
                {
                    if (dataToSend != null)
                    {
                        AddLogPreview(dataToSend, label, rawText);
                        if (_serial.IsOpen) _serial.SendRaw(dataToSend);
                    }
                    _lastSentContent[i] = contentHash;
                }
            }
        }

        private byte[] SelectedEncodingBytes(string t)
        {
            var enc = ComboEncoding.SelectedIndex == 1 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;
            return enc.GetBytes(t);
        }

        private void AddLogPreview(byte[] data, string label, string raw)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (HexPreview.Document.Blocks.Count > MaxLogLines)
                    HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);

                string hex = BitConverter.ToString(data).Replace("-", " ");
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                p.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss.f}] [{label}] ") { Foreground = Brushes.DarkCyan });
                p.Inlines.Add(new Run(raw + "\n") { Foreground = Brushes.White });

                if (ChkAdvancedMode.IsChecked == true && data.Length > 2 && data[0] == 0xAA)
                {
                    p.Inlines.Add(new Run(hex.Substring(0, 2)) { Foreground = Brushes.Red });
                    if (hex.Length > 3) p.Inlines.Add(new Run(" " + hex.Substring(3, 2)) { Foreground = Brushes.Yellow });
                    if (hex.Length > 6) p.Inlines.Add(new Run(" " + hex.Substring(6, 2)) { Foreground = Brushes.Cyan });
                    if (hex.Length > 9) p.Inlines.Add(new Run(" " + hex.Substring(9, hex.Length - 12)) { Foreground = Brushes.White });
                    p.Inlines.Add(new Run(" " + hex.Substring(hex.Length - 2)) { Foreground = Brushes.MediumPurple });
                }
                else
                {
                    p.Inlines.Add(new Run(hex) { Foreground = Brushes.LightGray });
                }
                HexPreview.Document.Blocks.Add(p);
                HexPreview.ScrollToEnd();
            }));
        }

        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
                    _serial.SelectedEncoding = ComboEncoding.SelectedIndex == 1 ? EncodingType.GB2312 : EncodingType.UTF8;
                    _serial.Connect(ComboPorts.Text, int.Parse(((ComboBoxItem)ComboBaud.SelectedItem).Content.ToString()));
                    BtnSerialConn.Content = "断开串口";
                }
                else { _serial.Disconnect(); BtnSerialConn.Content = "连接串口"; }
            }
            catch (Exception ex) { MessageBox.Show("串口失败: " + ex.Message); }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择文件夹 (直接点击确定即可)" };
            if (dialog.ShowDialog() == true)
            {
                TxtLrcPath.Text = Path.GetDirectoryName(dialog.FileName);
            }
        }

        private void UpdateLrcUIStatus()
        {
            if (_lyric.CurrentLyricPath != null)
            {
                TxtLrcStatus.Text = "找到: " + Path.GetFileName(_lyric.CurrentLyricPath);
                TxtLrcStatus.Foreground = Brushes.DarkGreen;
            }
            else
            {
                TxtLrcStatus.Text = "未找到歌词文件";
                TxtLrcStatus.Foreground = Brushes.Red;
            }
        }
    }
}