using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaMonitor.Services;
using MediaMonitor.Core;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _uiTimer;
        private bool _isInternalChange = false;
        private bool _isRealExit = false;

        public MainWindow()
        {
            InitializeComponent();

            // 1. 初始化 UI 定时器，用于刷新界面显示
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += UIUpdate_Tick;
            _uiTimer.Start();

            // 2. 加载当前配置到控件
            LoadConfigToUI();

            if (App.Smtc != null)
            {
                App.Smtc.SessionsListChanged += RefreshSessionList;
                RefreshSessionList(); // 立即加载一次
            }
            RefreshSerialPorts();
        }

        private void RefreshSerialPorts()
        {
            try
            {
                var ports = System.IO.Ports.SerialPort.GetPortNames();
                ComboPorts.ItemsSource = ports;
                if (ports.Length > 0)
                {
                    // 尝试选中配置文件中保存的串口号
                    var savedPort = App.ConfigSvc?.Current?.SerialPortName;
                    if (!string.IsNullOrEmpty(savedPort) && ports.Contains(savedPort))
                        ComboPorts.SelectedItem = savedPort;
                    else
                        ComboPorts.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"获取串口列表失败: {ex.Message}");
            }
        }

        private void RefreshSessionList()
        {
            // 1. 获取数据可以在后台做，这没问题
            if (App.Smtc == null)
                return;

            var sessions = App.Smtc.GetSessions();

            // 2. 修改 UI 必须“翻墙”回到主线程
            Application.Current.Dispatcher.Invoke(() =>
            {
                ComboSessions.ItemsSource = sessions;

                if (sessions.Count > 0 && ComboSessions.SelectedIndex == -1)
                    ComboSessions.SelectedIndex = 0;
            });
        }

        private void LoadConfigToUI()
        {
            _isInternalChange = true;
            var cfg = App.ConfigSvc.Current;

            // 传输层配置
            RbSerial.IsChecked = cfg.TransportMode == TransportType.Serial;
            RbUdp.IsChecked = cfg.TransportMode == TransportType.UDP;
            TxtRemoteIp.Text = cfg.RemoteIp;
            TxtRemotePort.Text = cfg.RemotePort.ToString();

            // 串口逻辑
            ComboBaud.Text = cfg.BaudRate.ToString();

            // 协议与路径
            TxtLrcPath.Text = cfg.LyricFolder;
            ChkAdvancedMode.IsChecked = cfg.IsAdvancedMode;
            ChkIncremental.IsChecked = cfg.IsIncremental;

            // 参数列表
            TxtLineLimit.Text = cfg.LineLimit.ToString();
            TxtOffset.Text = cfg.Offset.ToString();
            TxtUpdateRate.Text = cfg.UpdateIntervalMs.ToString();
            TxtSyncInterval.Text = cfg.SyncIntervalMs.ToString();

            _isInternalChange = false;
            TransMode_Changed(null, null); // 触发一次显隐逻辑
        }

        private void UIUpdate_Tick(object sender, EventArgs e)
        {
            if (App.Smtc == null || App.Lyrics == null)
                return;

            // 更新播放信息
            TxtTitle.Text = App.Smtc.CurrentTitle ?? "未在播放";
            TxtArtist.Text = App.Smtc.CurrentArtist ?? "未知艺术家";
            TxtAlbum.Text = App.Smtc.CurrentAlbum ?? "未知album";

            // 更新进度条
            var prog = App.Smtc.GetCurrentProgress();
            if (prog != null)
            {
                PbProgress.Maximum = prog.TotalSeconds;
                PbProgress.Value = prog.CurrentSeconds;
                TxtTime.Text = $"{TimeSpan.FromSeconds(prog.CurrentSeconds):mm\\:ss} / {TimeSpan.FromSeconds(prog.TotalSeconds):mm\\:ss}";
            }

            // 更新歌词状态
            int lrcCount = App.Lyrics.Lines?.Count ?? 0;
            TxtLrcStatus.Text = lrcCount > 0 ? $"已加载 {lrcCount} 行" : "未找到歌词";
        }

        private void TransMode_Changed(object sender, RoutedEventArgs e)
        {
            if (GridSerialConfig == null || GridUdpConfig == null)
                return;

            bool isSerial = RbSerial.IsChecked ?? true;
            GridSerialConfig.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
            GridUdpConfig.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = App.ConfigSvc.Current;
                cfg.TransportMode = RbSerial.IsChecked == true ? TransportType.Serial : TransportType.UDP;
                cfg.RemoteIp = TxtRemoteIp.Text;
                if (int.TryParse(TxtRemotePort.Text, out int port))
                    cfg.RemotePort = port;
                if (int.TryParse(TxtLineLimit.Text, out int lines))
                    cfg.LineLimit = lines;
                if (int.TryParse(TxtOffset.Text, out int offset))
                    cfg.Offset = offset;
                if (int.TryParse(TxtUpdateRate.Text, out int updateMs))
                    cfg.UpdateIntervalMs = updateMs;
                if (int.TryParse(TxtSyncInterval.Text, out int syncMs))
                    cfg.SyncIntervalMs = syncMs;
                cfg.LyricFolder = TxtLrcPath.Text;
                cfg.IsAdvancedMode = ChkAdvancedMode.IsChecked ?? true;
                cfg.IsIncremental = ChkIncremental.IsChecked ?? true;
                cfg.TransOccupies = ChkTransOccupies.IsChecked ?? true;
                // 编码
                cfg.EncodingName = (ComboEncoding.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "utf-8";
                // 串口参数
                cfg.SerialPortName = ComboPorts.SelectedItem?.ToString() ?? "COM3";
                if (int.TryParse(ComboBaud.Text, out int baud))
                    cfg.BaudRate = baud;

                // 创建传输层
                IMediaTransport transport;
                if (cfg.TransportMode == TransportType.Serial)
                {
                    var serial = new SerialService();
                    serial.Connect(cfg.SerialPortName, cfg.BaudRate);
                    transport = serial;
                }
                else
                {
                    var udp = new UdpService { RemoteIp = cfg.RemoteIp, RemotePort = cfg.RemotePort, LocalPort = cfg.LocalPort };
                    udp.Connect();
                    transport = udp;
                }

                App.Master?.UpdateTransport(transport);
                App.Master?.ReconnectTransport();

                App.ConfigSvc.Save();

                BtnConnect.Content = "传输服务运行中";
                BtnConnect.IsEnabled = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败: {ex.Message}");
            }
        }

        // 1. 处理媒体源切换
        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (App.Smtc == null)
                return;

            // 从 ComboBox 中获取选中的会话并交给 SmtcService
            var session = ComboSessions.SelectedItem as Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
            App.Smtc.SelectSession(session);
        }

        // 2. 处理文本框内容改变（XAML 中引用的通用事件）
        private void NumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 如果你现在不需要在输入时实时保存，可以先留空
            // 这样红线会立即消失
        }

        // 简单的数字输入限制
        private void OnlyNumber_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !char.IsDigit(e.Text, 0) && e.Text != "-";
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        { /* 这里可以后续加 FolderBrowserDialog */
        }
        private void BtnSyncTime_Click(object sender, RoutedEventArgs e)
        {
            App.Master?.SendTimeSync();
        } //
    }
}