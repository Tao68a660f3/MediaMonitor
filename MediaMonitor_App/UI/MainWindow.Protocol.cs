using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Services;
using MediaMonitor.UI;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaMonitor
{
    public partial class MainWindow
    {
        // 正在按代码设置模式单选（屏蔽 RadioButton 因程序赋值产生的假“切换”事件）
        private bool _applyingMode;

        // 两种协议栈视图各一份；主窗口只认当前视图
        private IProtocolStackView? _newView;
        private IProtocolStackView? _legacyView;

        // 新协议模式的串口列表来源（与 Legacy 的 SerialService 各自独立）
        private SerialService? _newPortScanner;
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            IProtocolStackView? view = CurrentView;
            if (view == null)
            {
                return;
            }

            // 1. 已经连上 / 正在连接或重试 → 点它就是"停"（重试中按钮文案是「取消重试」）
            if (view.IsConnected || view.ConnectPending)
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
        // linkUp         = 链路已建立（TCP 连上 / 串口打开）
        // sessionActive  = **对端已应答**（新协议 = SESSION ACTIVE；Legacy 传 null，按链路算）
        // connectPending = **连接/重试进行中**（链路还没建立，但后台还在自动尝试）
        //
        // 这个区分很要紧：只开了串口/连上 TCP 但对端根本没应答时，不能显示"已连接"，
        // 按钮也只能是"取消连接"（点击 = 放弃等待）；而"连不上、后台正在重试"时按钮是
        // **「取消重试」**（点击 = 立刻停止重试，不留后台循环）。
        private void UpdateConnectButtonState(bool linkUp, bool? sessionActive = null, bool connectPending = false)
        {
            bool active = sessionActive ?? linkUp;

            if (active)
            {
                BtnConnect.Content = "断开连接";
                BtnConnect.Background = Brushes.OrangeRed;
            }
            else if (connectPending)
            {
                BtnConnect.Content = "取消重试";
                BtnConnect.Background = Brushes.DarkOrange;
            }
            else if (linkUp)
            {
                BtnConnect.Content = "取消连接";
                BtnConnect.Background = Brushes.DarkOrange;
            }
            else
            {
                BtnConnect.Content = "开始连接";
                BtnConnect.Background = Brushes.SeaGreen;
            }

            // 有链路就锁住模式切换与传输参数，避免"跑着换协议"
            RbSerial.IsEnabled = !linkUp;
            RbUdp.IsEnabled = !linkUp;
            ComboBaud.IsEnabled = !linkUp;

            RbProtoNew.IsEnabled = !linkUp;
            RbProtoLegacy.IsEnabled = !linkUp;
            RbNewCom.IsEnabled = !linkUp;
            RbNewTcp.IsEnabled = !linkUp;
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

            // 公共 UI 选项跟随“当前模式”的配置；隐藏规则变化后立即重算列表
            ChkHideClosedSessions.IsChecked = _sessionList?.HideClosed ?? true;
            RefreshSessionList();

            PanelNew.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            PanelLegacy.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            // 「同步系统时间 (0x20)」是 Legacy 专有包，新协议没有对应命令
            BtnSyncTime.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            // 切换后按钮状态按新模式的连接状态重算；新协议的"连接中/重试中"由轮询体现
            var view = CurrentView;
            UpdateConnectButtonState(view?.IsConnected == true, view?.SessionActive == true, view?.ConnectPending == true);
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

            // 颜色跟着状态走：绿=对端已应答 / 橙=有链路但还没应答 / 红=对端无应答·重试用尽 / 灰=未连接
            TxtNewSession.Foreground = stack.SessionActive
                ? Brushes.Green
                : (stack.HandshakeFailed || stack.ReconnectGaveUp)
                    ? Brushes.OrangeRed
                    : stack.LinkUp ? Brushes.DarkOrange : Brushes.Gray;

            // 按钮与单选的启用状态也跟随（只在 New 模式下改，别抢 Legacy 的按钮）
            if (App.Mode == ProtocolMode.New)
            {
                UpdateConnectButtonState(stack.LinkUp, stack.SessionActive, stack.IsRetrying);
            }
        }

    }
}
