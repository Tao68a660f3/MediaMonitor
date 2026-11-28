using MediaMonitor.Core;
using MediaMonitor.Services;
using MediaMonitor.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MediaMonitor
{
    public partial class MainWindow
    {
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

        private void LoadConfigToUI()
        {
            // 开启静默模式，防止赋值过程触发 TextChanged/Checked 事件导致重复保存
            _isInternalChange = true;

            try
            {
                if (App.ConfigSvc == null)
                    return;
                var cfg = App.ConfigSvc.Current;

                // 公共 UI 选项：隐藏已关闭会话（两份配置由 SessionListController 同步写）
                ChkHideClosedSessions.IsChecked = _sessionList?.HideClosed ?? true;

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
                    if (string.Equals(item.Content?.ToString(), savedEnc, StringComparison.OrdinalIgnoreCase))
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

                // --- 5. 业务层配置分发（传输驱动的装载由构造函数统一执行） ---
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
                    App.Lyrics.LoadAndParse(App.Smtc.CurrentTitle, App.Smtc.CurrentArtist, App.Smtc.CurrentAlbum);
                    App.Master?.Invalidate(); // 强制刷新账本，重新输出当前行
                }
            }
        }

        // 1. 纯数字输入限制 (防止输入字母)
        private void OnlyNumber_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // 允许数字；只有“歌词偏移”允许一个负号
            var textBox = sender as TextBox;
            bool isOffset = textBox is { } tb && string.Equals(tb.Name, "TxtOffset", StringComparison.Ordinal);

            if (isOffset && (e.Text == "-") && (textBox!.Text.IndexOf('-') < 0))
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

            var cfgSvc = App.ConfigSvc;
            if (cfgSvc == null)
                return;

            var cfg = cfgSvc.Current;

            // --- A. 传输模式与物理配置 ---
            cfg.TransportMode = RbSerial.IsChecked == true ? TransportType.Serial : TransportType.UDP;

            // 串口名：ComboPorts 里存的是字符串
            if (ComboPorts.SelectedItem is string portName)
                cfg.SerialPortName = portName;

            // 波特率是可编辑 ComboBox：手输值也算数
            if (int.TryParse(ComboBaud.Text, out int baud))
                cfg.BaudRate = baud;

            // UDP 配置
            cfg.RemoteIp = TxtRemoteIp.Text;
            if (int.TryParse(TxtRemotePort.Text, out int port))
                cfg.RemotePort = port;

            // 编码：同步 EncodingName 即可自动触发内部 Encoding 转换
            if (ComboEncoding.SelectedItem is ComboBoxItem encItem)
            {
                cfg.EncodingName = encItem.Content?.ToString()?.ToLower() ?? cfg.EncodingName;
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
            cfgSvc.Save();

            // 再次注入：静默项（无 UI 入口）不会在这里被改动，因此仍是"仅启动时生效"
            App.TransportMgr.ApplyConfig(cfg);
            _latencyTest?.ApplyConfig(cfg);

            if (App.Lyrics != null)
                App.Lyrics.LyricFolder = cfg.LyricFolder;

            if (App.Master != null)
                App.Master.UpdateConfig(cfg); // 触发业务层热更新
        }

    }
}
