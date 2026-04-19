using MediaMonitor;
using MediaMonitor.Core;
using MediaMonitor.Services;
using MediaMonitor.Tools;
using System;
using System.Windows;

namespace MediaMonitor
{
    public partial class App : Application
    {
        // 全局单例零件，方便在 MainWindow 中随时调用
        public static PackageMaster? Master
        {
            get; private set;
        }
        public static SmtcService? Smtc
        {
            get; private set;
        }
        public static ConfigService? ConfigSvc
        {
            get; private set;
        }
        public static LyricService? Lyrics
        {
            get; private set;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // --- 1. 你的异常捕获“保险丝” ---
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show($"致命错误: {args.ExceptionObject}", "程序崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            this.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"UI 线程错误: {args.Exception.Message}", "同步异常", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true; // 尝试让程序继续运行
            };

            // --- 2. 核心零件初始化逻辑 ---
            try
            {
                // 加载配置文件
                ConfigSvc = new ConfigService();
                var cfg = ConfigSvc.Current;

                // 按照你定义的属性初始化歌词服务
                Lyrics = new LyricService
                {
                    LyricFolder = cfg.LyricFolder,
                };

                // 初始化 SMTC 监听
                Smtc = new SmtcService();

                // 初始化大脑 (Master)，默认传入一个空的传输层
                // 等你在 MainWindow 点“开启服务”时，我们再通过 Master.UpdateTransport 换成真正的串口或 UDP
                Master = new PackageMaster(new TransportManager(), Lyrics, Smtc);

                // 异步启动 SMTC 服务
                await Smtc.InitializeAsync();

                // 开启逻辑循环（心跳检测开始，但因为是 DummyTransport，所以不会真发数据）
                Master.Start();

                // 最后打开主界面
                new MainWindow().Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化零件失败: {ex.Message}", "启动中止", MessageBoxButton.OK, MessageBoxImage.Stop);
                Shutdown(); // 如果初始化就坏了，直接安全退出
            }
        }
    }
}


