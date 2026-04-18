using MediaMonitor.Services;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaMonitor
{
    public partial class MainWindow : Window
    {
        private bool _isInternalChange = false; // 防止初始化时触发 TextChanged 导致循环调用

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 注册 GB2312 支持

            // 初始化 UI 状态
            LoadSettingsToUI();

            // 挂载后台服务委托
            AttachBackend();
        }

        // 仅数字输入限制
        private void OnlyNumber_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = new Regex("[^0-9]+").IsMatch(e.Text);
        }

        private void TransMode_Changed(object sender, RoutedEventArgs e)
        {
            // 保护逻辑：防止在 UI 初始化完成前被触发
            if (GridSerialConfig == null || GridUdpConfig == null)
                return;

            bool isSerial = RbSerial.IsChecked ?? true;

            // 切换容器可见性
            GridSerialConfig.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
            GridUdpConfig.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;

            // 如果已经初始化，自动保存一次模式选择
            if (!_isInternalChange)
                ApplyAndSaveConfig();
        }

        // 从后台配置对象加载到 UI
        private void LoadSettingsToUI()
        {
            _isInternalChange = true;
            var cfg = App.ConfigSvc.Current;

            // 1. 传输模式
            RbSerial.IsChecked = cfg.TransportMode == TransportType.Serial;
            RbUdp.IsChecked = cfg.TransportMode == TransportType.UDP;
            TransMode_Changed(null, null); // 触发一次显隐切换

            // 2. 串口/UDP 专属项
            ComboPorts.Text = cfg.SerialPortName;
            TxtPortOrBaud.Text = (cfg.TransportMode == TransportType.Serial)
                ? cfg.BaudRate.ToString()
                : cfg.RemotePort.ToString();
            TxtRemoteIp.Text = cfg.RemoteIp;

            // 3. 协议开关
            ChkAdvancedMode.IsChecked = cfg.IsAdvancedMode;
            ChkIncremental.IsChecked = cfg.IsIncremental;
            ChkTransOccupies.IsChecked = cfg.TransOccupies;

            // 4. 运行参数
            TxtLineLimit.Text = cfg.LineLimit.ToString();
            TxtOffset.Text = cfg.Offset.ToString();
            TxtSyncInterval.Text = cfg.SyncIntervalMs.ToString();
            TxtLrcPath.Text = cfg.LyricFolder;

            _isInternalChange = false;
        }

        // 从 UI 提取并保存到后台
        private void ApplyAndSaveConfig()
        {
            if (_isInternalChange)
                return;

            var cfg = App.ConfigSvc.Current;

            // 读取传输模式
            cfg.TransportMode = RbSerial.IsChecked == true ? TransportType.Serial : TransportType.UDP;

            if (cfg.TransportMode == TransportType.Serial)
            {
                cfg.SerialPortName = ComboPorts.Text;
                cfg.BaudRate = int.TryParse(TxtPortOrBaud.Text, out int br) ? br : 115200;
            }
            else
            {
                cfg.RemoteIp = TxtRemoteIp.Text;
                cfg.RemotePort = int.TryParse(TxtPortOrBaud.Text, out int rp) ? rp : 8080;
            }

            // 读取其他参数
            cfg.IsAdvancedMode = ChkAdvancedMode.IsChecked ?? true;
            cfg.IsIncremental = ChkIncremental.IsChecked ?? true;
            cfg.TransOccupies = ChkTransOccupies.IsChecked ?? true;
            cfg.LineLimit = int.TryParse(TxtLineLimit.Text, out int ll) ? ll : 2;
            cfg.Offset = int.TryParse(TxtOffset.Text, out int os) ? os : 0;
            cfg.SyncIntervalMs = int.TryParse(TxtSyncInterval.Text, out int si) ? si : 500;
            cfg.LyricFolder = TxtLrcPath.Text;

            // 持久化并通知后台大脑
            App.ConfigSvc.Save();
            App.Master.UpdateConfig(cfg);
        }
    }
}