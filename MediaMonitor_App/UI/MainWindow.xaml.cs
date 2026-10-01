using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Services;
using MediaMonitor.Tools;
using MediaMonitor.Tray;
using MediaMonitor.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _uiTimer;
        private bool _isInternalChange = false;

        // 正在按代码设置模式单选（用于屏蔽 RadioButton 因程序赋值而产生的假"切换"事件）
        private bool _applyingMode = false;

        private TrayManager _tray;
        private bool _isRealExit = false;

        // 全链路延迟测试：全部交互逻辑封装在 UI\LatencyTestController.cs，这里只留调用点
        private LatencyTestController? _latencyTest;

        // ★ 协议栈视图（见 UI/ProtocolStackView.cs）：两种模式各一份实现，
        //   主窗口只认"当前视图"，因此连接/断开/测延迟只需要一条代码路径。
        private IProtocolStackView? _newView;
        private IProtocolStackView? _legacyView;

        // 新协议模式的串口列表来源（与 Legacy 的 SerialService 各自独立，互不干扰）
        private SerialService? _newPortScanner;

        // ★★ 封面验证开关 ★★
        // 自动弹窗已关闭（2026-01 起改用新界面上的「Show Artwork」按钮按需查看：
        // 调用 public 方法 ShowArtworkPreview()，见下方）。
        // 置 true 可恢复"每次封面变化自动弹窗"的调试行为。
        private static readonly bool ShowArtworkPreviewOnUpdate = false;
        private ArtworkPreviewWindow? _artPreview;

        public MainWindow()
        {
            InitializeComponent();

            // 1. 初始化托盘
            _tray = new TrayManager();

            // 传入两个动作（Action）：一个是双击显示的逻辑，一个是彻底退出的逻辑
            _tray.Init(
                onShow: () =>
                {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                },
                onExit: () =>
                {
                    _isRealExit = true;
                    System.Windows.Application.Current.Shutdown();
                }
            );

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

                // 封面更新验证（临时）：抓到封面后弹窗看一眼压缩结果，见 OnArtworkUpdated
                App.Smtc.OnThumbnailUpdated += OnArtworkUpdated;
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

            // 初始化 LogService 并绑定 UI 上的 RichTextBox (假设叫 LogBox)
            App.LogSvc = new LogService(this.HexPreview);

            // 延迟测试控制器（按钮交互 / 统计展示 / 日志全部封装在 LatencyTestController 内）
            _latencyTest = new LatencyTestController(
                App.TransportMgr,
                BtnLatencyTest,
                Dispatcher,
                (msg, color) => App.LogSvc?.LogInfo(msg, color));

            // 启动时注入一次配置（静默项以此为准；界面有入口的项在每次保存时再注入）
            _latencyTest.ApplyConfig(App.ConfigSvc?.Current);

            // 顺便把串口/UDP 的报错也接过来
            App.TransportMgr.OnTransportError += (msg) =>
            {
                App.LogSvc.LogInfo($"[传输异常] {msg}", Brushes.OrangeRed);
            };

            // 4. 执行初始化“点火”：根据配置决定是串口还是 UDP
            bool isSerialMode = RbSerial.IsChecked ?? true;
            SwitchTransportMode(isSerialMode);

            // 4.5 协议栈视图 + 新协议面板（模式来自 App.Mode：默认 New，运行期可切）
            InitProtocolStackViews();

            // 5. 恢复上次的窗口位置与大小（位置不合理会自动忽略，见 UI/WindowPlacement.cs）
            ApplyWindowPlacement();
        }

        // 2. 拦截关闭按钮：让它“隐藏”而不是“毁灭”
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 窗口布局落盘：点 X 只是隐藏到托盘，这里必须也保存，否则"从托盘退出"时就没有第二次机会了
            SaveWindowPlacement();

            if (!_isRealExit)
            {
                // 封面验证窗口是 Topmost 的，主窗口隐藏到托盘时必须一起关掉，否则会一直浮在最上层
                _artPreview?.Close();

                // 如果不是点击了托盘里的“退出”，就取消关闭，改为隐藏
                e.Cancel = true;
                this.Hide();

                // 可选：如果你的 TrayManager 里写了弹出气泡的方法，可以在这里调用
                // _tray.ShowBalloon("MediaMonitor", "程序已在后台运行");
            }
            else
            {
                // 只有彻底退出时才释放资源
                _latencyTest?.Dispose();
                _newView?.Shutdown();          // 新协议栈：摘掉 SMTC 事件 + 停掉心跳定时器
                _tray.Dispose();               // 串口扫描器随进程退出即可（SerialService 未实现 IDisposable）
            }
            base.OnClosing(e);
        }

        /// <summary>
        /// 把当前窗口布局写进**当前模式**的配置文件（Legacy→config.json，New→config.new.json）。
        /// 两份配置各存一份窗口布局，切模式后各自恢复。
        /// </summary>
        private void SaveWindowPlacement()
        {
            string? bounds = WindowPlacement.CaptureBounds(this);
            if (bounds == null)
            {
                return;
            }

            if (App.Mode == ProtocolMode.New)
            {
                if (App.NewConfigSvc == null)
                {
                    return;
                }
                App.NewConfigSvc.Current.WindowBounds = bounds;
                App.NewConfigSvc.Save();
            }
            else
            {
                if (App.ConfigSvc == null)
                {
                    return;
                }
                App.ConfigSvc.Current.WindowBounds = bounds;
                App.ConfigSvc.Save();
            }
        }

        /// <summary>按当前模式恢复窗口布局（两份配置各存一份）</summary>
        private void ApplyWindowPlacement()
        {
            string? bounds = (App.Mode == ProtocolMode.New)
                ? App.NewConfigSvc?.Current?.WindowBounds
                : App.ConfigSvc?.Current?.WindowBounds;

            WindowPlacement.Apply(this, bounds);
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
                    // 刷新串口列表失败不打断用户：只留在调试输出里
                    Debug.WriteLine($"[UI] 刷新串口列表失败: {ex.Message}");
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
                //TxtUpdateRate.Text = cfg.UpdateIntervalMs.ToString();
                TxtSyncInterval.Text = cfg.SyncIntervalMs.ToString();
                TxtSyncOffset.Text = cfg.SyncCurrentOffsetMs.ToString();

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

                // --- 7. 新协议面板（独立配置文件：config.new.json）---
                LoadNewConfigToUI();
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

        string FormatTime(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return ts.ToString(@"hh\:mm\:ss");
            else
                return ts.ToString(@"mm\:ss");
        }

        private void UIUpdate_Tick(object sender, EventArgs e)
        {
            if (App.Smtc == null || App.Lyrics == null)
                return;

            // 更新播放信息
            TxtTitle.Text = App.Smtc.CurrentTitle ?? "未在播放";
            TxtArtist.Text = App.Smtc.CurrentArtist ?? "未知艺术家";
            TxtAlbum.Text = App.Smtc.CurrentAlbum ?? "未知唱片集";

            // 更新进度条
            var prog = App.Smtc.GetCurrentProgress();
            if (prog != null)
            {
                PbProgress.Maximum = prog.Duration.TotalSeconds;
                PbProgress.Value = prog.Position.TotalSeconds;
                //TxtTime.Text = $"{prog.Position:mm\\:ss} / {prog.Duration:mm\\:ss}";
                TxtTime.Text = $"{FormatTime(prog.Position)} / {FormatTime(prog.Duration)}";
            }
            else
            {
                // SMTC 丢失（播放器退出/未选中会话）：进度控件是被动刷新的，不主动复位就会停在最后一帧
                PbProgress.Maximum = 1;
                PbProgress.Value = 0;
                TxtTime.Text = "--:-- / --:--";
            }

            // 更新歌词状态：区分"真的加载到 lrc"与"未找到歌词的歌曲信息占位"
            int lrcCount = App.Lyrics.Lines?.Count ?? 0;
            string lrcPath = App.Lyrics.CurrentLyricPath ?? "";
            if (App.Lyrics.IsPlaceholder)
                TxtLrcStatus.Text = "未找到歌词（已用歌曲信息占位）";
            else
                TxtLrcStatus.Text = lrcCount > 0 ? $"已加载 {lrcPath}, {lrcCount} 行" : "未找到歌词";

            // 同步托盘提示：歌曲信息 + 主程序标题（含版本号）；歌曲信息与占位歌词共用一份拼接实现
            string song = App.Smtc.CurrentTitle ?? "未在播放";
            string artist = App.Smtc.CurrentArtist ?? "";
            _tray.UpdateTooltip($"{LyricService.ComposeSongInfo(song, artist)} | {this.Title}");

            // 新协议面板的只读状态（会话 / 对端能力 / 延迟）：100ms 一次，跨线程由这里统一消化
            UpdateNewStatusUI();
        }

        // MainWindow.xaml.cs 内部

        private void OnMasterLyricChanged(int index, LyricLine line)
        {
            // 因为 PackageMaster 通常在后台线程，更新 UI 必须回到主线程
            Dispatcher.Invoke(() =>
            {
                if (line == null || line.IsEmpty)
                {
                    TxtLyricDisplay.Text = "  ";
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

        /// <summary>
        /// 手动显示封面预览（供新界面的「Show Artwork」按钮调用）：
        /// 取当前 SMTC 封面原始字节 → 压缩到 600×600 弹窗显示；无封面时会显示"本曲无封面"。
        /// </summary>
        public void ShowArtworkPreview() => ShowArtworkWindow(App.Smtc?.CurrentThumbnail);

        /// <summary>
        /// 封面变化回调：仅在 <c>ShowArtworkPreviewOnUpdate</c> 为 true 时自动弹窗
        /// （当前默认关闭，改用界面上按需触发 <see cref="ShowArtworkPreview"/>）。
        /// </summary>
        private void OnArtworkUpdated(byte[]? raw)
        {
            if (!ShowArtworkPreviewOnUpdate)
                return;

            ShowArtworkWindow(raw);
        }

        /// <summary>
        /// 弹窗显示封面 —— 自动回调与手动按钮共用的核心实现。
        /// 回调可能来自 SMTC 事件的线程池线程，建窗口必须回到 UI 线程；
        /// </summary>
        private void ShowArtworkWindow(byte[]? raw)
        {
            // 程序正在退出时 Dispatcher 可能已经关停，直接放弃
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.BeginInvoke(() =>
            {
                // 单实例：快速切歌时先关掉上一个，避免窗口堆积
                _artPreview?.Close();

                var win = new ArtworkPreviewWindow(raw) { Owner = this };
                // 用户手动点 X 关掉后把引用置空，否则下一次切歌会去 Close() 一个已关闭的窗口
                win.Closed += (_, __) =>
                {
                    if (ReferenceEquals(_artPreview, win))
                        _artPreview = null;
                };

                _artPreview = win;
                win.Show();   // 非模态：不阻塞后台帧循环，也不抢播放器焦点
            });
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            IProtocolStackView? view = CurrentView;
            if (view == null)
            {
                return;
            }

            // 1. 如果已经连接，就断开
            if (view.IsConnected)
            {
                view.Disconnect();
                UpdateConnectButtonState(false);
                UpdateNewStatusUI();
                return;
            }

            // 2. 连接前的最后同步（确保界面上的参数都已写进"当前模式"的配置对象）
            if (view.Mode == ProtocolMode.New)
            {
                SyncAndSaveNewConfig();
            }
            else
            {
                SyncAndSaveConfig();
            }

            // 3. 交给当前模式的协议栈视图去连（新协议内部是异步的，状态由 UI 定时器刷新）
            view.Connect();
            UpdateConnectButtonState(view.IsConnected, view.SessionActive);
            UpdateNewStatusUI();

            if (view.Mode == ProtocolMode.Legacy && !view.IsConnected)
            {
                MessageBox.Show("连接请求已发出，但引擎未能就绪。请检查硬件状态或 Log。");
            }
        }

        // 辅助方法：美化 UI 状态
        //
        // linkUp        = 链路已建立（TCP 连上 / 串口打开）
        // sessionActive = **对端已应答**（新协议 = SESSION ACTIVE；Legacy 传 null，按链路算）
        //
        // 这个区分很要紧：只开了串口/连上 TCP 但对端根本没应答时，不能显示"已连接"，
        // 按钮也只能是"取消连接"（点击 = 放弃等待），不是"断开连接"。
        private void UpdateConnectButtonState(bool linkUp, bool? sessionActive = null)
        {
            bool active = sessionActive ?? linkUp;

            BtnConnect.Content = active ? "断开连接" : (linkUp ? "取消连接" : "开始连接");
            BtnConnect.Background = active ? Brushes.OrangeRed : (linkUp ? Brushes.DarkOrange : Brushes.SeaGreen);

            // 有链路就锁住模式切换与传输参数，避免"跑着换协议"
            RbSerial.IsEnabled = !linkUp;
            RbUdp.IsEnabled = !linkUp;
            ComboBaud.IsEnabled = !linkUp;

            RbProtoNew.IsEnabled = !linkUp;
            RbProtoLegacy.IsEnabled = !linkUp;
            RbNewCom.IsEnabled = !linkUp;
            RbNewTcp.IsEnabled = !linkUp;
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
            // 如果保存的路径不存在，回退到桌面，避免 OpenFolderDialog 弹报错
            string initDir = TxtLrcPath.Text;
            if (!Directory.Exists(initDir))
                initDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择歌词搜索目录",
                InitialDirectory = initDir
            };

            if (dialog.ShowDialog() == true)
            {
                TxtLrcPath.Text = dialog.FolderName;
                // 自动触发实时生效逻辑
                SyncAndSaveConfig();

                // 切换目录后立即用当前歌曲信息重新载入歌词
                if (App.Lyrics != null && App.Smtc != null)
                {
                    App.Lyrics.LoadAndParse(App.Smtc.CurrentTitle, App.Smtc.CurrentArtist);
                    App.Master?.Invalidate(); // 强制刷新账本，重新输出当前行
                }
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
                        tb.Text = Math.Clamp(val, 1, 20).ToString();
                        break;
                    case "TxtOffset":
                        tb.Text = Math.Clamp(val, -10000, 10000).ToString();
                        break;
                    case "TxtUpdateRate":
                        tb.Text = Math.Clamp(val, 20, 1000).ToString();
                        break;
                    case "TxtSyncInterval":
                        tb.Text = Math.Clamp(val, 100, 30000).ToString();
                        break;
                    case "TxtSyncOffset":
                        tb.Text = Math.Clamp(val, -1000, 1000).ToString();
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
            string raw = TxtRemoteIp.Text.Trim();

            // 0.0.0.0 / :: 是【本机监听地址】，不是合法的发送目标：
            // 真发出去会报 10049「在其上下文中，该请求的地址无效」。
            if (!System.Net.IPAddress.TryParse(raw, out var address) ||
                address.Equals(System.Net.IPAddress.Any) ||
                address.Equals(System.Net.IPAddress.IPv6Any))
            {
                App.LogSvc?.LogInfo(
                    $"[远程IP] 「{raw}」不能作为发送目标（0.0.0.0/:: 是本机监听地址）。" +
                    "对端是同机程序请填 127.0.0.1，对端是 ESP32/另一台机器请填它的局域网 IP。已回退为 127.0.0.1。",
                    Brushes.OrangeRed);
                TxtRemoteIp.Text = "127.0.0.1";
            }
            else
            {
                TxtRemoteIp.Text = address.ToString();
            }

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

        private void Global_TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox tb)
            {
                // 关键点：不管你的 LostFocus 挂载的是哪个函数
                // 只要手动触发 LostFocus 事件，WPF 就会去跑你在 XAML 里写的那个方法
                tb.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

                // 既然数据已经读走了，顺便把键盘焦点撤了，让 UI 看起来更自然
                Keyboard.ClearFocus();

                e.Handled = true;
            }
        }

        // --- 4. 核心同步与分发站 (只负责搬运数据) ---
        private void SyncAndSaveConfig()
        {
            // 安全围栏：如果关键 UI 还没加载完，直接退出，不要报错
            if (!this.IsLoaded || _isInternalChange)
                return;

            // Legacy 专用：新协议模式下界面上的 Legacy 控件是隐藏的，不参与读写
            if (App.Mode != ProtocolMode.Legacy)
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
            {
                cfg.EncodingName = encItem.Content.ToString().ToLower();
                PackageBuilder.UpdateEncoding(cfg.Encoding);
            }


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
            //if (int.TryParse(TxtUpdateRate.Text, out int ur))
            //    cfg.UpdateIntervalMs = ur;
            if (int.TryParse(TxtSyncInterval.Text, out int si))
                cfg.SyncIntervalMs = si;
            if (int.TryParse(TxtSyncOffset.Text, out int so))
                cfg.SyncCurrentOffsetMs = so;

            // --- D. 持久化与分发 ---
            App.ConfigSvc.Save(); //

            // 再次注入：静默项（无 UI 入口）不会在这里被改动，因此仍是"仅启动时生效"
            App.TransportMgr.ApplyConfig(cfg);
            _latencyTest?.ApplyConfig(cfg);

            if (App.Lyrics != null)
                App.Lyrics.LyricFolder = cfg.LyricFolder;

            if (App.Master != null)
                App.Master.UpdateConfig(cfg); // 触发业务层热更新
        }

        private void BtnSyncTime_Click(object sender, RoutedEventArgs e)
        {
            App.Master?.SendTimeSync();
        } //

        // 全链路延迟测试：具体逻辑见 UI\LatencyTestController.cs（Legacy）/ NewProtocolStack.StartLatencyTest（New）
        private void BtnLatencyTest_Click(object sender, RoutedEventArgs e)
        {
            CurrentView?.StartLatencyTest();
        }

        /* ==================================================================== */
        /* 协议模式与协议栈视图                                                  */
        /*                                                                      */
        /* 约定（实施计划 §2.7）：                                               */
        /*   * 模式是**运行期选择**，不落盘；启动默认 New（App.Mode）；           */
        /*   * 两种模式各用一份配置文件：Legacy→config.json / New→config.new.json；      */
        /*   * 切模式 = 断开当前链路 → 换配置 → （原本连着的话）用新模式重连。      */
        /* ==================================================================== */

        /// <summary>当前模式对应的协议栈视图（未初始化时为 null）</summary>
        private IProtocolStackView? CurrentView
            => (App.Mode == ProtocolMode.New) ? _newView : _legacyView;

        /// <summary>
        /// 构建两个协议栈视图并接管新协议面板：New 走 <see cref="NewProtocolStack"/>，
        /// Legacy 仍是既有的 TransportManager/PackageMaster/LatencyTestController。
        /// </summary>
        private void InitProtocolStackViews()
        {
            if (App.NewStack is { } stack && App.NewConfigSvc is { } newCfg)
            {
                _newView = new NewStackView(stack, newCfg);
                stack.Log += OnNewStackLog;      // 协议栈日志 → 统一走 LogService
            }

            if (App.ConfigSvc is { } legacyCfg && _latencyTest != null)
            {
                _legacyView = new LegacyStackView(legacyCfg, _latencyTest);
            }

            // 新协议的串口列表：单独一个 SerialService（各自的扫描定时器互不干扰）
            _newPortScanner = new SerialService();
            _newPortScanner.OnPortListChanged += RefreshNewSerialPorts;
            RefreshNewSerialPorts(_newPortScanner.GetPortNames());

            LoadNewConfigToUI();
            ApplyProtocolModeUI();

            App.LogSvc?.LogInfo(
                $"[模式] 当前协议：{(App.Mode == ProtocolMode.New ? "New (5A A5)" : "Legacy (0xAA/0xAB)")}" +
                "　（切换后各模式使用自己的配置文件）", Brushes.SeaGreen);
        }

        /// <summary>把当前模式落到界面上：面板显隐、专有按钮显隐、按钮状态、只读状态刷新</summary>
        private void ApplyProtocolModeUI()
        {
            bool isNew = App.Mode == ProtocolMode.New;

            _applyingMode = true;
            try
            {
                RbProtoNew.IsChecked = isNew;
                RbProtoLegacy.IsChecked = !isNew;
            }
            finally
            {
                _applyingMode = false;
            }

            PanelNew.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            PanelLegacy.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            // 「同步系统时间 (0x20)」是 Legacy 专有包，新协议没有对应命令
            BtnSyncTime.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            // 切换后按钮状态按新模式的连接状态重算；新协议的"连接中"由轮询体现
            var view = CurrentView;
            UpdateConnectButtonState(view?.IsConnected == true, view?.SessionActive == true);
            UpdateNewStatusUI();
        }

        /// <summary>模式切换（两个 RadioButton 共用）</summary>
        private void ProtoMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || _isInternalChange || _applyingMode)
            {
                return;
            }

            var target = (RbProtoLegacy.IsChecked == true) ? ProtocolMode.Legacy : ProtocolMode.New;
            if (target == App.Mode)
            {
                ApplyProtocolModeUI();
                return;
            }

            // 切模式 = 断开 → 换配置 → 重连（原本连着才重连）
            bool wasConnected = CurrentView?.IsConnected == true;
            CurrentView?.Disconnect();

            App.Mode = target;

            var view = CurrentView;
            view?.ApplyConfig();                 // 重新注入"新模式"的配置对象
            ApplyProtocolModeUI();

            App.LogSvc?.LogInfo(
                $"[模式] 已切换到 {(target == ProtocolMode.New ? "New (5A A5) → config.new.json" : "Legacy (0xAA/0xAB) → config.json")}",
                Brushes.SeaGreen);

            if (wasConnected && (view != null))
            {
                BtnConnect_Click(this, new RoutedEventArgs());    // 用新模式重新连上
            }
        }

        /// <summary>新协议栈日志 → 界面日志（按级别上色；可能来自后台线程，统一回 UI 线程）</summary>
        private void OnNewStackLog(NpLogLevel level, string msg)
        {
            Brush color = level switch
            {
                NpLogLevel.Error => Brushes.OrangeRed,
                NpLogLevel.Warn => Brushes.Orange,
                _ => Brushes.LightSkyBlue
            };

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(() => App.LogSvc?.LogInfo($"[New] {msg}", color));
        }

        /// <summary>
        /// 刷新新协议面板的只读区（会话状态 / 对端能力 / 延迟）。
        /// 由 100ms 的 UI 定时器驱动：协议栈的事件来自任意线程，这样就不用在每个事件里做跨线程编组。
        /// </summary>
        private void UpdateNewStatusUI()
        {
            var stack = App.NewStack;
            if (stack == null)
            {
                TxtNewSession.Text = "（新协议栈未初始化）";
                TxtNewCaps.Text = "—";
                TxtNewLatency.Text = "—";
                return;
            }

            TxtNewSession.Text = stack.StateText;            // 已区分"链路通"与"对端已应答"
            TxtNewCaps.Text = stack.RemoteCapsText;
            TxtNewLatency.Text = stack.LatencyText;

            // 颜色跟着状态走：绿=对端已应答 / 橙=有链路但还没应答 / 红=对端无应答 / 灰=未连接
            TxtNewSession.Foreground = stack.SessionActive
                ? Brushes.Green
                : stack.HandshakeFailed
                    ? Brushes.OrangeRed
                    : stack.LinkUp ? Brushes.DarkOrange : Brushes.Gray;

            // 按钮与单选的启用状态也跟随（只在 New 模式下改，别抢 Legacy 的按钮）
            if (App.Mode == ProtocolMode.New)
            {
                UpdateConnectButtonState(stack.LinkUp, stack.SessionActive);
            }
        }

        /* ==================================================================== */
        /* 新协议面板（config.new.json 的读写）                                  */
        /* ==================================================================== */

        /// <summary>把 config.new.json 载入新协议面板（只读，不做任何写盘）</summary>
        private void LoadNewConfigToUI()
        {
            var cfg = App.NewConfigSvc?.Current;
            if (cfg == null)
            {
                return;
            }

            bool isCom = cfg.TransportMode == NewTransportType.Com;
            RbNewCom.IsChecked = isCom;
            RbNewTcp.IsChecked = !isCom;
            GridNewCom.Visibility = isCom ? Visibility.Visible : Visibility.Collapsed;
            GridNewTcp.Visibility = isCom ? Visibility.Collapsed : Visibility.Visible;

            ComboNewBaud.Text = cfg.BaudRate.ToString();

            // 串口列表由 _newPortScanner 异步刷新；列表还空着时先放一个占位，免得看不见已存端口
            if ((ComboNewPorts.ItemsSource == null) && !string.IsNullOrEmpty(cfg.ComPortName))
            {
                ComboNewPorts.ItemsSource = new[] { cfg.ComPortName };
                ComboNewPorts.SelectedIndex = 0;
            }

            TxtNewIp.Text = cfg.TcpRemoteIp;
            TxtNewPort.Text = cfg.TcpRemotePort.ToString();
            TxtNewSyncInterval.Text = cfg.SyncIntervalMs.ToString();
            TxtNewSyncOffset.Text = cfg.SyncCurrentOffsetMs.ToString();
            TxtNewLrcPath.Text = cfg.LyricFolder;
        }

        /// <summary>
        /// 把新协议面板上的值写回 config.new.json。
        /// 静默项（SendIntervalMs / AckTimeoutMs / ResourceChunkSize 等）没有界面入口，
        /// 因此只在启动注入 / 这里整体回写时保留原值 —— 与 Legacy 的处理方式一致。
        /// </summary>
        private void SyncAndSaveNewConfig()
        {
            if (!this.IsLoaded || _isInternalChange)
            {
                return;
            }

            var svc = App.NewConfigSvc;
            if (svc == null)
            {
                return;
            }

            var cfg = svc.Current;

            // --- A. 传输 ---
            cfg.TransportMode = (RbNewTcp.IsChecked == true) ? NewTransportType.Tcp : NewTransportType.Com;

            if (ComboNewPorts.SelectedItem != null)
            {
                cfg.ComPortName = ComboNewPorts.SelectedItem.ToString() ?? cfg.ComPortName;
            }

            // 波特率是可编辑 ComboBox：手输的值也算数
            if (int.TryParse(ComboNewBaud.Text, out int baud))
            {
                cfg.BaudRate = Math.Clamp(baud, 1200, 4000000);
            }

            cfg.TcpRemoteIp = TxtNewIp.Text.Trim();
            if (int.TryParse(TxtNewPort.Text, out int nport))
            {
                cfg.TcpRemotePort = Math.Clamp(nport, 1, 65535);
            }

            // --- B. 时间轴 ---
            if (int.TryParse(TxtNewSyncInterval.Text, out int si))
            {
                cfg.SyncIntervalMs = Math.Clamp(si, 50, 30000);
            }
            if (int.TryParse(TxtNewSyncOffset.Text, out int so))
            {
                cfg.SyncCurrentOffsetMs = Math.Clamp(so, -1000, 1000);
            }

            // --- C. 歌词目录 ---
            cfg.LyricFolder = TxtNewLrcPath.Text;

            // --- D. 落盘 + 注入（静默项原样保留） ---
            svc.Save();
            App.NewStack?.ApplyConfig(cfg);

            if (App.Lyrics != null)
            {
                App.Lyrics.LyricFolder = cfg.LyricFolder;
            }
        }

        /// <summary>刷新新协议的串口下拉（可能由后台扫描线程触发）</summary>
        private void RefreshNewSerialPorts(string[] ports)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string? saved = App.NewConfigSvc?.Current?.ComPortName;
                    ComboNewPorts.ItemsSource = ports;

                    if (!string.IsNullOrEmpty(saved) && ports.Contains(saved))
                    {
                        ComboNewPorts.SelectedItem = saved;
                    }
                    else if ((ComboNewPorts.SelectedIndex == -1) && (ports.Length > 0))
                    {
                        ComboNewPorts.SelectedIndex = 0;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UI] 刷新新协议串口列表失败: {ex.Message}");
                }
            });
        }

        private void NewTransMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || _isInternalChange)
            {
                return;
            }

            bool isCom = RbNewCom.IsChecked ?? true;
            GridNewCom.Visibility = isCom ? Visibility.Visible : Visibility.Collapsed;
            GridNewTcp.Visibility = isCom ? Visibility.Collapsed : Visibility.Visible;

            SyncAndSaveNewConfig();
        }

        private void ComboNewConfig_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncAndSaveNewConfig();
        }

        private void BtnNewBrowse_Click(object sender, RoutedEventArgs e)
        {
            string initDir = TxtNewLrcPath.Text;
            if (!Directory.Exists(initDir))
            {
                initDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择歌词搜索目录（新协议）",
                InitialDirectory = initDir
            };

            if (dialog.ShowDialog() == true)
            {
                TxtNewLrcPath.Text = dialog.FolderName;
                SyncAndSaveNewConfig();

                // 换目录后立即用当前歌曲信息重新载入（下一帧资源请求就会带上新歌词）
                App.Lyrics?.LoadAndParse(App.Smtc?.CurrentTitle ?? "", App.Smtc?.CurrentArtist ?? "");
            }
        }

        /// <summary>新协议的 IP 校验：规则与 Legacy 相同（0.0.0.0 / :: 是本机监听地址，不能当连接目标）</summary>
        private void TxtNewIp_LostFocus(object sender, RoutedEventArgs e)
        {
            string raw = TxtNewIp.Text.Trim();

            if (!System.Net.IPAddress.TryParse(raw, out var address) ||
                address.Equals(System.Net.IPAddress.Any) ||
                address.Equals(System.Net.IPAddress.IPv6Any))
            {
                App.LogSvc?.LogInfo(
                    $"[新协议 IP] 「{raw}」不能作为连接目标（0.0.0.0/:: 是本机监听地址）；" +
                    "新协议里 ESP32 是服务端、PC 是客户端，请填 ESP32 的局域网 IP。已回退为 127.0.0.1。",
                    Brushes.OrangeRed);
                TxtNewIp.Text = "127.0.0.1";
            }
            else
            {
                TxtNewIp.Text = address.ToString();
            }

            SyncAndSaveNewConfig();
        }

        /// <summary>新协议数值框的"安检站"：越界拉回合法区间，乱输则整份重载</summary>
        private void NewNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInternalChange)
            {
                return;
            }

            if (sender is TextBox tb)
            {
                if (int.TryParse(tb.Text, out int val))
                {
                    switch (tb.Name)
                    {
                        case "TxtNewPort":
                            tb.Text = Math.Clamp(val, 1, 65535).ToString();
                            break;
                        case "TxtNewSyncInterval":
                            tb.Text = Math.Clamp(val, 50, 30000).ToString();
                            break;
                        case "TxtNewSyncOffset":
                            tb.Text = Math.Clamp(val, -1000, 1000).ToString();
                            break;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(tb.Text))
                {
                    LoadNewConfigToUI();
                }
            }
            else if (sender is ComboBox cb)     // 波特率（可编辑 ComboBox）
            {
                if (int.TryParse(cb.Text, out int baud))
                {
                    cb.Text = Math.Clamp(baud, 1200, 4000000).ToString();
                }
                else if (!string.IsNullOrWhiteSpace(cb.Text))
                {
                    LoadNewConfigToUI();
                }
            }

            SyncAndSaveNewConfig();
        }

        /// <summary>查看当前 SMTC 封面（与协议模式无关：两种模式下都能用）</summary>
        private void BtnShowArtwork_Click(object sender, RoutedEventArgs e) => ShowArtworkPreview();
    }
}
