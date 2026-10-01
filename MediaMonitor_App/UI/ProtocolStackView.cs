using System;
using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Services;

namespace MediaMonitor.UI
{
    /// <summary>
    /// 协议栈视图：把"连接 / 断开 / 延迟测量 / 配置注入"从 MainWindow 里抽出来，
    /// 两种模式各给一个实现 —— MainWindow 只认这个接口 + 当前模式，
    /// 因此按钮点击处不需要写 <c>if (模式 == ...)</c> 的散点分支。
    /// </summary>
    internal interface IProtocolStackView
    {
        ProtocolMode Mode { get; }

        bool IsConnected { get; }

        /// <summary>给界面看的一行状态文本</summary>
        string StatusText { get; }

        /// <summary>建立链路（内部异步的实现在这里发起，不阻塞 UI）</summary>
        void Connect();

        void Disconnect();

        /// <summary>触发一次延迟测量（两套实现语义相同，落地方式不同）</summary>
        void StartLatencyTest();

        /// <summary>把"当前模式的配置对象"重新注入协议栈</summary>
        void ApplyConfig();

        /// <summary>应用退出：释放资源</summary>
        void Shutdown();
    }

    /// <summary>
    /// Legacy（0xAA/0xAB）视图 —— 只是把既有的 TransportManager / PackageMaster /
    /// LatencyTestController 包一层，**行为与引入新协议之前完全一致**。
    /// </summary>
    internal sealed class LegacyStackView : IProtocolStackView
    {
        private readonly ConfigService _cfg;
        private readonly LatencyTestController _latency;

        public LegacyStackView(ConfigService cfg, LatencyTestController latency)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _latency = latency ?? throw new ArgumentNullException(nameof(latency));
        }

        public ProtocolMode Mode => ProtocolMode.Legacy;

        public bool IsConnected => App.TransportMgr.IsConnected;

        public string StatusText => App.TransportMgr.IsConnected ? "已连接（0xAA/0xAB）" : "未连接";

        public void Connect()
        {
            App.TransportMgr.Connect();

            // 链路建立后立即补发当前元数据，让硬件端从连接开始就显示正确的歌曲信息
            if (App.TransportMgr.IsConnected)
            {
                App.Master?.SendMetadata(
                    App.Smtc?.CurrentTitle ?? "",
                    App.Smtc?.CurrentArtist ?? "",
                    App.Smtc?.CurrentAlbum ?? "");
            }
        }

        public void Disconnect() => App.TransportMgr.Disconnect();

        public void StartLatencyTest() => _latency.Toggle();

        public void ApplyConfig()
        {
            var cfg = _cfg.Current;
            App.TransportMgr.ApplyConfig(cfg);
            _latency.ApplyConfig(cfg);
        }

        public void Shutdown() => _latency.Dispose();
    }

    /// <summary>
    /// New（5A A5）视图 —— 包一层 <see cref="NewProtocolStack"/>，配置直接来自
    /// <c>config.new.json</c>（每次注入都取 <c>Current</c>，因为保存会替换整个对象）。
    /// </summary>
    internal sealed class NewStackView : IProtocolStackView
    {
        private readonly NewProtocolStack _stack;
        private readonly ConfigService<NewProtocolConfig> _cfg;

        public NewStackView(NewProtocolStack stack, ConfigService<NewProtocolConfig> cfg)
        {
            _stack = stack ?? throw new ArgumentNullException(nameof(stack));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        public ProtocolMode Mode => ProtocolMode.New;

        public bool IsConnected => _stack.IsConnected;

        public string StatusText => _stack.StateText;

        public void Connect()
        {
            ApplyConfig();
            _ = _stack.ConnectAsync();     // 内部自带异常处理；状态经 StateChanged/轮询回 UI
        }

        public void Disconnect() => _stack.Disconnect();

        public void StartLatencyTest() => _stack.StartLatencyTest();

        public void ApplyConfig() => _stack.ApplyConfig(_cfg.Current);

        public void Shutdown() => _stack.Dispose();
    }
}
