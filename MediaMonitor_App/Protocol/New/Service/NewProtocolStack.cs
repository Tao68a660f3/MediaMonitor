using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Codec;
using MediaMonitor.Protocol.New.Transport;
using MediaMonitor.Services;
using Windows.Media.Control;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>协议栈日志级别（UI 层再映射成颜色，协议层不依赖 WPF）</summary>
    public enum NpLogLevel { Info, Warn, Error }

    /// <summary>
    /// 新协议栈的装配（实施计划 §1.1）。
    ///
    /// <para>把 Transport → Codec → Session → 各 Service 串起来，对 UI 只暴露
    /// Connect / Disconnect / 状态文本 / 日志事件。**不引用任何 Legacy 协议逻辑**，
    /// 只读复用业务数据源（<see cref="SmtcService"/> / <see cref="LyricService"/>）。</para>
    ///
    /// <para>帧分发顺序（重要）：<c>ResourceSender</c>（REQUEST/ACK）→ <c>SessionManager</c>（SYSTEM）
    /// → <c>LatencyManager</c>（LATENCY_*）→ <c>ControlReceiver</c>（CONTROL）→ 其余记日志。</para>
    /// </summary>
    public sealed class NewProtocolStack : IDisposable
    {
        private readonly SmtcService _smtc;
        private readonly LyricService _lyrics;
        private readonly Action<byte>? _controlExecutor;   // 回控落地（app 注入 MediaKeyInvoker；协议层不依赖 WPF）
        private readonly Func<NewProtocolConfig, (INewTransport? Transport, string? Error)>? _comTransportFactory;

        private readonly NpStreamParser _parser = new NpStreamParser();
        private readonly Timer _tickTimer;

        private NewProtocolConfig _cfg;
        private INewTransport? _transport;
        private NewSendScheduler? _scheduler;
        private SessionManager? _session;
        private MediaPublisher? _media;
        private TimelinePump? _timeline;
        private ControlReceiver? _control;
        private ResourceSender? _resources;
        private LatencyManager? _latency;

        private bool _wantConnect;
        private bool _disposed;
        private bool _tearingDown;             // 主动拆链路标志：抑制传输层回抛的"断线"事件
        private bool _thumbEventForTrack;      // 当前曲目是否已经有过封面事件（用于区分"没封面"与"还没读出来"）
        private ProtocolState _lastSessionState = ProtocolState.Idle;   // 用于区分"状态跃迁"与"同状态重复通知"

        /* ---- 连接 / 重试：重试次数与退避都定义在 NpReconnectPolicy 里 ---- */

        // 串行化"拆旧链路 + 建新链路"：并发打开会同时挂两条连接（第二条会把在用的那条顶掉）。
        private readonly SemaphoreSlim _linkGate = new SemaphoreSlim(1, 1);
        private readonly object _loopGate = new object();

        private NpReconnectPolicy _retry = new NpReconnectPolicy();      // 本次连接的重试计数（首次 + 重试）
        private CancellationTokenSource _linkCts = new CancellationTokenSource();
        private Task? _reconnectTask;                                   // **只允许一个**重连循环
        private bool _retryWaiting;                                     // 正在等退避（只是状态栏文案）
        private string? _giveUpReason;                                  // 非 null = 已停止重试，等用户手动重来
        private string? _lastFailureReason;                             // 最近一次失败原因（停止时原样带出）

        // 当前链路的事件委托（拆链路时要精确摘掉；顺带用"是不是当前这条链路"做代际校验）
        private Action<ReadOnlyMemory<byte>>? _onData;
        private Action<string>? _onDisconnected;

        public event Action<NpLogLevel, string>? Log;
        public event Action? StateChanged;

        public NewProtocolStack(NewProtocolConfig cfg, SmtcService smtc, LyricService lyrics,
                                Action<byte>? controlExecutor = null,
                                Func<NewProtocolConfig, (INewTransport? Transport, string? Error)>? comTransportFactory = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _smtc = smtc ?? throw new ArgumentNullException(nameof(smtc));
            _lyrics = lyrics ?? throw new ArgumentNullException(nameof(lyrics));
            _controlExecutor = controlExecutor;             // null = 不回控（自测场景）；app 传 MediaKeyInvoker
            _comTransportFactory = comTransportFactory;     // app 传串口传输的实现；协议层不依赖串口服务

            // 会话心跳：10ms 驱动 SessionManager.Tick（重传/超时）
            _tickTimer = new Timer(_ => SafeTick(), null, 0, 10);

            // 业务数据源（只读复用，不反向依赖）
            _smtc.OnMediaUpdated += OnMediaUpdated;
            _smtc.MediaCleared += OnMediaCleared;
            _smtc.PlaybackChanged += OnPlaybackChanged;
            _smtc.OnThumbnailUpdated += OnThumbnailUpdated;
        }

        /* ------------------------------------------------------------------ */
        /* 对外状态                                                            */
        /* ------------------------------------------------------------------ */

        public bool WantConnect => _wantConnect;

        /// <summary>链路层：TCP 已连上 / 串口已打开。**不代表对端在**。</summary>
        public bool LinkUp => _transport?.IsConnected == true;

        /// <summary>会话层：已收到 SESSION_START，对端确实在按新协议对话（这才是"连上了"）。</summary>
        public bool SessionActive => _session?.IsActive == true;

        /// <summary>握手失败（HELLO 重发用尽仍无 HELLO_ACK）：对端多半没启动/波特率不对/接线有问题</summary>
        public bool HandshakeFailed => _session?.HandshakeFailed == true;

        public bool IsConnected => LinkUp;

        /// <summary>
        /// 连接 / 重试**还在进行中**（点了连接但链路还没建立）：
        /// 界面据此把按钮显示成「取消重试」—— 点一下就是"立刻停"，不留后台循环。
        /// </summary>
        public bool IsRetrying => _wantConnect && !LinkUp;

        /// <summary>自动重试用尽已停止（界面把状态染红；再连要用户手动点「开始连接」）</summary>
        public bool ReconnectGaveUp => _giveUpReason != null;

        /// <summary>已用掉的重试轮数（不含首次尝试）</summary>
        public int ReconnectAttempt => _retry.Attempts;

        /// <summary>重试次数上限（不含首次尝试）</summary>
        public static int ReconnectMaxAttempts => NpReconnectPolicy.MaxRetries;

        /// <summary>当前连接目标（状态栏 / 日志用，一眼看清在打哪个口）</summary>
        public string ConnectTargetText => _cfg.TransportMode == NewTransportType.Tcp
            ? $"TCP {_cfg.TcpRemoteIp}:{_cfg.TcpRemotePort}"
            : $"{_cfg.ComPortName} @ {_cfg.BaudRate}";

        /// <summary>
        /// 会话状态文本 —— 严格区分"链路通"与"对端已应答"，避免"没收到 ACK 就显示已连接"；
        /// 连接阶段还会带上**目标**与**第几次重试**（这样"连不上"时不用猜在打哪个口）。
        /// </summary>
        public string StateText
        {
            get
            {
                if (!LinkUp)
                {
                    if (!_wantConnect)
                    {
                        return _giveUpReason == null
                            ? "未连接"
                            : $"未连接（{_giveUpReason}）";
                    }

                    if (_retry.Attempts == 0)
                    {
                        return $"连接中… {ConnectTargetText}（首次尝试）";
                    }

                    return $"连接中… {ConnectTargetText}（{_retry.Describe(_retryWaiting)}）";
                }

                if (SessionActive)
                {
                    return "已连接 · SESSION ACTIVE";
                }

                if (HandshakeFailed)
                {
                    return "未连接（对端无应答）";
                }

                return $"链路已通，等待对端应答…（{StateName(_session?.State ?? ProtocolState.Idle)}）";
            }
        }

        /// <summary>只读的"对端能力"文本（来自 HELLO_ACK）</summary>
        public string RemoteCapsText
        {
            get
            {
                NpCaps? caps = _session?.RemoteCaps;
                if (caps == null)
                {
                    return "（尚未收到 HELLO_ACK）";
                }

                return $"资源: {(caps.Has(NpCapsBits.Lyrics) ? "歌词✓" : "歌词✗")} {(caps.Has(NpCapsBits.AlbumCover) ? "封面✓" : "封面✗")}" +
                       $"　格式: {(caps.Has(NpCapsBits.Jpeg) ? "JPEG✓" : "JPEG✗")} {(caps.Has(NpCapsBits.Png) ? "PNG✓" : "PNG✗")} " +
                       $"{(caps.Has(NpCapsBits.Rgb565) ? "RGB565✓" : "RGB565✗")}" +
                       $"　最大边长: {(caps.MaxImageEdge == 0 ? "不限" : caps.MaxImageEdge + "px")}" +
                       $"　最大资源: {(caps.MaxResourceSize == 0 ? "不限" : caps.MaxResourceSize + "B")}";
            }
        }

        /// <summary>最近一次延迟测量结果文本</summary>
        public string LatencyText => _latency?.LastStats is { } s
            ? $"{s}　状态: {(_latency.IsRunning ? "测量中…" : "空闲")}"
            : "（未测量）";

        private static string StateName(ProtocolState st) => st switch
        {
            ProtocolState.Idle => "IDLE",
            ProtocolState.Handshake => "握手中",
            ProtocolState.Negotiating => "协商中",
            ProtocolState.WaitSessionStart => "等 SESSION_START",
            ProtocolState.Active => "SESSION ACTIVE",
            _ => "?"
        };

        /* ------------------------------------------------------------------ */
        /* 连接 / 断开（切模式 = 断开 → 重注入配置 → 重连）                      */
        /* ------------------------------------------------------------------ */

        /// <summary>注入配置（切模式 / 参数变更后调用；下次连接生效）</summary>
        public void ApplyConfig(NewProtocolConfig cfg) => _cfg = cfg ?? _cfg;

        /// <summary>
        /// 建立链路并启动握手（异步：TCP 连接可能耗时，不阻塞 UI 线程）。
        ///
        /// <para>**首次尝试失败后**转交 <see cref="ReconnectLoopAsync"/> 做有界重试
        /// （<see cref="NpReconnectPolicy.MaxRetries"/> 次，不含首次）；重试用尽即停止，
        /// 状态显示「连接失败」，等用户点「开始连接」再来 —— 不再无限重连。</para>
        /// </summary>
        public async Task ConnectAsync()
        {
            _wantConnect = true;
            _giveUpReason = null;
            _lastFailureReason = null;
            _retryWaiting = false;
            _retry = new NpReconnectPolicy(_cfg.TcpReconnectBackoffMs);      // 每次"点连接"都重新给满重试预算
            StopReconnectLoop();                                            // 掐掉上一轮遗留的重试（令牌作废）
            RaiseStateChanged();

            if (await OpenLinkGuardedAsync().ConfigureAwait(true))
            {
                return;
            }

            if (_cfg.TransportMode == NewTransportType.Tcp)
            {
                StartReconnectLoop();                                       // 首次失败 → 交给有界重试
            }
            else
            {
                _wantConnect = false;                                       // COM：串口拔插由用户重按，不自动重试
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// 主动断开（用户操作 / 切模式）：清掉"期望连接"位、**立刻掐掉重试循环**（退避中的等待会被打断），
        /// 再拆链路 —— 这样"连不上"时点一下按钮就能真正停下来。
        /// </summary>
        public void Disconnect()
        {
            _wantConnect = false;
            _retryWaiting = false;
            _giveUpReason = null;
            StopReconnectLoop();
            Teardown();
            RaiseStateChanged();
        }

        /// <summary>
        /// 串行化地"拆旧链路 + 建一条新链路"。**已经有链路时直接返回 true，绝不再开第二条** ——
        /// 过去重连循环不看"是否已连上"就新建连接，会把正在用的那条顶掉：新链路没人服务（对端在忙旧的），
        /// 旧链路上的时间轴却还在跑，于是表现为"只有时间轴能用、别的全废"。
        /// </summary>
        private async Task<bool> OpenLinkGuardedAsync()
        {
            await _linkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (LinkUp)
                {
                    return true;                                            // 别处已经连上了：收工
                }

                Teardown();                                                 // 开新链路前必须彻底拆掉旧的（含旧服务与旧事件订阅）
                _lastSessionState = ProtocolState.Idle;
                _parser.Reset();
                return await OpenTransportAsync().ConfigureAwait(false);
            }
            finally
            {
                _linkGate.Release();
            }
        }

        /// <summary>创建并接好传输层 + 全套服务，然后发 HELLO</summary>
        private async Task<bool> OpenTransportAsync()
        {
            INewTransport transport;

            if (_cfg.TransportMode == NewTransportType.Tcp)
            {
                var tcp = new TcpTransport(_cfg.TcpRemoteIp, _cfg.TcpRemotePort);
                Emit(NpLogLevel.Info, $"连接目标：{ConnectTargetText} …");
                if (!await tcp.ConnectAsync(3000).ConfigureAwait(true))
                {
                    _lastFailureReason = $"连接失败：{ConnectTargetText} 无人应答";
                    Emit(NpLogLevel.Error,
                         $"TCP 连接失败：{ConnectTargetText} —— 没有服务在该地址/端口上监听。");
                    return false;
                }
                transport = tcp;
            }
            else
            {
                Emit(NpLogLevel.Info, $"连接目标：{ConnectTargetText} …");

                // 串口实现由 app 注入：协议层不依赖串口服务（它带着 WPF 依赖），自测工程也就不用链接它
                (INewTransport? com, string? comError) = (null, "app 没有注入 COM 传输工厂");
                if (_comTransportFactory != null)
                {
                    (com, comError) = _comTransportFactory(_cfg);
                }

                if (com == null)
                {
                    _lastFailureReason = $"串口打开失败（{comError}）";
                    Emit(NpLogLevel.Error, $"串口打开失败：{comError}");
                    return false;
                }

                transport = com;
            }

            _transport = transport;
            _parser.Reset();

            /* ---- 装配顺序：Scheduler → Session → 各 Service ---- */
            var scheduler = new NewSendScheduler(transport, _cfg.SendIntervalMs);
            var session = new SessionManager(scheduler);
            var media = new MediaPublisher(session);
            var timeline = new TimelinePump(session,
                                            new OffsetTimelineSource(new SmtcTimelineSource(_smtc), _cfg.SyncCurrentOffsetMs),
                                            _cfg.SyncIntervalMs);
            var latency = new LatencyManager(session);
            var control = new ControlReceiver(session, ExecuteLegacyControl);
            var resources = new ResourceSender(session, ProvideResource, _cfg.ResourceChunkSize);

            _scheduler = scheduler;
            _session = session;
            _media = media;
            _timeline = timeline;
            _latency = latency;
            _control = control;
            _resources = resources;

            // 事件委托按"这一条链路"绑定：旧链路的事件不许影响新链路（代际校验）
            INewTransport bound = transport;
            _onData = d => { if (ReferenceEquals(_transport, bound)) { OnDataReceived(d); } };
            _onDisconnected = r => { if (ReferenceEquals(_transport, bound)) { OnTransportDisconnected(r); } };

            transport.DataReceived += _onData;
            transport.Disconnected += _onDisconnected;
            _parser.FrameReceived += OnFrame;
            session.StateChanged += OnSessionStateChanged;
            latency.StatsUpdated += _ => RaiseStateChanged();

            scheduler.Start();
            session.LinkUp();                     // 发 HELLO（§5.4）

            Emit(NpLogLevel.Info, "链路已建立，已发送 HELLO");
            return true;
        }

        /// <summary>
        /// 启动（或复用）**唯一**的重连循环。多循环并存是"链路被偷偷换掉"的根源，所以这里只认一个。
        /// </summary>
        private void StartReconnectLoop()
        {
            lock (_loopGate)
            {
                if (_reconnectTask is { IsCompleted: false })
                {
                    return;                                     // 已经有一个循环在跑：绝不再起第二个
                }

                _linkCts = new CancellationTokenSource();        // 上一轮的令牌已经作废，重开一个
                _reconnectTask = ReconnectLoopAsync(_linkCts.Token);
            }
        }

        /// <summary>掐掉重连循环（退避中的 <see cref="Task.Delay(int, CancellationToken)"/> 会被立刻打断）</summary>
        private void StopReconnectLoop()
        {
            try
            {
                _linkCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放：当作已停
            }
        }

        /// <summary>
        /// 自动重试：**最多 <see cref="NpReconnectPolicy.MaxRetries"/> 次（不含首次尝试）**，轮间按退避等待；
        /// 预算用尽即 <see cref="GiveUp"/> 停止（不再无限重连）。
        /// COM 不做自动重试：串口拔插由用户重按。
        /// </summary>
        private async Task ReconnectLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed && _wantConnect)
            {
                if (!_retry.TryBeginNext(out int delay))
                {
                    GiveUp(_lastFailureReason ?? "连接失败");
                    return;
                }

                _retryWaiting = true;
                Emit(NpLogLevel.Info, $"{_retry.Describe(true)} → {ConnectTargetText} …");
                RaiseStateChanged();

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _retryWaiting = false;
                    return;                                     // 用户点了「取消重试」：安静退出
                }

                if (ct.IsCancellationRequested || _disposed || !_wantConnect)
                {
                    _retryWaiting = false;
                    return;
                }

                _retryWaiting = false;
                RaiseStateChanged();

                if (await OpenLinkGuardedAsync().ConfigureAwait(false))
                {
                    return;                                     // 链路已建立；会话是否成立由握手结果决定
                }
            }

            _retryWaiting = false;
        }

        /// <summary>
        /// 重试用尽：**停止**并给出可读原因，等用户手动点「开始连接」重来（清的 <c>_wantConnect</c>
        /// 顺带让按钮从「取消重试」回到「开始连接」）。
        /// </summary>
        private void GiveUp(string reason)
        {
            _wantConnect = false;
            _retryWaiting = false;
            _giveUpReason = reason;

            Emit(NpLogLevel.Error,
                 $"已停止重试：{reason}。首次尝试 + {_retry.Attempts} 次重试都没成功，" +
                 "检查对端与参数后点「开始连接」重来。");
            RaiseStateChanged();
        }

        /// <summary>
        /// 拆链路：先摘事件、停服务，再关传输 —— 顺序反了后台线程会往已关闭的口写。
        /// **不动 <c>_wantConnect</c>**（是否继续连接由调用方决定）。
        /// </summary>
        private void Teardown()
        {
            _tearingDown = true;

            _parser.FrameReceived -= OnFrame;

            if (_transport != null)
            {
                if (_onData != null)
                {
                    _transport.DataReceived -= _onData;
                }

                if (_onDisconnected != null)
                {
                    _transport.Disconnected -= _onDisconnected;
                }
            }

            _onData = null;
            _onDisconnected = null;

            if (_session != null)
            {
                _session.StateChanged -= OnSessionStateChanged;
            }

            _timeline?.Stop();
            _latency?.Stop();

            _resources = null;
            _control = null;
            _media = null;

            _timeline?.Dispose();
            _timeline = null;
            _latency?.Dispose();
            _latency = null;

            _session?.Dispose();
            _session = null;

            _scheduler?.Dispose();
            _scheduler = null;

            _transport?.Close();
            _transport = null;

            _parser.Reset();
            _tearingDown = false;
        }

        /* ------------------------------------------------------------------ */
        /* 数据面：链路事件 → 解析 → 分发                                       */
        /* ------------------------------------------------------------------ */

        private void OnDataReceived(ReadOnlyMemory<byte> data)
        {
            try
            {
                _parser.Feed(data.Span);
            }
            catch (Exception ex)
            {
                Emit(NpLogLevel.Error, $"解析异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 帧分发顺序（与端到端自测保持一致，顺序不能换）：
        /// ResourceSender(REQUEST / 资源 ACK) → SessionManager(SYSTEM) → LatencyManager(LATENCY_*) → ControlReceiver(CONTROL)。
        /// </summary>
        private void OnFrame(NpFrame f)
        {
            try
            {
                _resources?.OnFrame(f);                       // 资源帧（REQUEST / 资源 ACK）：必须先于会话层

                if (_session?.OnFrame(f) == true)
                {
                    return;                                   // SYSTEM：会话层已消费
                }

                if (IsLatencyFrame(f))
                {
                    _latency?.OnFrame(f);                     // LATENCY_REQUEST / RESPONSE / END
                    return;
                }

                if (_control?.OnFrame(f) == true)
                {
                    return;                                   // CONTROL：已执行并回 ACK
                }

                // ResourceSender / LatencyManager 的 OnFrame 没有返回值，只能按类型判断"已被消费"，
                // 否则每一帧资源/延迟帧都会被误报成"未处理帧"（实测到的日志噪声）。
                if (IsResourceFrame(f))
                {
                    return;
                }

                Emit(NpLogLevel.Warn, $"未处理帧 T=0x{f.Type:X2} C=0x{f.Code:X2} len={f.PayloadLen} seq={f.Sequence}");
            }
            catch (Exception ex)
            {
                Emit(NpLogLevel.Error, $"帧处理异常：{ex.Message}");
            }
        }

        /// <summary>资源帧（歌词 / 封面）：由 <see cref="ResourceSender"/> 消费</summary>
        private static bool IsResourceFrame(in NpFrame f)
            => (f.Type == NpType.Lyrics) || (f.Type == NpType.AlbumCover);

        /// <summary>延迟测量帧：由 <see cref="LatencyManager"/> 消费</summary>
        private static bool IsLatencyFrame(in NpFrame f)
            => (f.Type == NpType.System) &&
               ((f.Code == NpSysCode.LatencyRequest) ||
                (f.Code == NpSysCode.LatencyResponse) ||
                (f.Code == NpSysCode.LatencyEnd));

        private void OnTransportDisconnected(string reason)
        {
            if (_tearingDown)
            {
                return;                                       // 自家 Close() 引发的，不算断线
            }

            Emit(NpLogLevel.Warn, $"链路断开：{reason}");
            _lastFailureReason = $"链路断开（{reason}）";

            Teardown();
            RaiseStateChanged();

            if (!_wantConnect || _disposed)
            {
                return;                                       // 用户已经点了"停/断开"：不重连
            }

            if (_cfg.TransportMode != NewTransportType.Tcp)
            {
                _wantConnect = false;                         // COM：串口拔插由用户重按
                RaiseStateChanged();
                return;
            }

            StartReconnectLoop();                             // 掉线重连同样受"重试次数上限"约束
        }

        /// <summary>
        /// 握手失败（HELLO 重发用尽仍无 HELLO_ACK）—— 说明**这条链路其实没人应答**，
        /// 典型是"连上了对端却没进 New 模式"或只有半条链路在通。这时必须**主动关掉它**：
        /// 留着就是僵尸链路（旧时间轴可能仍在发，看起来"只有时间轴能用、别的全废"）。
        /// 关掉之后交给重试预算：还有次数就再连一轮，用尽则停止并提示。
        /// </summary>
        private void OnHandshakeFailed()
        {
            Emit(NpLogLevel.Error,
                 $"握手失败：{ConnectTargetText} 已建立链路但等不到 HELLO_ACK —— " +
                 "对端可能没进入 New 模式 / 波特率不一致 / 链路半通。已关闭这条链路。");

            _lastFailureReason = "对端无应答：等不到 HELLO_ACK";
            Teardown();
            RaiseStateChanged();

            if (!_wantConnect || _disposed)
            {
                return;
            }

            if (_cfg.TransportMode != NewTransportType.Tcp)
            {
                _wantConnect = false;                         // COM：交给用户重按
                RaiseStateChanged();
                return;
            }

            StartReconnectLoop();                             // 有预算就再来一轮，用尽则由循环内部 GiveUp
        }

        private void OnSessionStateChanged(ProtocolState st)
        {
            // 只有"进入新状态"才做事：会话层可能在状态没变时也会通知（例如重复收到 SESSION_START），
            // 若每次都当成"刚进入"，就会把 MEDIA / 时间轴打成高频（实测：50 帧/秒冲垮链路）。
            bool entered = st != _lastSessionState;
            _lastSessionState = st;

            if (!entered)
            {
                RaiseStateChanged();
                return;
            }

            switch (st)
            {
                case ProtocolState.Active:
                    Emit(NpLogLevel.Info, $"会话建立：SESSION_ID=0x{_session?.SessionId:X8}（时间轴 {_cfg.SyncIntervalMs}ms）");
                    _thumbEventForTrack = false;
                    LoadLyricsForCurrent();
                    PublishMediaNow();                        // §6：会话建立后补推一次元数据
                    _timeline?.PublishNow();                  // §7：先补发当前进度…
                    _timeline?.Start();                       // …再起节拍（顺序反了会重复发同一点）
                    break;

                case ProtocolState.Idle:
                case ProtocolState.WaitSessionStart:
                    _timeline?.Stop();                        // 会话未成立不发时间轴

                    if ((st == ProtocolState.Idle) && (_session?.HandshakeFailed == true))
                    {
                        // 链路通但会话没建立 → 关掉这条僵尸链路（并计入重试预算）
                        OnHandshakeFailed();
                    }
                    else
                    {
                        Emit(NpLogLevel.Info, $"会话状态 → {StateName(st)}");
                    }
                    break;

                default:
                    Emit(NpLogLevel.Info, $"会话状态 → {StateName(st)}");
                    break;
            }

            RaiseStateChanged();
        }

        /* ------------------------------------------------------------------ */
        /* 业务数据源（只读复用 SMTC / LyricService，不反向依赖）                */
        /* ------------------------------------------------------------------ */

        private void LoadLyricsForCurrent()
        {
            _lyrics.LyricFolder = _cfg.LyricFolder ?? "";
            _lyrics.LoadAndParse(_smtc.CurrentTitle ?? "", _smtc.CurrentArtist ?? "");

            Emit(NpLogLevel.Info, _lyrics.IsPlaceholder
                ? $"歌词：未找到（目录 {_lyrics.LyricFolder}）"
                : $"歌词：{Path.GetFileName(_lyrics.CurrentLyricPath)} 共 {_lyrics.Lines.Count} 行（[offset]={_lyrics.CurrentOffsetMs}ms）");
        }

        private void PublishMediaNow()
            => _media?.Publish(_smtc.CurrentTitle, _smtc.CurrentArtist, _smtc.CurrentAlbum);

        private void OnMediaUpdated(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            _thumbEventForTrack = false;

            if (!_wantConnect)
            {
                return;                                       // 未启用新协议栈（Legacy 模式）：不重复解析歌词
            }

            _resources?.AbortCurrent("切歌");               // §9.8：在途资源作废，对端会自行重发 REQUEST
            LoadLyricsForCurrent();
            PublishMediaNow();
            _timeline?.PublishNow();
        }

        private void OnMediaCleared()
        {
            _thumbEventForTrack = false;

            if (!_wantConnect)
            {
                return;
            }

            _resources?.AbortCurrent("无会话");
            _lyrics.LoadAndParse("", "");
            _media?.PublishEmpty();                         // §6：空元数据 → 硬件清屏复位
            _timeline?.PublishNow();
        }

        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSessionPlaybackStatus st)
        {
            if (_wantConnect)
            {
                _timeline?.PublishNow();                    // 播放/暂停/跳转立刻补一帧，不等节拍
            }
        }

        private void OnThumbnailUpdated(byte[]? raw)
        {
            // 有事件就说明"读过了"：null 才是真的没封面（此时按 §11 发**空资源 FORMAT=0x00**，而不是 NOT_READY）
            _thumbEventForTrack = true;

            if (_wantConnect)
            {
                Emit(NpLogLevel.Info, (raw == null)
                    ? "封面：SMTC 已通知本曲目无封面 → 对端请求时回空资源（FORMAT=0x00）"
                    : $"封面：SMTC 已就绪（{raw.Length} 字节）→ 对端请求时可正常发送");
            }
        }

        /* ------------------------------------------------------------------ */
        /* 资源提供（规范 §10 歌词 / §11 封面）                                 */
        /* ------------------------------------------------------------------ */

        private NpResourceData? ProvideResource(byte type, ushort width, ushort height, NpResFormat format, byte quality)
        {
            if (type == NpType.Lyrics)
            {
                TimeSpan total = _smtc.GetCurrentProgress()?.Duration ?? TimeSpan.Zero;
                byte[] data = LyricResourceBuilder.Build(_lyrics, total, out int lineCount);
                Emit(NpLogLevel.Info, $"生成歌词资源：{data.Length} 字节 / {lineCount} 行");
                return new NpResourceData(Crc32Ieee.Compute(data), data, 0, 0, 0);
            }

            if (type != NpType.AlbumCover)
            {
                return null;                                  // 未知资源类型 → NOT_READY（保持与 C 侧一致）
            }

            if (!_thumbEventForTrack)
            {
                return null;                                  // NOT_READY：SMTC 还没读完封面，对端换新 REQUEST_ID 重试
            }

            NpResourceData? art = ArtworkResourceBuilder.Build(_smtc.CurrentThumbnail, width, height, format, quality, out string? err);
            if (art == null)
            {
                Emit(NpLogLevel.Error, $"封面构造失败：{err}");
            }
            else
            {
                Emit(NpLogLevel.Info, $"生成封面资源：{art.Data.Length} 字节 FORMAT=0x{art.Format:X2} {art.Width}×{art.Height}");
            }
            return art;
        }

        /// <summary>回控落地：交给 app 注入的执行器（app 侧是媒体键，与 Legacy 同一个通道，不重复实现）</summary>
        private void ExecuteLegacyControl(byte cmd)
        {
            _controlExecutor?.Invoke(cmd);
            Emit(NpLogLevel.Info, $"回控：已执行媒体键 0x{cmd:X2}");
        }

        /* ------------------------------------------------------------------ */
        /* 对外动作 / 内部小工具                                                */
        /* ------------------------------------------------------------------ */

        /// <summary>触发一轮延迟测量（规范 §14）</summary>
        public void StartLatencyTest()
        {
            if ((_session?.IsActive != true) || !IsConnected)
            {
                Emit(NpLogLevel.Warn, "延迟测量需要会话已建立：请先连接并等到 SESSION ACTIVE");
                return;
            }

            _latency?.Start();
            Emit(NpLogLevel.Info, "开始延迟测量（30 样本，间隔 100ms）…");
            RaiseStateChanged();
        }

        /// <summary>10ms 心跳：驱动会话层重传 / 超时判定（不在 UI 线程上）</summary>
        private void SafeTick()
        {
            if (_disposed || _tearingDown)
            {
                return;
            }

            try
            {
                _session?.Tick();
            }
            catch
            {
                // 心跳里绝不允许异常逃逸（否则 Timer 会自杀）
            }
        }

        private void Emit(NpLogLevel level, string msg) => Log?.Invoke(level, msg);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wantConnect = false;
            StopReconnectLoop();

            _tickTimer.Dispose();

            _smtc.OnMediaUpdated -= OnMediaUpdated;
            _smtc.MediaCleared -= OnMediaCleared;
            _smtc.PlaybackChanged -= OnPlaybackChanged;
            _smtc.OnThumbnailUpdated -= OnThumbnailUpdated;

            Teardown();
        }

        private void RaiseStateChanged() => StateChanged?.Invoke();
    }
}
