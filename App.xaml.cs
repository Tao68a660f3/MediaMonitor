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
        // 全局零件，哪里需要哪里调
        public static PackageConfig Config
        {
            get; private set;
        }
        public static PackageMaster Master
        {
            get; private set;
        }
        public static SmtcService Smtc
        {
            get; private set;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 加载配置 
            Config = ConfigLoader.Load();
            Smtc = new SmtcService();

            // 2. 初始化后台大脑 (启动时不建立物理连接，等 UI 点击连接) 
            Master = new PackageMaster(new DynamicTransport(), new LyricService(Config.LyricFolder), Smtc);
             Master.UpdateConfig(Config);
        
        await Smtc.InitializeAsync();
            Master.Start(); // 启动逻辑循环

            // 3. 显示 UI 进行初始化配置
            var mainWin = new MainWindow();
            mainWin.Show();

            // 4. 初始化托盘（即便 UI 关了，进程也靠它活着）
            InitTray();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 捕获所有线程的未处理异常
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show($"致命错误: {args.ExceptionObject}", "程序崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // 捕获 UI 线程的未处理异常
            this.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"UI 线程错误: {args.Exception.Message}", "同步异常", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true; // 尝试让程序继续运行
            };
        }
    }
}


