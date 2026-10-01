using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Codec;
using MediaMonitor.Protocol.New.Transport;
using MediaMonitor.Services;
using MediaMonitor.Tools;
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

        public event Action<NpLogLevel, string>? Log;
        public event Action? StateChanged;

        public NewProtocolStack(NewProtocolConfig cfg, SmtcService smtc, LyricService lyrics)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _smtc = smtc ?? throw new ArgumentNullException(nameof(smtc));
            _lyrics = lyrics ?? throw new ArgumentNullException(nameof(lyrics));

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
        /// 会话状态文本 —— 严格区分"链路通"与"对端已应答"，避免"没收到 ACK 就显示已连接"。
        /// </summary>
        public string StateText
        {
            get
            {
                if (!LinkUp)
                {
                    return _wantConnect ? "连接中…（等待链路）" : "未连接";
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

        /// <summary>建立链路并启动握手（异步：TCP 连接可能耗时，不阻塞 UI 线程）</summary>
        public async Task ConnectAsync()
        {
            Disconnect();
            _wantConnect = true;
            RaiseStateChanged();

            if (await OpenTransportAsync().ConfigureAwait(true))
            {
                return;
            }

            // TCP：对端稍后才上线也算正常 → 退避重连；COM：交给用户重按（避免对话框刷屏）
            _wantConnect = _cfg.TransportMode == NewTransportType.Tcp;
            RaiseStateChanged();

            if (_wantConnect)
            {
                _ = ReconnectLoopAsync();
            }
        }

        /// <summary>主动断开（用户操作 / 切模式）：清掉"期望连接"位，避免自动重连回弹</summary>
        public void Disconnect()
        {
            _wantConnect = false;
            Teardown();
            RaiseStateChanged();
        }

        /// <summary>创建并接好传输层 + 全套服务，然后发 HELLO</summary>
        private async Task<bool> OpenTransportAsync()
        {
            INewTransport transport;

            if (_cfg.TransportMode == NewTransportType.Tcp)
            {
                var tcp = new TcpTransport(_cfg.TcpRemoteIp, _cfg.TcpRemotePort);
                Emit(NpLogLevel.Info, $"连接 TCP {_cfg.TcpRemoteIp}:{_cfg.TcpRemotePort} …");
                if (!await tcp.ConnectAsync(3000).ConfigureAwait(true))
                {
                    Emit(NpLogLevel.Error, $"TCP 连接失败：{_cfg.TcpRemoteIp}:{_cfg.TcpRemotePort}（对端未启动？）");
                    return false;
                }
                transport = tcp;
            }
            else
            {
                var com = new ComTransport();
                Emit(NpLogLevel.Info, $"打开串口 {_cfg.ComPortName} @ {_cfg.BaudRate} …");
                if (!com.Connect(_cfg.ComPortName, _cfg.BaudRate, out string? err))
                {
                    Emit(NpLogLevel.Error, $"串口打开失败：{err}");
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

            transport.DataReceived += OnDataReceived;
            transport.Disconnected += OnTransportDisconnected;
            _parser.FrameReceived += OnFrame;
            session.StateChanged += OnSessionStateChanged;
            latency.StatsUpdated += _ => RaiseStateChanged();

            scheduler.Start();
            session.LinkUp();                     // 发 HELLO（§5.4）

            Emit(NpLogLevel.Info, "链路已建立，已发送 HELLO");
            return true;
        }

        /// <summary>TCP 断线退避重连（COM 不做：串口拔插由用户重按）</summary>
        private async Task ReconnectLoopAsync()
        {
            int delay = Math.Clamp(_cfg.TcpReconnectBackoffMs, 200, 10000);

            while (_wantConnect && !_disposed)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (!_wantConnect || _disposed)
                {
                    return;
                }

                Emit(NpLogLevel.Info, $"尝试重连（退避 {delay}ms）…");
                if (await OpenTransportAsync().ConfigureAwait(false))
                {
                    return;
                }

                delay = Math.Min(delay * 2, 10000);
            }
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
                _transport.DataReceived -= OnDataReceived;
                _transport.Disconnected -= OnTransportDisconnected;
            }

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
            bool stillWanted = _wantConnect;

            Teardown();
            RaiseStateChanged();

            if (stillWanted && (_cfg.TransportMode == NewTransportType.Tcp) && !_disposed)
            {
                _ = ReconnectLoopAsync();                     // TCP 自动回来；COM 等用户重按
            }
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
                        // 这条是用户最常遇到的"看起来连上了其实对端不在"的真相：
                        // HELLO 重发用尽仍无 HELLO_ACK → 链路通但会话没建立
                        Emit(NpLogLevel.Error,
                             "握手失败：HELLO 重发用尽仍无 HELLO_ACK —— 对端可能没启动 / 串口被占用 / 波特率不一致。" +
                             "New 模式的对端可用：python MediaMonitor_App/A_tools/mock_esp32_gui.py");
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
            => _thumbEventForTrack = true;                  // 有事件就说明"读过了"，null 才是真的没封面

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

        /// <summary>回控落地：交给既有媒体键执行器（与 Legacy 同一个通道，不重复实现）</summary>
        private void ExecuteLegacyControl(byte cmd)
        {
            MediaKeyInvoker.Instance.EnqueueCommand(cmd);
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
