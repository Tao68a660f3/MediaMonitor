using MediaMonitor.Core;
using MediaMonitor.Services;
using MediaMonitor.Tray;
using System;
using System.Windows;
using System.Windows.Media;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private bool _isInternalChange;

        private TrayManager _tray;
        private bool _isRealExit;

        // 全链路延迟测试：全部交互逻辑封装在 UI\LatencyTestController.cs，这里只留调用点
        private LatencyTestController? _latencyTest;

        // 封面预览窗口（显示逻辑见 MainWindow.Playback.cs）
        private ArtworkPreviewWindow? _artPreview;

        public MainWindow()
        {
            InitializeComponent();

            // 0. 窗口标题（含版本号）统一取自 AppInfo：版本号唯一来源 = MediaMonitor.csproj 的 <Version>，
            //    托盘提示在 UpdateUI 里复用 this.Title，于是界面/托盘/exe 属性三处永远一致（以前 XAML 里写死 V3.0.0）。
            Title = AppInfo.WindowTitle;

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

            // 1. UI 定时器：刷新播放信息 / 歌词预览 / 协议只读状态
            StartUiTimer();

            // 2. 媒体源列表（ItemsSource 只绑定一次，后续由 SessionListController 原地 diff）
            InitializeSessionList();

            // 3. 加载当前配置（Legacy / New 各一份）
            LoadConfigToUI();

            // 封面更新回调：是否自动弹窗由 Playback 里的开关决定
            if (App.Smtc != null)
            {
                App.Smtc.OnThumbnailUpdated += OnArtworkUpdated;
            }

            // 订阅歌词变化信号
            if (App.Master != null)
            {
                App.Master.LyricChanged += OnMasterLyricChanged;
            }

            // 日志服务：先于传输错误订阅初始化，后续统一在这里写 UI 日志
            App.LogSvc = new LogService(this.HexPreview);

            // 传输层异常：刷新连接按钮状态并写日志（必须回到 UI 线程）
            App.TransportMgr.OnTransportError += msg =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!App.TransportMgr.IsConnected)
                    {
                        UpdateConnectButtonState(false);
                    }

                    App.LogSvc?.LogInfo($"[传输异常] {msg}", Brushes.OrangeRed);
                }));
            };

            // 延迟测试控制器（按钮交互 / 统计展示 / 日志全部封装在 LatencyTestController 内）
            _latencyTest = new LatencyTestController(
                App.TransportMgr,
                BtnLatencyTest,
                Dispatcher,
                (msg, color) => App.LogSvc?.LogInfo(msg, color));

            // 启动时注入一次配置（静默项以此为准；界面有入口的项在每次保存时再注入）
            _latencyTest.ApplyConfig(App.ConfigSvc?.Current);

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
                _sessionList?.Dispose();       // 摘掉 SMTC 会话列表事件
                _latencyTest?.Dispose();
                _newView?.Shutdown();          // 新协议栈：摘掉 SMTC 事件 + 停掉心跳定时器
                App.Smtc?.Dispose();           // SMTC：摘除 manager 事件、停止会话去抖定时器
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
    }
}
