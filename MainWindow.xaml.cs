using System;
using System.Collections.Generic;
using System.IO;
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

        // 核心标记位：记录每个坑位当前显示的内容 ID
        private Dictionary<int, string> _lastRowId = new Dictionary<int, string>();
        private int _tickCount = 0;
        private const int MaxLogLines = 100;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiTimer.Tick += (s, e) => UpdateUIProgress();

            InitApp();
            LoadSettings();

            // 订阅开关点击，立即重置标记实现刷新
            ChkAdvancedMode.Click += (s, e) => ResetMarkers();
            ChkIncremental.Click += (s, e) => ResetMarkers();
            ChkTransOccupies.Click += (s, e) => ResetMarkers();

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

        private void ResetMarkers() => _lastRowId.Clear();

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
                    _lyric.LyricFolder = TxtLrcPath.Text;
                    _lyric.FileNamePatterns = TxtPatterns.Text.Split(';');
                    _lyric.LoadAndParse(props.Title, props.Artist);
                    UpdateLrcUIStatus();
                    ResetMarkers();

                    // 切歌包
                    if (ChkAdvancedMode.IsChecked == true)
                    {
                        var p = _serial.BuildMetadata(props.Title, props.Artist);
                        AddLogPreview(p, "切歌", props.Title, Brushes.Gold);
                        if (_serial.IsOpen) _serial.SendRaw(p);
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
            TimeSpan curPos = TimeSpan.FromSeconds(info.CurrentSeconds);
            _lyric.UpdateCurrentStatus(curPos);
            TxtLyricDisplay.Text = _lyric.CurrentLine?.Content ?? "...";

            _tickCount = (_tickCount + 1) % 10;

            // 进度包 (500ms)
            if (_tickCount == 0 && ChkAdvancedMode.IsChecked == true)
            {
                var p = _serial.BuildProgress(info.Status.Contains("Playing"), (uint)curPos.TotalMilliseconds, (uint)(info.TotalSeconds * 1000));
                // 进度属于持续更新，通常不进预览，如需预览可取消注释
                // AddLogPreview(p, "进度", info.CurrentStr, Brushes.Gray); 
                if (_serial.IsOpen) _serial.SendRaw(p);
            }

            ProcessLyricWindow(curPos);
        }

        private void ProcessLyricWindow(TimeSpan curPos)
        {
            if (!int.TryParse(TxtScreenLines.Text, out int screenLines)) return;
            if (!int.TryParse(TxtOffset.Text, out int centerOffset)) return;

            // 1. 快速定位物理行
            var viewList = new List<(ushort idx, string text, byte type, LyricLine src)>();
            if (_lyric.Lines.Count > 0 && _lyric.CurrentLine != null)
            {
                int curIdx = _lyric.Lines.IndexOf(_lyric.CurrentLine);
                int scanIdx = curIdx, backCount = 0;
                while (scanIdx > 0 && backCount < centerOffset)
                {
                    scanIdx--; backCount++;
                    if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(_lyric.Lines[scanIdx].Translation)) backCount++;
                }
                for (int i = scanIdx; i < _lyric.Lines.Count && viewList.Count < screenLines; i++)
                {
                    var line = _lyric.Lines[i];
                    viewList.Add(((ushort)i, line.Content, 0x12, line));
                    if (ChkTransOccupies.IsChecked == true && !string.IsNullOrEmpty(line.Translation) && viewList.Count < screenLines)
                        viewList.Add(((ushort)i, line.Translation, 0x13, line));
                }
            }

            // 2. 坑位状态判断
            for (int i = 0; i < screenLines; i++)
            {
                string currentId = "EMPTY";
                byte[] data = null;
                string text = "---";

                if (i < viewList.Count)
                {
                    var row = viewList[i];
                    text = row.text;
                    currentId = $"{row.idx}_{row.type}";

                    if (ChkAdvancedMode.IsChecked == true)
                    {
                        data = (row.src == _lyric.CurrentLine && row.type == 0x12 && row.src.Words.Count > 0)
                            ? _serial.BuildWordByWord(row.idx, row.src.Words, row.src.Time)
                            : _serial.BuildLyricWithIndex(row.type, row.idx, row.text);
                    }
                    else
                    {
                        var enc = ComboEncoding.SelectedIndex == 1 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;
                        data = enc.GetBytes(row.text + "\n");
                    }
                }

                // --- 修正后的增量开关逻辑 ---
                // ChkIncremental.IsChecked == true (增量开启): 仅内容变了才动作
                // ChkIncremental.IsChecked == false (增量关闭): 内容变了 或 500ms周期到 就动作
                bool isChanged = !_lastRowId.ContainsKey(i) || _lastRowId[i] != currentId;
                bool shouldAction = isChanged || (ChkIncremental.IsChecked == false && _tickCount == 0 && currentId != "EMPTY");

                if (shouldAction && data != null)
                {
                    // 不管串口开没开，预览必跳（只要内容是真的新）
                    if (isChanged) AddLogPreview(data, $"行{i}", text, Brushes.LightGreen);

                    // 只有串口开了才发数据
                    if (_serial.IsOpen) _serial.SendRaw(data);

                    // 记录标记
                    _lastRowId[i] = currentId;
                }
            }
        }

        private void AddLogPreview(byte[] data, string label, string raw, Brush color)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (HexPreview.Document.Blocks.Count > MaxLogLines)
                    HexPreview.Document.Blocks.Remove(HexPreview.Document.Blocks.FirstBlock);

                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
                // 时间和标签
                p.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss.f}] [{label}] ") { Foreground = Brushes.Cyan });
                // 原始文字
                p.Inlines.Add(new Run(raw + "\n") { Foreground = color, FontWeight = FontWeights.Bold });
                // 十六进制包
                p.Inlines.Add(new Run(BitConverter.ToString(data).Replace("-", " ")) { Foreground = Brushes.Gray, FontSize = 10 });

                HexPreview.Document.Blocks.Add(p);
                HexPreview.ScrollToEnd();
            }));
        }

        #region 基础逻辑与配置
        private void BtnSerialConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serial.IsOpen)
                {
                    _serial.SelectedEncoding = ComboEncoding.SelectedIndex == 1 ? EncodingType.GB2312 : EncodingType.UTF8;
                    _serial.Connect(ComboPorts.Text, int.Parse(((ComboBoxItem)ComboBaud.SelectedItem).Content.ToString()));
                    ResetMarkers();
                    BtnSerialConn.Content = "断开串口";
                }
                else { _serial.Disconnect(); BtnSerialConn.Content = "连接串口"; }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void LoadSettings()
        {
            var cfg = ConfigService.Load();
            TxtLrcPath.Text = cfg.LyricPath; TxtPatterns.Text = cfg.Patterns;
            TxtScreenLines.Text = cfg.ScreenLines.ToString(); TxtOffset.Text = cfg.Offset.ToString();
            ChkAdvancedMode.IsChecked = cfg.AdvancedMode; ChkIncremental.IsChecked = cfg.Incremental;
            ChkTransOccupies.IsChecked = cfg.TransOccupies; ComboEncoding.SelectedIndex = cfg.EncodingIndex;
            if (!string.IsNullOrEmpty(cfg.PortName)) ComboPorts.Text = cfg.PortName;
            foreach (ComboBoxItem item in ComboBaud.Items) if (item.Content.ToString() == cfg.BaudRate) item.IsSelected = true;
        }

        private void SaveSettings()
        {
            ConfigService.Save(new AppConfig
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
            });
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = false, FileName = "选择文件夹" };
            if (dialog.ShowDialog() == true) TxtLrcPath.Text = Path.GetDirectoryName(dialog.FileName);
        }

        private void UpdateLrcUIStatus()
        {
            TxtLrcStatus.Text = _lyric.CurrentLyricPath != null ? "找到: " + Path.GetFileName(_lyric.CurrentLyricPath) : "未找到歌词文件";
            TxtLrcStatus.Foreground = _lyric.CurrentLyricPath != null ? Brushes.LimeGreen : Brushes.OrangeRed;
        }
        #endregion
    }
}