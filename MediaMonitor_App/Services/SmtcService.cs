using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaMonitor.Services
{
    /// <summary>
    /// 播放状态（与系统 SMTC 状态对应的强类型枚举）
    /// </summary>
    public enum PlaybackState
    {
        Closed,
        Opened,
        Changing,
        Stopped,
        Playing,
        Paused
    }

    public record MediaProgressInfo(TimeSpan Position, TimeSpan Duration, PlaybackState Status);

    public class SmtcService
    {
        public string? CurrentTitle { get; private set; }
        public string? CurrentArtist { get; private set; }
        public string? CurrentAlbum { get; private set; }

        /// <summary>
        /// 当前曲目的封面「原始字节」（SMTC 直出，未做任何缩放/重编码）；
        /// null = 无封面 / 还没读完 / 当前无会话。
        ///
        /// <para>本类只负责<b>抓取 + 在内存里存着</b>：要压缩到指定分辨率请用 <c>Tools/ArtworkProcessor</c>，
        /// 将来新协议下发也直接从这里取，不必再动抓取层。</para>
        /// <para>注意：当前的 0xAA/0xAB 协议属于 legacy，<b>不会</b>用到这个字段。</para>
        /// </summary>
        public byte[]? CurrentThumbnail => _thumbState.Data;

        /// <summary>当前曲目的封面是否已经读取完成（含"确实没有封面"）。仅供诊断/日志。</summary>
        public bool ThumbnailReadyForCurrentTrack => _thumbState.Ready;

        /// <summary>
        /// 原子读取"当前曲目的封面读取结果"：
        /// <para>true = 已读完；<paramref name="data"/> 为 null 表示**确实没有封面**（应按 §11 回空资源 FORMAT=0x00）；</para>
        /// <para>false = 还没读完（应按 §9.8 回 ACK(NOT_READY)，等对端用新 REQUEST_ID 重试）。</para>
        /// 与换曲共用同一把锁，保证不会出现"就绪=true 但拿到上一首封面"或"刚切歌仍回旧图"。
        /// </summary>
        public bool TryGetThumbnailForCurrentTrack(out byte[]? data) => _thumbState.TryGet(out data);

        // ========== 会话同步（事件 → 去抖 → 重新取快照 → diff） ==========
        // SessionsChanged 只表示"列表变了"，不保证此刻 GetSessions() 已经反映最终结果；
        // 所以这里不直接把它当成新快照，而是 250ms 去抖后重新取，并由 UI 每秒对账兜底。
        private const int SessionSyncDebounceMs = 250;

        private readonly object _sessionGate = new object();
        private readonly Timer _sessionSyncTimer;               // 去抖定时器（构造时创建，Dispose 时释放）
        private int _sessionSyncPending;                        // 0/1：已有一次待执行的同步
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>?
            _sessionsChangedHandler;                            // 保存委托，Dispose 时精确摘除
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        /// <summary>
        /// 最近一次 <c>TimelinePropertiesChanged</c> 拿到的时间轴快照。
        /// <para>SMTC 的 Timeline/Playback 是事件源，两次事件之间 <c>GetTimelineProperties()</c>
        /// 返回的也是同一份锚点；因此 <see cref="GetCurrentProgress"/> 优先读这里，
        /// 避免 10ms/100ms 循环重复发起跨进程调用。</para>
        /// </summary>
        private GlobalSystemMediaTransportControlsSessionTimelineProperties? _lastTimeline;
        private GlobalSystemMediaTransportControlsSessionPlaybackInfo? _lastPlayback;

        /// <summary>进度缓存（Timeline + Playback）的写入时间；超过 1s 由 GetCurrentProgress 兜底刷新一次。</summary>
        private DateTimeOffset _lastProgressCacheAt;

        /// <summary>0/1：并发调用时保证只有一个线程在刷新进度缓存。</summary>
        private int _progressRefreshBusy;

        /// <summary>正在读取/应用媒体属性的事件数；&gt;0 时 PackageMaster 暂停发送旧帧（原子发布闸门）。</summary>
        private int _mediaUpdatePending;

        private const int ProgressFallbackRefreshMs = 1000;
        private IReadOnlyList<GlobalSystemMediaTransportControlsSession> _lastSessions =
            Array.Empty<GlobalSystemMediaTransportControlsSession>();
        private bool _disposed;

        // --- 暂停→恢复播放的锚点（统一由 _sessionGate 保护） ---
        // Chrome 等浏览器在恢复播放瞬间只触发 PlaybackInfoChanged（状态→Playing），
        // 但 Timeline/LastUpdatedTime 仍停留在暂停时刻。若不设锚点，
        // GetCurrentProgress 会按 LastUpdatedTime 外推，把整个暂停时长虚增进首个同步包。
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus _lastStatus =
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        private TimeSpan _resumeBasePosition;
        private DateTimeOffset _resumeBaseTime;
        private bool _resumeAnchorValid;

        // --- 媒体事件排序 / 封面状态 ---
        // 事件序号只用来排序：空属性事件（切歌过渡）不推进"已应用水位"，
        // 旧会话的在途回调在取号之前就被 sender 校验挡掉，因此不会误杀有效读取。
        private readonly MediaUpdateGate _mediaGate = new MediaUpdateGate();

        // 当前曲目的封面状态：曲目切换、读取完成、同曲目去重、数据/就绪的原子切换都在这里。
        // 只保留这一个事实源（历史上 NewProtocolStack 另有一份，导致 NOT_READY 假死）。
        private readonly ThumbnailState _thumbState = new ThumbnailState();

        /// <summary>空属性事件先等一小段时间再清场：避免切歌过渡期的空事件造成无谓闪屏。</summary>
        private const int EmptyMediaGraceMs = 350;

        /// <summary>封面读取失败的有限重试（切歌瞬间 COMException 很常见）。</summary>
        private const int ThumbnailReadRetries = 2;
        private const int ThumbnailRetryDelayMs = 300;

        /// <summary>上层（PackageMaster / NewProtocolStack）当前是否持有媒体：决定要不要发 <see cref="MediaCleared"/>。</summary>
        private bool _mediaNotified;

        public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus>? PlaybackChanged;

        /// <summary>
        /// 当前曲目（Title / Artist / Album）发生变化。
        /// <para>同一首歌反复触发 MediaPropertiesChanged 时**不会**重复触发（避免每次重扫歌词目录、
        /// 重发 MEDIA/时间轴）；封面旁支仍会照常读取，变化时通过 <see cref="OnThumbnailUpdated"/> 通知。</para>
        /// </summary>
        public event Action<GlobalSystemMediaTransportControlsSessionMediaProperties>? OnMediaUpdated;

        /// <summary>
        /// 当前曲目的封面发生变化：参数 null 表示「本曲没有封面」。
        ///
        /// <para>只在 <see cref="ReadMediaPropertiesAsync"/> 成功读到媒体属性后触发；
        /// 会话消失（<see cref="DetachCurrentSessionLocked"/>）<b>不走这里</b> —— 上层由 <see cref="MediaCleared"/>
        /// 同步清掉自己的封面缓存，以保持"会话消失不下发任何清场数据"的既有约定。</para>
        /// <para>同一首歌反复触发 MediaPropertiesChanged 时内容摘要不变，因此不会重复触发。</para>
        /// </summary>
        public event Action<byte[]?>? OnThumbnailUpdated;

        public event Action? SessionsListChanged;

        /// <summary>
        /// 当前会话消失（播放器退出/会话被关闭）、选中了空会话、或系统明确报告"没有媒体"时触发：
        /// 此时 CurrentTitle/Artist/Album 已被清空，上层应同步清掉自己的残留状态。
        /// </summary>
        public event Action? MediaCleared;

        public SmtcService()
        {
            _sessionSyncTimer = new Timer(_ => ReconcileSessions(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>当前选中的会话（仅供 UI 做"始终保留当前项"的判断；没有则为 null）。</summary>
        public GlobalSystemMediaTransportControlsSession? CurrentSession => Volatile.Read(ref _currentSession);

        /// <summary>
        /// 是否有媒体属性更新正在读取/应用。
        /// <para>为 true 时上层（Legacy PackageMaster）应暂停发送旧帧：
        /// 避免切换会话/切歌瞬间先发出 seek 包或旧账本差分歌词，等新元数据/歌词就绪后再发。</para>
        /// </summary>
        public bool MediaUpdatePending => Volatile.Read(ref _mediaUpdatePending) > 0;

        public async Task InitializeAsync()
        {
            if (_disposed || (_manager != null))
            {
                return;                                    // 幂等：重复调用不会重复订阅
            }

            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed)
            {
                return;
            }

            _manager = manager;
            _sessionsChangedHandler = (_, _) => ScheduleSessionSync();
            manager.SessionsChanged += _sessionsChangedHandler;

            ReconcileSessions();                           // 初始快照：UI 构造时可以直接拿到
        }

        /// <summary>
        /// 当前会话快照（最近一次 <see cref="ReconcileSessions"/> 的结果）。
        /// UI 直接读缓存，避免在 UI 线程反复做跨进程调用；快照变化时由 <see cref="SessionsListChanged"/> 通知。
        /// </summary>
        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
            => Volatile.Read(ref _lastSessions);

        /// <summary>
        /// 重新核对系统会话列表：重新取快照 → diff → 处理"当前会话已消失" → 必要时通知 UI。
        ///
        /// <para>为什么需要它：<c>SessionsChanged</c> 回调时刻的 <c>GetSessions()</c> 可能还没反映增删，
        /// 而 WinRT 集合本身不通知变化。仅靠事件会偶发"关闭后残留 / 新开不显示"，
        /// 因此这里既用于事件去抖后的复查，也由 UI 每秒调用一次做兜底。</para>
        ///
        /// <para>幂等、可跨线程调用；只有快照真的变化时才触发 <see cref="SessionsListChanged"/>。</para>
        /// </summary>
        public void ReconcileSessions()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions =
                    _manager is { } manager
                        ? manager.GetSessions()
                        : (IReadOnlyList<GlobalSystemMediaTransportControlsSession>)
                          Array.Empty<GlobalSystemMediaTransportControlsSession>();

                bool changed;
                bool notifyCleared = false;
                GlobalSystemMediaTransportControlsSession? migratedTo = null;

                lock (_sessionGate)
                {
                    changed = !SessionListsEqual(_lastSessions, sessions);
                    if (changed)
                    {
                        Volatile.Write(ref _lastSessions, sessions);
                    }

                    if ((_currentSession != null) && !ContainsSession(sessions, _currentSession))
                    {
                        // 播放器每次切歌都可能新建会话对象：同 AUMID 的新会话视为“同一媒体源重建”，
                        // 平滑迁移、保留旧标题/封面，避免 MediaCleared 清屏与 UI 选择跳变。
                        var replacement = FindReplacementSession(sessions, _currentSession);

                        if ((replacement != null) && BindSession(replacement))
                        {
                            var old = _currentSession;
                            UnbindSession(old);

                            _currentSession = replacement;
                            _mediaGate.InvalidateAll();
                            _resumeAnchorValid = false;

                            // 进度缓存切到新会话；标题/封面保留到新属性读出后再统一应用
                            try
                            {
                                var timeline = replacement.GetTimelineProperties();
                                var playback = replacement.GetPlaybackInfo();
                                StoreProgressCacheLocked(replacement, timeline, playback, updateAnchor: false);
                            }
                            catch (Exception ex)
                            {
                                _lastTimeline = null;
                                _lastPlayback = null;
                                _lastProgressCacheAt = DateTimeOffset.MinValue;
                                System.Diagnostics.Debug.WriteLine($"[SMTC] 迁移会话的初始进度读取失败: {ex.Message}");
                            }

                            migratedTo = replacement;
                        }
                        else
                        {
                            // 没有同 AUMID 的替代会话 → 播放器真的退出了
                            notifyCleared = DetachCurrentSessionLocked(notify: true);
                        }
                    }
                }

                if (migratedTo != null)
                {
                    // 先进入 pending 闸门，再通知列表/上层：避免切换后的第一拍被当成 Seek。
                    // 不 immediate：属性读失败时保留旧内容，等下一次事件。
                    StartMediaRead(migratedTo, immediate: false);
                }

                if (notifyCleared)
                {
                    SafeInvoke(MediaCleared);
                }

                if (changed)
                {
                    SafeInvoke(SessionsListChanged);
                }
            }
            catch (Exception ex)
            {
                // 会话服务偶尔会在播放器退出瞬间抛 COMException；本轮跳过，下一轮对账会补上
                System.Diagnostics.Debug.WriteLine($"[SMTC] 会话列表同步失败: {ex.Message}");
            }
        }

        /// <summary>该会话当前是否处于 Closed（无媒体）状态；查询失败按"未关闭"处理，避免误隐藏。</summary>
        public bool IsSessionClosed(GlobalSystemMediaTransportControlsSession? session)
            => GetSessionPlaybackStatus(session) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

        /// <summary>读取会话播放状态；失败返回 null（调用方按未知处理）。</summary>
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus? GetSessionPlaybackStatus(
            GlobalSystemMediaTransportControlsSession? session)
        {
            if (session == null)
            {
                return null;
            }

            try
            {
                return session.GetPlaybackInfo().PlaybackStatus;
            }
            catch
            {
                return null;
            }
        }

        private void ScheduleSessionSync()
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.Exchange(ref _sessionSyncPending, 1) != 0)
            {
                return;                                    // 已经排了一次，等它执行
            }

            try
            {
                _sessionSyncTimer.Change(SessionSyncDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 已释放：忽略
            }
        }

        private static bool SessionListsEqual(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> a,
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!Equals(a[i], b[i]))
                {
                    return false;                          // WinRT 投影的 Equals = 原生指针身份（已核对）
                }
            }
            return true;
        }

        private static bool ContainsSession(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession session)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (Equals(sessions[i], session))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 找同 AUMID 的替代会话（播放器切歌时常见的“旧对象销毁 + 新对象创建”）。
        /// 优先返回正在播放的，其次返回第一个同 AUMID 的。
        /// </summary>
        private static GlobalSystemMediaTransportControlsSession? FindReplacementSession(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession current)
        {
            string aumid = current.SourceAppUserModelId;
            if (string.IsNullOrEmpty(aumid))
            {
                return null;
            }

            GlobalSystemMediaTransportControlsSession? first = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                var candidate = sessions[i];
                if (!string.Equals(candidate.SourceAppUserModelId, aumid, StringComparison.Ordinal))
                {
                    continue;
                }

                first ??= candidate;

                try
                {
                    if (candidate.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // 查询失败就按普通候选处理
                }
            }

            return first;
        }

        /// <summary>
        /// 解绑并丢弃当前会话（调用方必须持有 <see cref="_sessionGate"/>）。
        /// 返回 true = 上层仍持有媒体、需要补发 <see cref="MediaCleared"/>。
        /// </summary>
        private bool DetachCurrentSessionLocked(bool notify)
        {
            GlobalSystemMediaTransportControlsSession? session = _currentSession;
            if (session == null)
            {
                return false;
            }

            UnbindSession(session);

            // 丢弃会话：让旧会话所有在途的媒体读取全部失效
            _mediaGate.InvalidateAll();

            _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            _resumeAnchorValid = false;
            _currentSession = null;
            _lastTimeline = null;
            _lastPlayback = null;
            _lastProgressCacheAt = default;

            CurrentTitle = null;
            CurrentArtist = null;
            CurrentAlbum = null;
            _thumbState.Reset();

            bool shouldNotify = notify && _mediaNotified;
            if (shouldNotify)
            {
                _mediaNotified = false;
            }
            return shouldNotify;
        }

        private bool BindSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 绑定会话失败: {ex.Message}");
                UnbindSession(session);
                return false;
            }
        }

        private void UnbindSession(GlobalSystemMediaTransportControlsSession session)
        {
            try { session.MediaPropertiesChanged -= Session_MediaPropertiesChanged; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SMTC] 解绑媒体事件失败: {ex.Message}"); }

            try { session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SMTC] 解绑时间轴事件失败: {ex.Message}"); }

            try { session.PlaybackInfoChanged -= Session_PlaybackInfoChanged; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SMTC] 解绑播放事件失败: {ex.Message}"); }
        }

        /// <summary>写入进度缓存（调用方必须持有 <see cref="_sessionGate"/>）。</summary>
        private void StoreProgressCacheLocked(
            GlobalSystemMediaTransportControlsSession session,
            GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback,
            bool updateAnchor)
        {
            if (!ReferenceEquals(session, _currentSession))
            {
                return;
            }

            if (playback != null)
            {
                var status = playback.PlaybackStatus;

                if (updateAnchor &&
                    (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) &&
                    (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
                {
                    _resumeBasePosition = timeline?.Position
                                       ?? _lastTimeline?.Position
                                       ?? TimeSpan.Zero;
                    _resumeBaseTime = DateTimeOffset.Now;
                    _resumeAnchorValid = true;
                }

                _lastStatus = status;
                _lastPlayback = playback;
            }

            if (timeline != null)
            {
                _lastTimeline = timeline;
            }

            _lastProgressCacheAt = DateTimeOffset.Now;
        }

        /// <summary>
        /// 缓存超过 1 秒（或缺失）时兜底刷新一次。
        ///
        /// <para>正常进度由 <c>TimelinePropertiesChanged</c> / <c>PlaybackInfoChanged</c> 更新；
        /// 这里只处理"播放器不发事件 / 漏事件"的少数情况，最多约 2 次 COM/秒，
        /// 代替原来 10ms/100ms 循环里的 200～400 次/秒。</para>
        /// </summary>
        private void RefreshProgressCacheIfStale(GlobalSystemMediaTransportControlsSession session)
        {
            DateTimeOffset at;
            bool missing;

            lock (_sessionGate)
            {
                if (!ReferenceEquals(session, _currentSession))
                {
                    return;
                }

                at = _lastProgressCacheAt;
                missing = (_lastTimeline == null) || (_lastPlayback == null);
            }

            if (!missing && ((DateTimeOffset.Now - at).TotalMilliseconds < ProgressFallbackRefreshMs))
            {
                return;
            }

            if (Interlocked.Exchange(ref _progressRefreshBusy, 1) != 0)
            {
                return;                       // 已有线程在刷新，本次直接用旧缓存
            }

            try
            {
                var timeline = session.GetTimelineProperties();
                var playback = session.GetPlaybackInfo();

                lock (_sessionGate)
                {
                    StoreProgressCacheLocked(session, timeline, playback, updateAnchor: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 进度缓存刷新失败: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _progressRefreshBusy, 0);
            }
        }

        ///
        /// <para>新会话如果本身就是"空会话"（没有标题），读取结果会按实际状态清场并补发
        /// <see cref="MediaCleared"/>，不会继续显示上一首；读取失败也同样按"无媒体"处理，
        /// 避免切换后残留旧内容。</para>
        /// </summary>
        public void SelectSession(GlobalSystemMediaTransportControlsSession? session)
        {
            bool notifyCleared = false;
            bool bound = false;

            lock (_sessionGate)
            {
                DetachCurrentSessionLocked(notify: false);

                _currentSession = session;
                if (session != null)
                {
                    bound = BindSession(session);
                    if (!bound)
                    {
                        _currentSession = null;
                    }
                }

                if (bound && (session != null))
                {
                    // 初始进度锚点：之后由 Timeline/Playback 事件更新，GetCurrentProgress 只读缓存
                    try
                    {
                        var timeline = session.GetTimelineProperties();
                        var playback = session.GetPlaybackInfo();
                        StoreProgressCacheLocked(session, timeline, playback, updateAnchor: false);
                    }
                    catch (Exception ex)
                    {
                        _lastTimeline = null;
                        _lastPlayback = null;
                        _lastProgressCacheAt = DateTimeOffset.MinValue;
                        System.Diagnostics.Debug.WriteLine($"[SMTC] 读取初始进度失败: {ex.Message}");
                    }
                }

                if (_currentSession == null)
                {
                    // 清空选择 / 绑定失败：上层仍持有旧媒体就必须清掉
                    notifyCleared = _mediaNotified;
                    if (notifyCleared)
                    {
                        _mediaNotified = false;
                    }
                }
            }

            if (notifyCleared)
            {
                SafeInvoke(MediaCleared);
            }

            if (bound && (session != null))
            {
                // 关键：强制在线程池上开始读取。SelectSession 由 UI 线程调用，
                // 若直接 await WinRT 操作会捕获 WPF 同步上下文，导致歌词目录扫描 / 封面解码跑在 UI 线程。
                StartMediaRead(session, immediate: true);
            }
        }

        // ========== SMTC 直控方法（MediaKeyInvoker 主用路径，不依赖前台窗口/权限） ==========

        /// <summary>
        /// 播放/暂停：直接调用系统 SMTC 会话，绕过 keybd_event 注入的环境限制。
        /// 返回 false = 没有会话 / 系统拒绝 / 抛异常，调用方可回退到键盘注入。
        /// </summary>
        public async Task<bool> PlayPauseAsync()
        {
            var session = Volatile.Read(ref _currentSession);
            if (session == null)
            {
                return false;
            }

            try
            {
                bool ok = await session.TryTogglePlayPauseAsync();
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[SMTC] 播放/暂停请求被系统拒绝");
                }
                return ok;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 播放/暂停失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>下一曲：直接调用系统 SMTC 会话；返回 false 表示没下发成功。</summary>
        public async Task<bool> NextAsync()
        {
            var session = Volatile.Read(ref _currentSession);
            if (session == null)
            {
                return false;
            }

            try
            {
                bool ok = await session.TrySkipNextAsync();
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[SMTC] 下一曲请求被系统拒绝");
                }
                return ok;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 下一曲失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>上一曲：直接调用系统 SMTC 会话；返回 false 表示没下发成功。</summary>
        public async Task<bool> PrevAsync()
        {
            var session = Volatile.Read(ref _currentSession);
            if (session == null)
            {
                return false;
            }

            try
            {
                bool ok = await session.TrySkipPreviousAsync();
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[SMTC] 上一曲请求被系统拒绝");
                }
                return ok;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 上一曲失败: {ex.Message}");
                return false;
            }
        }

        private void Session_TimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                if (!ReferenceEquals(sender, Volatile.Read(ref _currentSession)))
                {
                    return;                       // 旧会话的在途回调
                }

                var timeline = sender.GetTimelineProperties();

                lock (_sessionGate)
                {
                    StoreProgressCacheLocked(sender, timeline, null, updateAnchor: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 时间轴读取失败: {ex.Message}");
            }
        }

        private void Session_PlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                if (!ReferenceEquals(sender, Volatile.Read(ref _currentSession)))
                {
                    return;                       // 旧会话的在途回调：不允许污染新会话的状态
                }

                var playback = sender.GetPlaybackInfo();
                var status = playback.PlaybackStatus;

                // 检测"非播放 → 播放"转换：暂停/停止后恢复（含切歌后首播）。
                // 用事件触发瞬间的 Position + 墙钟建立恢复锚点，
                // 避免用陈旧的 LastUpdatedTime 外推导致首个进度包虚增整个暂停时长。
                GlobalSystemMediaTransportControlsSessionTimelineProperties? anchorTimeline = null;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    try
                    {
                        anchorTimeline = sender.GetTimelineProperties();
                    }
                    catch { /* 取不到就用缓存里的位置，下一次时间轴刷新会纠正 */ }
                }

                lock (_sessionGate)
                {
                    if (!ReferenceEquals(sender, _currentSession))
                    {
                        return;
                    }

                    StoreProgressCacheLocked(sender, anchorTimeline, playback, updateAnchor: true);
                }

                SafeInvoke(() => PlaybackChanged?.Invoke(status));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 播放状态读取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 媒体属性变化：统一走 <see cref="ReadMediaPropertiesAsync"/>。
        /// <para>用 <see cref="Task.Run(Action)"/> 强制在线程池上开始：即使事件被投递到 UI 线程，
        /// 后续的属性读取 / 歌词重载 / 封面解码也不会捕获 WPF 同步上下文（否则整个歌词目录扫描会卡 UI）。</para>
        /// 这里不是 async void，未观察异常由方法内部消化。
        /// </summary>
        private void Session_MediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs? args)
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _currentSession)))
            {
                return;                       // 旧会话的在途事件：不进入 pending 闸门
            }

            StartMediaRead(sender, immediate: args is null);
        }

        /// <summary>
        /// 启动一次媒体属性读取，并在读取+应用（含歌词闸门）期间把 <see cref="MediaUpdatePending"/> 置位。
        /// </summary>
        private void StartMediaRead(GlobalSystemMediaTransportControlsSession session, bool immediate)
        {
            Interlocked.Increment(ref _mediaUpdatePending);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ReadMediaPropertiesAsync(session, immediate).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _mediaUpdatePending);
                }
            });
        }

        private async Task ReadMediaPropertiesAsync(
            GlobalSystemMediaTransportControlsSession sender, bool immediate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                long seq;
                lock (_sessionGate)
                {
                    if (!ReferenceEquals(sender, _currentSession))
                    {
                        return;                   // 先判 sender：旧会话的事件不参与序号，避免误杀新读取
                    }
                    seq = _mediaGate.Begin();
                }

                GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                bool readOk = true;
                try
                {
                    props = await sender.TryGetMediaPropertiesAsync();
                }
                catch (Exception ex)
                {
                    readOk = false;
                    System.Diagnostics.Debug.WriteLine($"[SMTC] 属性获取失败: {ex.Message}");
                }

                if (!ReferenceEquals(sender, Volatile.Read(ref _currentSession)))
                {
                    return;
                }
                if (_disposed)
                {
                    return;
                }

                if ((props == null) || string.IsNullOrEmpty(props.Title))
                {
                    if (!readOk && !immediate)
                    {
                        return;                   // 事件驱动读取失败：保持现状，等下一次事件
                    }

                    if (immediate)
                    {
                        // 用户主动切换到"空会话"：按实际状态立即清场（不再显示上一首）
                        ClearMedia(seq, notify: true);
                    }
                    else
                    {
                        await ClearMediaAfterGraceAsync(sender, seq).ConfigureAwait(false);
                    }
                    return;
                }

                ApplyMedia(sender, props, seq);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 媒体属性处理失败: {ex.Message}");
            }
        }

        private void ApplyMedia(
            GlobalSystemMediaTransportControlsSession sender,
            GlobalSystemMediaTransportControlsSessionMediaProperties props,
            long seq)
        {
            bool trackChanged;

            lock (_sessionGate)
            {
                if (!ReferenceEquals(sender, _currentSession))
                {
                    return;
                }
                if (!_mediaGate.TryApply(seq))
                {
                    return;                       // 已有更新的事件应用过，或序号过期
                }

                trackChanged = (CurrentTitle != props.Title)
                            || (CurrentArtist != props.Artist)
                            || (CurrentAlbum != props.AlbumTitle);

                if (trackChanged)
                {
                    _thumbState.Reset();
                }

                CurrentTitle = props.Title;
                CurrentArtist = props.Artist;
                CurrentAlbum = props.AlbumTitle;
                _mediaNotified = true;

                // 新曲目：旧时间轴通常属于上一首，强制下一次 GetCurrentProgress 立即重读一次
                _lastProgressCacheAt = DateTimeOffset.MinValue;
            }

            if (trackChanged)
            {
                SafeInvoke(() => OnMediaUpdated?.Invoke(props));
            }
            _ = UpdateThumbnailAsync(sender, props.Thumbnail, seq);
        }

        private async Task ClearMediaAfterGraceAsync(
            GlobalSystemMediaTransportControlsSession sender, long seq)
        {
            await Task.Delay(EmptyMediaGraceMs).ConfigureAwait(false);

            if (!ReferenceEquals(sender, Volatile.Read(ref _currentSession)))
            {
                return;
            }
            if (_mediaGate.Applied > seq)
            {
                return;                           // 期间有更新的非空事件应用过：不是"空会话"
            }

            ClearMedia(seq, notify: true);
        }

        private void ClearMedia(long throughSeq, bool notify)
        {
            bool notifyCleared;

            lock (_sessionGate)
            {
                if (!_mediaGate.TryClearThrough(throughSeq))
                {
                    return;                       // 期间已有更新事件应用，不能清掉新内容
                }

                CurrentTitle = null;
                CurrentArtist = null;
                CurrentAlbum = null;
                _thumbState.Reset();

                notifyCleared = notify && _mediaNotified;
                if (notifyCleared)
                {
                    _mediaNotified = false;
                }
            }

            if (notifyCleared)
            {
                SafeInvoke(MediaCleared);
            }
        }

        /// <summary>
        /// 读取 SMTC 封面原始字节并写入当前曲目的封面状态（不做缩放/重编码，压缩交给 Tools/ArtworkProcessor）。
        ///
        /// <para>结果分三类：</para>
        /// <list type="bullet">
        /// <item><c>reference == null</c> / 流为空 → **确定没有封面**（§11 空资源）；</item>
        /// <item>读取成功 → 正常字节；</item>
        /// <item>读取抛异常（切歌瞬间 COMException 很常见）→ **暂时读不到**：做有限重试，
        /// 仍失败则保持 not-ready（§9.8 的 NOT_READY），不会谎报"本曲无封面"。</item>
        /// </list>
        ///
        /// <para>状态写入仍在锁内校验 sender + 事件水位，切歌/切会话后旧结果直接丢弃。</para>
        /// </summary>
        private async Task UpdateThumbnailAsync(
            GlobalSystemMediaTransportControlsSession sender,
            IRandomAccessStreamReference? reference,
            long seq,
            int attempt = 0)
        {
            byte[]? raw = null;
            bool definitive = true;

            try
            {
                if (reference != null)
                {
                    using IRandomAccessStreamWithContentType stream =
                        await reference.OpenReadAsync();

                    if ((stream != null) && (stream.Size > 0))
                    {
                        // AsStreamForRead 来自 System.IO.WindowsRuntimeStreamExtensions（Windows SDK 投影自带，无需额外 NuGet 包）
                        using Stream net = stream.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await net.CopyToAsync(ms).ConfigureAwait(false);
                        raw = (ms.Length == 0) ? null : ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                definitive = false;
                System.Diagnostics.Debug.WriteLine($"[SMTC] 封面读取失败（第 {attempt + 1} 次）: {ex.Message}");
            }

            if (!definitive)
            {
                if (attempt < ThumbnailReadRetries)
                {
                    await Task.Delay(ThumbnailRetryDelayMs).ConfigureAwait(false);
                    if (ReferenceEquals(sender, Volatile.Read(ref _currentSession)) && _mediaGate.IsLatest(seq))
                    {
                        await UpdateThumbnailAsync(sender, reference, seq, attempt + 1).ConfigureAwait(false);
                    }
                }
                // 重试用尽：保持"还没读完"（资源层继续回 NOT_READY），等下一次媒体事件再试
                return;
            }

            string? hash = raw == null ? null : Convert.ToHexString(SHA256.HashData(raw));

            // 在状态锁内再次校验 sender/seq：避免"读取期间刚好切歌"时把旧曲目封面
            // 写成新曲目的就绪状态。同曲目同字节只通知一次；换曲后的首个结果一定通知。
            bool accepted = _thumbState.TrySetIfCurrent(
                () => ReferenceEquals(sender, Volatile.Read(ref _currentSession)) && _mediaGate.IsLatest(seq),
                raw, hash, out bool notify);

            if (!accepted)
            {
                return;   // 过期结果：会话已切换 / 已有更新的属性事件应用
            }

            if (notify)
            {
                SafeInvoke(() => OnThumbnailUpdated?.Invoke(raw));
            }
        }

        /// <summary>
        /// 将系统 SMTC 播放状态映射为强类型枚举
        /// </summary>
        private static PlaybackState MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            switch (status)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed:
                    return PlaybackState.Closed;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened:
                    return PlaybackState.Opened;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                    return PlaybackState.Changing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    return PlaybackState.Stopped;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    return PlaybackState.Playing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return PlaybackState.Paused;
                default:
                    return PlaybackState.Closed;
            }
        }

        public MediaProgressInfo? GetCurrentProgress()
        {
            var session = Volatile.Read(ref _currentSession);
            if (session == null)
            {
                return null;
            }

            RefreshProgressCacheIfStale(session);

            GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback;

            lock (_sessionGate)
            {
                if (!ReferenceEquals(session, _currentSession))
                {
                    return null;
                }

                timeline = _lastTimeline;
                playback = _lastPlayback;
            }

            if ((timeline == null) || (playback == null))
            {
                return null;
            }

            try
            {
                var status = playback.PlaybackStatus;

                TimeSpan pos = timeline.Position;
                double rate = playback.PlaybackRate ?? 1.0;
                if (rate <= 0)
                {
                    rate = 1.0;
                }

                // Chrome 等浏览器不会像常规播放器那样频繁刷新 SMTC Position/LastUpdatedTime，
                // 旧的 10 秒插值窗口会让进度在播放约 10 秒后停止前进（用户观察到的"卡住"）。
                // 去掉时间窗上限，按 LastUpdatedTime + 播放速率持续外推；
                // 下方 EndTime/0 钳位兜底，确保外推永远不会越过曲目范围。
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    lock (_sessionGate)
                    {
                        if (!ReferenceEquals(session, _currentSession))
                        {
                            return null;           // 读取期间切换了会话：本次结果作废
                        }

                        if (_resumeAnchorValid)
                        {
                            // 刚恢复播放：Timeline/LastUpdatedTime 仍停留在暂停时刻，
                            // 必须从"恢复锚点"（进入 Playing 事件瞬间的 Position + 墙钟）外推，
                            // 否则会把整个暂停时长虚增进第一个进度包。
                            var resumePassed = DateTimeOffset.Now - _resumeBaseTime;
                            if (resumePassed.TotalSeconds >= 0)
                            {
                                pos = _resumeBasePosition + TimeSpan.FromTicks(
                                    (long)(resumePassed.Ticks * rate));
                            }

                            // 系统 Timeline 已刷新到恢复时刻之后 → 恢复正常 LastUpdatedTime 外推
                            if (timeline.LastUpdatedTime >= _resumeBaseTime)
                            {
                                _resumeAnchorValid = false;
                                pos = timeline.Position;
                            }
                        }
                        else
                        {
                            var timePassed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                            if (timePassed.TotalSeconds >= 0)
                            {
                                pos += TimeSpan.FromTicks((long)(timePassed.Ticks * rate));
                            }
                        }
                    }
                }

                // 时长 = EndTime - StartTime。部分直播/网页源不上报时长（EndTime=0），
                // 此时绝不能拿 EndTime 去钳位，否则进度会被永久钉在 0。
                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                if ((duration > TimeSpan.Zero) && (pos > timeline.EndTime))
                {
                    pos = timeline.EndTime;
                }
                if (pos < TimeSpan.Zero)
                {
                    pos = TimeSpan.Zero;
                }

                if (!ReferenceEquals(session, Volatile.Read(ref _currentSession)))
                {
                    return null;                   // 计算期间切换了会话：本次结果作废
                }

                return new MediaProgressInfo(pos, duration, MapPlaybackStatus(status));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>安全触发事件：订阅者抛异常不能打断 SMTC 回调线程/异步支线。</summary>
        private static void SafeInvoke(Action? action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMTC] 事件订阅者异常: {ex.Message}");
            }
        }

        /// <summary>释放：摘除 manager 事件、停止去抖定时器、解绑当前会话（不向上层发清场通知）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try { _sessionSyncTimer.Dispose(); }
            catch { /* 已释放 */ }

            if ((_manager != null) && (_sessionsChangedHandler != null))
            {
                try { _manager.SessionsChanged -= _sessionsChangedHandler; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SMTC] 解绑会话列表事件失败: {ex.Message}"); }
            }
            _manager = null;

            lock (_sessionGate)
            {
                DetachCurrentSessionLocked(notify: false);
            }
        }
    }

    /// <summary>
    /// 当前曲目的封面状态（<see cref="SmtcService"/> 内部使用）：
    /// 统一管理"是否读完 / 原始字节 / 内容摘要"，并用一把锁保证读方看到的
    /// <c>ready</c> 与 <c>data</c> 永远属于同一曲目。
    ///
    /// <para>换曲时 <see cref="Reset"/> 必须**同步**执行（在发送 MEDIA 之前）：
    /// 新曲目封面读取完成前，资源层只能回 <c>ACK(NOT_READY)</c>；
    /// 读完后即使字节与上一首完全相同，也会因 <c>ready=false</c> 而重新通知一次，
    /// 不会被"同曲目同字节"去重挡掉（规范 §9.8-3 / §11.2）。</para>
    /// </summary>
    internal sealed class ThumbnailState
    {
        private readonly object _gate = new object();

        private bool _ready;
        private string? _hash;
        private byte[]? _data;

        public bool Ready
        {
            get { lock (_gate) return _ready; }
        }

        public byte[]? Data
        {
            get { lock (_gate) return _data; }
        }

        /// <summary>换曲 / 会话消失：旧封面立即失效，等待下一次读取结果。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _ready = false;
                _hash = null;
                _data = null;
            }
        }

        /// <summary>原子读取：<c>true</c> = 已读完（<paramref name="data"/> 为 null 表示确实无封面）；<c>false</c> = 还没读完。</summary>
        public bool TryGet(out byte[]? data)
        {
            lock (_gate)
            {
                data = _data;
                return _ready;
            }
        }

        /// <summary>
        /// 写入一次读取结果。字符串 <paramref name="hash"/> 用于"同一曲目内相同字节"去重。
        /// <paramref name="stillCurrent"/> 在锁内执行，调用方在这里校验 sender/seq，
        /// 保证"读取期间切歌"的旧结果不会写成新曲目的就绪状态。
        /// 返回 false = 结果已过期，调用方丢弃；<paramref name="notify"/> = 是否需要通知上层。
        /// </summary>
        public bool TrySetIfCurrent(Func<bool> stillCurrent, byte[]? data, string? hash, out bool notify)
        {
            lock (_gate)
            {
                notify = false;

                if (!stillCurrent())
                    return false;

                if (_ready && string.Equals(hash, _hash, StringComparison.Ordinal))
                    return true;                       // 同曲目同字节：保持就绪，不重复通知

                _data = data;
                _hash = hash;
                _ready = true;
                notify = true;
                return true;
            }
        }
    }

    /// <summary>
    /// 媒体属性事件的排序闸门（纯逻辑，可单元测试）。
    ///
    /// <para><b>为什么不能用"最后发出的序号"判断有效性：</b>切歌过渡期的空属性事件、
    /// 旧会话的在途回调都会取号，但它们<b>不应用</b>任何内容。若用"最新取号"作判据，
    /// 这些事件会把同曲目正在读取的有效属性/封面作废，表现为标题不更新、封面长期 NOT_READY。</para>
    ///
    /// <para><b>规则：</b></para>
    /// <list type="bullet">
    /// <item><see cref="Begin"/>：为一次读取取号（调用方必须先确认 sender 是当前会话）；</item>
    /// <item><see cref="TryApply"/>：只有比"已应用水位"更新的结果才能生效（属性应用、空会话清场）；</item>
    /// <item><see cref="IsLatest"/>：封面等旁支结果只接受与当前水位相同的事件；</item>
    /// <item><see cref="InvalidateAll"/>：切会话/会话消失时让所有在途读取失效。</item>
    /// </list>
    /// </summary>
    internal sealed class MediaUpdateGate
    {
        private long _issued;
        private long _applied;

        /// <summary>已应用（或已清场）的最高事件号。</summary>
        public long Applied => Volatile.Read(ref _applied);

        /// <summary>取一个递增的事件号。</summary>
        public long Begin() => Interlocked.Increment(ref _issued);

        /// <summary>
        /// 尝试把事件号 <paramref name="seq"/> 标记为已应用。
        /// 返回 false = 已有更新的事件应用过（或序号非法），调用方应丢弃这次结果。
        /// </summary>
        public bool TryApply(long seq)
        {
            while (true)
            {
                long current = Volatile.Read(ref _applied);
                if (seq <= current)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _applied, seq, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// 把"清场"视为一次应用：只有当前水位不高于 <paramref name="seq"/> 时才允许清。
        /// 返回 false = 期间已有更新的内容应用过，不能清掉新内容。
        /// </summary>
        public bool TryClearThrough(long seq)
        {
            while (true)
            {
                long current = Volatile.Read(ref _applied);
                if (current > seq)
                {
                    return false;
                }
                if (current == seq)
                {
                    return true;
                }

                if (Interlocked.CompareExchange(ref _applied, seq, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>事件号是否仍是当前已应用水位（封面等旁支结果用它判断是否过期）。</summary>
        public bool IsLatest(long seq) => Volatile.Read(ref _applied) == seq;

        /// <summary>会话切换 / 会话消失：取一个新号并直接推进水位，使所有在途读取失效。</summary>
        public void InvalidateAll()
        {
            long seq = Interlocked.Increment(ref _issued);
            Interlocked.Exchange(ref _applied, seq);
        }
    }
}
