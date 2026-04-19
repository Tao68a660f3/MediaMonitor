using MediaMonitor.Core;
using MediaMonitor.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

            // 1. 初始化 UI 定时器（保持不变，用于刷新进度条等）
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += UIUpdate_Tick;
            _uiTimer.Start();

            // 2. 加载当前配置
            LoadConfigToUI();

            // 3. 对接 SMTC 逻辑
            if (App.Smtc != null)
            {
                // 记得我们刚才给 RefreshSessionList 加了 Dispatcher.Invoke 吗？
                App.Smtc.SessionsListChanged += RefreshSessionList;
                RefreshSessionList();
            }

            // 订阅歌词变化信号
            if (App.Master != null)
            {
                App.Master.LyricChanged += OnMasterLyricChanged;
            }

            // 在 MainWindow 构造函数或初始化位置
            App.TransportMgr.OnTransportError += (msg) =>
            {
                // 必须回到 UI 线程执行
                Dispatcher.Invoke(() =>
                {
                    // 1. 如果当前是连接状态，但底层报错导致断开了，就刷新按钮
                    if (!App.TransportMgr.IsConnected)
                    {
                        UpdateConnectButtonState(false);

                        // 2. 可以在状态栏提示一下，而不是弹窗（弹窗太吵了）
                        // TxtStatus.Text = $"连接异常中断: {msg}";
                    }
                });
            };

            // 4. 执行初始化“点火”：根据配置决定是串口还是 UDP
            bool isSerialMode = RbSerial.IsChecked ?? true;
            SwitchTransportMode(isSerialMode);
        }

        private void TransMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || _isInternalChange)
                return;

            bool isSerial = RbSerial.IsChecked ?? true;
            GridSerialConfig.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
            GridUdpConfig.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;

            // 【新增】只有当界面已经加载完成，且不是 LoadConfig 触发时才执行逻辑切换
            if (this.IsLoaded && !_isInternalChange)
            {
                SwitchTransportMode(isSerial);
                SyncAndSaveConfig(); // 立即保存模式选择
            }
        }

        private void SwitchTransportMode(bool isSerial)
        {
            if (isSerial)
            {
                var serial = new SerialService();

                // 绑定事件时，直接指向你的 RefreshSerialPorts 方法
                serial.OnPortListChanged += RefreshSerialPorts;

                // 装载进管家
                App.TransportMgr.SetTransport(serial);

                // 初始化刷新：既然刚才在 Service 里加了 GetPortNames，这里就能用了
                RefreshSerialPorts(serial.GetPortNames());
            }
            else
            {
                App.TransportMgr.SetTransport(new UdpService());
            }
        }

        private void RefreshSerialPorts(string[] ports)
        {
            // 因为这个方法会被后台事件调用，必须确保在 UI 线程执行
            Dispatcher.Invoke(() =>
            {
                try
                {
                    ComboPorts.ItemsSource = ports;
                    if (ports.Length > 0)
                    {
                        // 只有在没选中的时候才尝试自动选择
                        if (ComboPorts.SelectedIndex == -1)
                        {
                            var savedPort = App.ConfigSvc?.Current?.SerialPortName;
                            if (!string.IsNullOrEmpty(savedPort) && ports.Contains(savedPort))
                                ComboPorts.SelectedItem = savedPort;
                            else
                                ComboPorts.SelectedIndex = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 在大项目里，建议用状态栏显示错误，而不是弹窗打断用户
                    //StatusTextBlock.Text = $"更新串口列表失败: {ex.Message}";
                }
            });
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
            // 开启静默模式，防止赋值过程触发 TextChanged/Checked 事件导致重复保存
            _isInternalChange = true;

            try
            {
                if (App.ConfigSvc == null)
                    return;
                var cfg = App.ConfigSvc.Current;

                // --- 1. 传输层与 IP ---
                RbSerial.IsChecked = cfg.TransportMode == TransportType.Serial;
                RbUdp.IsChecked = cfg.TransportMode == TransportType.UDP;
                TxtRemoteIp.Text = cfg.RemoteIp;
                TxtRemotePort.Text = cfg.RemotePort.ToString();

                // --- 2. 串口与编码 (使用更稳妥的匹配方式) ---
                // 匹配波特率：直接设置 Text，WPF 会自动匹配对应的 ComboBoxItem
                ComboBaud.Text = cfg.BaudRate.ToString();

                // 匹配编码：遍历下拉项进行不区分大小写的匹配，确保 UI 选中状态正确
                string savedEnc = cfg.EncodingName.ToLower();
                foreach (ComboBoxItem item in ComboEncoding.Items)
                {
                    if (item.Content.ToString().ToLower() == savedEnc)
                    {
                        ComboEncoding.SelectedItem = item;
                        break;
                    }
                }

                // --- 3. 协议与路径 ---
                TxtLrcPath.Text = cfg.LyricFolder;
                ChkAdvancedMode.IsChecked = cfg.IsAdvancedMode;
                ChkIncremental.IsChecked = cfg.IsIncremental;
                ChkTransOccupies.IsChecked = cfg.TransOccupies;

                // --- 4. 参数列表 ---
                TxtLineLimit.Text = cfg.LineLimit.ToString();
                TxtOffset.Text = cfg.Offset.ToString();
                TxtUpdateRate.Text = cfg.UpdateIntervalMs.ToString();
                TxtSyncInterval.Text = cfg.SyncIntervalMs.ToString();

                // --- 5. 核心状态同步 (解决 UDP 模式重新打开时的显示问题) ---
                // 显式强制刷新 Grid 的可见性，而不完全依赖自动触发的事件
                bool isSerial = cfg.TransportMode == TransportType.Serial;
                if (GridSerialConfig != null && GridUdpConfig != null)
                {
                    GridSerialConfig.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
                    GridUdpConfig.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
                }

                // --- 6. 业务点火 ---
                // 立即根据配置模式加载对应的传输驱动（Serial 或 UDP）
                SwitchTransportMode(isSerial);

                // 通知大脑（Master）使用当前加载的这一套配置
                App.Master?.UpdateConfig(cfg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载配置到UI失败: {ex.Message}");
            }
            finally
            {
                // 无论是否报错，最后必须关闭静默模式，否则后续手动操作无效
                _isInternalChange = false;
            }
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
            string lrcPath = App.Lyrics.CurrentLyricPath ?? "";
            TxtLrcStatus.Text = lrcCount > 0 ? $"已加载 {lrcPath}, {lrcCount} 行" : "未找到歌词";
        }

        // MainWindow.xaml.cs 内部

        private void OnMasterLyricChanged(int index, LyricLine line)
        {
            // 因为 PackageMaster 通常在后台线程，更新 UI 必须回到主线程
            Dispatcher.Invoke(() =>
            {
                if (line == null || line.IsEmpty)
                {
                    TxtLyricDisplay.Text = "--- 暂无歌词 ---";
                    return;
                }

                // 1. 获取当前配置（用于判断是否显示翻译）
                var cfg = App.ConfigSvc?.Current;
                bool canShowTranslation = cfg != null && cfg.TransOccupies && !string.IsNullOrEmpty(line.Translation);

                // 2. 拼接显示文本
                // 逻辑：原文 + (如果有翻译且开启了占行则换行加翻译)
                string displayContent = line.Content;
                if (canShowTranslation)
                {
                    displayContent += "\n" + line.Translation;
                }

                // 3. 刷到界面预览框
                TxtLyricDisplay.Text = displayContent;

                // 4. (可选) 如果你想在 UI 上标记当前是第几行，可以顺便用 index 坐点什么
                // Debug.WriteLine($"Current Line Index: {index}");
            });
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            // 1. 如果已经连接，就断开
            if (App.TransportMgr.IsConnected)
            {
                App.TransportMgr.Disconnect();
                UpdateConnectButtonState(false);
                return;
            }

            // 2. 连接前的最后同步（确保波特率、IP 等最新参数已写入配置对象）
            SyncAndSaveConfig();

            // 3. 这里的神秘之处在于：App.TransportMgr 内部的 _activeTransport 
            // 已经在你切换 RadioButton 时被 SetTransport 换成了正确的实例（Serial 或 UDP）
            // 所以我们只需要大喊一声：连接！
            App.TransportMgr.Connect();

            // 4. 检查是否点火成功
            if (App.TransportMgr.IsConnected)
            {
                UpdateConnectButtonState(true);
            }
            else
            {
                // 如果 Connect 内部报错了（比如端口占用了），Mgr 会触发 OnTransportError 事件
                // 这里可以给个简单提示
                MessageBox.Show("连接请求已发出，但引擎未能就绪。请检查硬件状态或 Log。");
            }
        }

        // 辅助方法：美化 UI 状态
        private void UpdateConnectButtonState(bool isConnected)
        {
            BtnConnect.Content = isConnected ? "断开连接" : "开始连接";
            // 连接后禁用模式切换，防止运行中修改导致崩溃
            RbSerial.IsEnabled = !isConnected;
            RbUdp.IsEnabled = !isConnected;
            ComboBaud.IsEnabled = !isConnected;
            // 改变按钮颜色（可选）
            // BtnConnect.Background = isConnected ? Brushes.Tomato : Brushes.LightGreen;
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
        
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择歌词搜索目录",
                InitialDirectory = TxtLrcPath.Text
            };

            if (dialog.ShowDialog() == true)
            {
                TxtLrcPath.Text = dialog.FolderName;
                // 自动触发实时生效逻辑
                SyncAndSaveConfig();
            }
        }

        // 1. 纯数字输入限制 (防止输入字母)
        private void OnlyNumber_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // 允许数字，如果是偏移量则允许负号
            var textBox = sender as TextBox;
            bool isOffset = textBox?.Name == "TxtOffset";
            if (isOffset && e.Text == "-" && !textBox.Text.Contains("-"))
            {
                e.Handled = false;
                return;
            }
            e.Handled = !char.IsDigit(e.Text, 0);
        }
        // 2. 粘贴拦截 (防止用户通过右键粘贴非数字内容)
        private void NumberTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(String)))
            {
                String text = (String)e.DataObject.GetData(typeof(String));
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^-?\\d+$"))
                    e.CancelCommand();
            }
            else
                e.CancelCommand();
        }

        // --- 2. 统一的安检站 (负责把 UI 上的非法值拉回合法线) ---
        private void NumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null || App.ConfigSvc == null)
                return;

            if (int.TryParse(tb.Text, out int val))
            {
                switch (tb.Name)
                {
                    case "TxtLineLimit":
                        tb.Text = Math.Clamp(val, 1, 10).ToString();
                        break;
                    case "TxtOffset":
                        tb.Text = Math.Clamp(val, -10000, 10000).ToString();
                        break;
                    case "TxtUpdateRate":
                        tb.Text = Math.Clamp(val, 20, 1000).ToString();
                        break;
                    case "TxtSyncInterval":
                        tb.Text = Math.Clamp(val, 100, 5000).ToString();
                        break;
                    case "TxtRemotePort":
                        tb.Text = Math.Clamp(val, 1, 65535).ToString();
                        break;
                }
            }
            else
            {
                // 输入彻底乱套时（比如空值），直接从配置加载回正确的值
                LoadConfigToUI();
            }

            // 格式化好文本后，统一由这里触发保存和分发
            SyncAndSaveConfig();
        }

        // --- 3. 唯一的 IP 校验 (因为它不是纯数字，逻辑独立) ---
        private void TxtRemoteIp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (System.Net.IPAddress.TryParse(TxtRemoteIp.Text.Trim(), out var address))
                TxtRemoteIp.Text = address.ToString();
            else
                TxtRemoteIp.Text = "127.0.0.1";

            SyncAndSaveConfig();
        }

        // MainWindow.xaml.cs 补全
        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            // CheckBox 勾选状态改变时，直接同步配置
            SyncAndSaveConfig();
        }

        private void ComboConfig_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncAndSaveConfig();
        }

        // --- 4. 核心同步与分发站 (只负责搬运数据) ---
        private void SyncAndSaveConfig()
        {
            // 安全围栏：如果关键 UI 还没加载完，直接退出，不要报错
            if (!this.IsLoaded || _isInternalChange)
                return;

            var cfg = App.ConfigSvc.Current;

            // --- A. 传输模式与物理配置 ---
            cfg.TransportMode = RbSerial.IsChecked == true ? TransportType.Serial : TransportType.UDP;

            // 串口名：直接取选中项的字符串
            if (ComboPorts.SelectedItem != null)
                cfg.SerialPortName = ComboPorts.SelectedItem.ToString();

            // 波特率：ComboBoxItem 需要转换
            if (ComboBaud.SelectedItem is ComboBoxItem baudItem)
                cfg.BaudRate = int.Parse(baudItem.Content.ToString());

            // UDP 配置
            cfg.RemoteIp = TxtRemoteIp.Text;
            if (int.TryParse(TxtRemotePort.Text, out int port))
                cfg.RemotePort = port;

            // 编码：同步 EncodingName 即可自动触发内部 Encoding 转换
            if (ComboEncoding.SelectedItem is ComboBoxItem encItem)
                cfg.EncodingName = encItem.Content.ToString().ToLower();

            // --- B. 协议与路径 ---
            cfg.LyricFolder = TxtLrcPath.Text;
            cfg.IsAdvancedMode = ChkAdvancedMode.IsChecked ?? true;
            cfg.IsIncremental = ChkIncremental.IsChecked ?? true;
            cfg.TransOccupies = ChkTransOccupies.IsChecked ?? true;

            // --- C. 数值参数 ---
            if (int.TryParse(TxtLineLimit.Text, out int ll))
                cfg.LineLimit = ll;
            if (int.TryParse(TxtOffset.Text, out int off))
                cfg.Offset = off;
            if (int.TryParse(TxtUpdateRate.Text, out int ur))
                cfg.UpdateIntervalMs = ur;
            if (int.TryParse(TxtSyncInterval.Text, out int si))
                cfg.SyncIntervalMs = si;

            // --- D. 持久化与分发 ---
            App.ConfigSvc.Save(); //

            if (App.Lyrics != null)
                App.Lyrics.LyricFolder = cfg.LyricFolder;

            if (App.Master != null)
                App.Master.UpdateConfig(cfg); // 触发业务层热更新
        }

        private void BtnSyncTime_Click(object sender, RoutedEventArgs e)
        {
            App.Master?.SendTimeSync();
        } //
    }
}