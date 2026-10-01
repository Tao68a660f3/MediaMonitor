using MediaMonitor;
using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Services;
using MediaMonitor.Tools;
using System;
using System.Windows;

namespace MediaMonitor
{
    public partial class App : Application
    {
        // 全局单例零件，方便在 MainWindow 中随时调用
        public static TransportManager TransportMgr { get; private set; } = new TransportManager();

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

        /// <summary>
        /// 新协议模式的配置服务（`config.new.json`）：与 Legacy 的 <see cref="ConfigSvc"/> 各自独立，
        /// 公共项各存一份 —— 唯一的约定是"从当前模式的配置文件读、写回同一个文件"。
        /// </summary>
        public static ConfigService<NewProtocolConfig>? NewConfigSvc
        {
            get; private set;
        }

        /// <summary>
        /// 当前协议模式（**运行期状态，不落盘**）：启动默认新协议，
        /// 切换见 MainWindow.ProtoMode_Changed（断开 → 换配置 → 重连）。
        /// </summary>
        public static ProtocolMode Mode { get; set; } = ProtocolMode.New;

        /// <summary>新协议栈（P2~P5 的服务装配，见 Protocol/New/Service/NewProtocolStack.cs）</summary>
        public static NewProtocolStack? NewStack
        {
            get; private set;
        }
        public static LyricService? Lyrics
        {
            get; private set;
        }
        public static LogService? LogSvc
        {
            get; set;
        }
        public static BackControlService? BackControl
        {
            get; private set;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

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

                // 新协议模式的配置（config.new.json）：独立文件、独立默认值，公共项各存一份
                NewConfigSvc = new ConfigService<NewProtocolConfig>("config.new.json");

                // 配置分发（启动时注入一次）：静默项（UI 无入口的 SendIntervalMs 等）只在此刻生效；
                // 界面有入口的项由 MainWindow.SyncAndSaveConfig() 在保存时再次注入。
                TransportMgr.ApplyConfig(cfg);

                // 编码是"文本包"的全局开关（PackageBuilder 内部是静态状态），启动即对齐一次配置
                PackageBuilder.UpdateEncoding(cfg.Encoding);

                // 按照你定义的属性初始化歌词服务
                Lyrics = new LyricService
                {
                    LyricFolder = cfg.LyricFolder,
                };

                // 初始化 SMTC 监听
                Smtc = new SmtcService();

                // 传入 TransportMgr，让 Service 自动订阅 OnRawDataReceived 事件
                BackControl = new BackControlService(TransportMgr);

                // 初始化大脑 (Master)，默认传入一个空的传输层
                // 等你在 MainWindow 点“开启服务”时，我们再通过 Master.UpdateTransport 换成真正的串口或 UDP
                Master = new PackageMaster(TransportMgr, Lyrics, Smtc);

                // 新协议栈（Transport → Codec → Session → 各 Service 的装配）。
                // 创建即订阅 SMTC/歌词数据源；只有 MainWindow 在 New 模式下点了「开始连接」才真正建链路。
                NewStack = new NewProtocolStack(NewConfigSvc.Current, Smtc, Lyrics);

                // 异步启动 SMTC 服务
                await Smtc.InitializeAsync();

                // 开启逻辑循环
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


