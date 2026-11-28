using MediaMonitor.Core;
using MediaMonitor.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaMonitor
{
    public partial class MainWindow
    {
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

    }
}
