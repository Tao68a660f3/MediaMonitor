using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private GlobalSystemMediaTransportControlsSessionTimelineProperties? _lastTimeline;

        // --- 暂停→恢复播放的锚点 ---
        // Chrome 等浏览器在恢复播放瞬间只触发 PlaybackInfoChanged（状态→Playing），
        // 但 Timeline/LastUpdatedTime 仍停留在暂停时刻。若不设锚点，
        // GetCurrentProgress 会按 LastUpdatedTime 外推，把整个暂停时长虚增进首个同步包。
        // 因此用"进入 Playing 的事件瞬间"作为新外推基准，直到系统 Timeline 刷新到锚点之后。
        private GlobalSystemMediaTransportControlsSessionPlaybackStatus _lastStatus =
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        private TimeSpan _resumeBasePosition;
        private DateTimeOffset _resumeBaseTime;
        private bool _resumeAnchorValid = false;

        // 媒体属性更新序号：切歌/切会话瞬间系统会连发多个 MediaPropertiesChanged，
        // 且 async void 中 await 的完成顺序不保证与触发顺序一致。
        // 只有"最后一次触发的事件"序号最新才允许生效，旧事件的延迟完成直接丢弃。
        private long _mediaUpdateSeq = 0;

        // 当前曲目的封面状态：曲目切换、读取完成、同曲目去重、数据/就绪的原子切换都在这里。
        // 过去 SmtcService 用 _thumbHash/_thumbNotified、NewProtocolStack 再维护一个 _thumbEventForTrack，
        // 两边状态在"切歌 + 同字节封面 + 系统连发多个 MediaPropertiesChanged"时会不同步，
        // 表现为封面其实已经可用，资源层却一直回 ACK(NOT_READY)。现在只保留这一个事实源。
        private readonly ThumbnailState _thumbState = new ThumbnailState();

        public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus>? PlaybackChanged;
        public Action<GlobalSystemMediaTransportControlsSessionMediaProperties>? OnMediaUpdated;

        /// <summary>
        /// 当前曲目的封面发生变化：参数 null 表示「本曲没有封面」。
        ///
        /// <para>只在 <see cref="Session_MediaPropertiesChanged"/> 成功读到媒体属性后触发；
        /// 会话消失（<see cref="DetachCurrentSession"/>）<b>不走这里</b> —— 上层由 <see cref="MediaCleared"/>
        /// 同步清掉自己的封面缓存，以保持"会话消失不下发任何清场数据"的既有约定。</para>
        /// <para>同一首歌反复触发 MediaPropertiesChanged 时内容摘要不变，因此不会重复触发。</para>
        /// </summary>
        public Action<byte[]?>? OnThumbnailUpdated;

        public event Action? SessionsListChanged;

        /// <summary>
        /// 当前会话消失（播放器退出/会话被关闭、或手动清空选择）时触发：
        /// 此时 CurrentTitle/Artist/Album 已被清空，上层应同步清掉自己的残留状态。
        /// </summary>
        public event Action? MediaCleared;

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => {
                DetachIfSessionGone();
                SessionsListChanged?.Invoke();
            };
        }

        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
            => _manager?.GetSessions() ?? new List<GlobalSystemMediaTransportControlsSession>();

        /// <summary>
        /// 当前会话是否已被系统移除（播放器退出/会话关闭）：是则彻底清场并通知上层。
        /// 不清场的话 CurrentTitle/Artist/Album 会一直保留上一首的残留值，
        /// 界面与托盘会"假装还在播放"，而 GetCurrentProgress() 其实已经返回 null。
        /// </summary>
        private void DetachIfSessionGone()
        {
            if (_currentSession == null) return;

            try
            {
                if (_manager?.GetSessions().Contains(_currentSession) == true) return;
            }
            catch { return; }   // 查询失败按"会话还在"处理，避免误清

            DetachCurrentSession(notify: true);
        }

        /// <summary>
        /// 解绑并丢弃当前会话，同时清空缓存的元数据（残留信息的源头就在这里）。
        /// notify = true 时通知上层"已经没有会话了"（MediaCleared）。
        /// </summary>
        private void DetachCurrentSession(bool notify)
        {
            bool hadSession = _currentSession != null;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            // 丢弃会话：使旧会话的所有在途事件全部失效
            Interlocked.Increment(ref _mediaUpdateSeq);

            // 跨会话重置状态，避免用上一个会话的播放状态/锚点误判
            _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            _resumeAnchorValid = false;
            _lastTimeline = null;

            _currentSession = null;

            // 元数据一并清空，否则界面/托盘会继续显示上一首
            CurrentTitle = null;
            CurrentArtist = null;
            CurrentAlbum = null;

            // 封面状态同理：清空数据/就绪/去重（下一次会话的首个封面事件必须能发出去）。
            // 不触发 OnThumbnailUpdated：会话消失按既有约定不下发、不提示。
            _thumbState.Reset();

            if (notify && hadSession)
                MediaCleared?.Invoke();
        }

        public void SelectSession(GlobalSystemMediaTransportControlsSession? session)
        {
            // 切换会话只解绑、不通知（新会话紧接着会立刻推送自己的属性）；
            // 但"清空选择"等于没有会话了，必须通知上层清掉残留的歌曲信息
            bool hadSession = _currentSession != null;

            DetachCurrentSession(notify: false);

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;

                try
                {
                    _lastTimeline = _currentSession.GetTimelineProperties();
                }
                catch { _lastTimeline = null; }

                // 立即触发一次更新
                Session_MediaPropertiesChanged(_currentSession, null);
            }
            else if (hadSession)
            {
                MediaCleared?.Invoke();
            }
        }

        // ========== SMTC 直控方法（MediaKeyInvoker 主用路径，不依赖前台窗口/权限） ==========

        /// <summary>
        /// 播放/暂停：直接调用系统 SMTC 会话，绕过 keybd_event 注入的环境限制
        /// </summary>
        public async Task PlayPauseAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TryTogglePlayPauseAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 播放/暂停失败: {ex.Message}"); }
        }

        /// <summary>
        /// 下一曲：直接调用系统 SMTC 会话
        /// </summary>
        public async Task NextAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipNextAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 下一曲失败: {ex.Message}"); }
        }

        /// <summary>
        /// 上一曲：直接调用系统 SMTC 会话
        /// </summary>
        public async Task PrevAsync()
        {
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipPreviousAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SMTC 上一曲失败: {ex.Message}"); }
        }

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                _lastTimeline = sender.GetTimelineProperties();
            }
            catch { /* SMTC 时间线获取失败时忽略 */ }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var status = sender.GetPlaybackInfo().PlaybackStatus;

                // 检测"非播放 → 播放"转换：暂停/停止后恢复（含切歌后首播）。
                // 用事件触发瞬间的 Position + 墙钟建立恢复锚点，
                // 避免用陈旧的 LastUpdatedTime 外推导致首个进度包虚增整个暂停时长。
                if (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    _resumeBasePosition = sender.GetTimelineProperties().Position;
                    _resumeBaseTime = DateTimeOffset.Now;
                    _resumeAnchorValid = true;
                }
                _lastStatus = status;

                PlaybackChanged?.Invoke(status);
            }
            catch { /* SMTC 播放信息获取失败时忽略 */ }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs? args)
        {
            try
            {
                // 核心修复：防止在切歌或关闭时因 Session 失效导致的 COM 崩溃
                long seq = Interlocked.Increment(ref _mediaUpdateSeq); // 本次事件序号

                var props = await sender.TryGetMediaPropertiesAsync();

                // 只有"最后一次触发的事件"才允许生效：
                // 切歌瞬间系统可能连发多个事件，且 await 完成顺序不保证与触发一致，
                // 旧事件的延迟完成会覆盖新歌词，必须用序号丢弃。
                if (props != null && sender == _currentSession
                    && seq == Volatile.Read(ref _mediaUpdateSeq))
                {
                    // 过滤切歌过渡期的空属性事件（Title 为空），
                    // 防止触发 LoadAndParse 清空已加载歌词、以及发出空的元数据包
                    if (string.IsNullOrEmpty(props.Title))
                        return;

                    // 是否换曲：换曲时**同步**让旧封面失效（ready=false / data=null / 去重清零），
                    // 新曲目的封面读取完成后才重新就绪；即使新旧封面字节完全相同，
                    // 也不能沿用上一首的"已通知/已就绪"状态（规范 §9.8 / §11.2）。
                    bool trackChanged = (CurrentTitle != props.Title)
                                     || (CurrentArtist != props.Artist)
                                     || (CurrentAlbum != props.AlbumTitle);

                    if (trackChanged)
                        _thumbState.Reset();

                    CurrentTitle = props.Title; // 赋值
                    CurrentArtist = props.Artist; // 赋值
                    CurrentAlbum = props.AlbumTitle;
                    OnMediaUpdated?.Invoke(props);

                    // 封面走独立异步支线：不阻塞元数据/歌词路径；
                    // 内部自带 try/catch 与 sender/seq 校验，不会产生未观察异常、也不会让旧封面盖新封面。
                    _ = UpdateThumbnailAsync(sender, props.Thumbnail, seq);
                }
            }
            catch (Exception ex)
            {
                // 捕获 COMException (0x80030070) 等，保持程序不崩溃
                System.Diagnostics.Debug.WriteLine($"SMTC 属性获取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取 SMTC 封面原始字节并写入当前曲目的封面状态（不做缩放/重编码，压缩交给 Tools/ArtworkProcessor）。
        ///
        /// <para>三点防护：</para>
        /// <list type="bullet">
        /// <item>整段 try/catch：会话消亡（播放器退出/切歌瞬间）OpenReadAsync 会抛 COMException(0x80030070)；</item>
        /// <item><b>在状态锁内</b>校验 sender + seq：切歌/切会话瞬间系统会连发多个事件，
        /// 且完成顺序不保证与触发顺序一致，旧事件的延迟完成必须丢弃；</item>
        /// <item>内容摘要去重：同一首歌反复触发 MediaPropertiesChanged（Chrome 尤甚）时不重复通知。
        /// <b>但换曲时 <see cref="ThumbnailState.Reset"/> 已同步清掉就绪标记</b>，所以
        /// "新曲目封面字节与上一首相同"仍会通知（规范 §9.8-3 / §11.2 要求区分"没读出来"与"就是没有"）。</item>
        /// </list>
        /// </summary>
        private async Task UpdateThumbnailAsync(
            GlobalSystemMediaTransportControlsSession sender,
            IRandomAccessStreamReference? reference,
            long seq)
        {
            byte[]? raw = null;

            try
            {
                if (reference != null)
                {
                    using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                    if (stream != null && stream.Size > 0)
                    {
                        // AsStreamForRead 来自 System.IO.WindowsRuntimeStreamExtensions（Windows SDK 投影自带，无需额外 NuGet 包）
                        using Stream net = stream.AsStreamForRead();
                        using var ms = new MemoryStream();
                        await net.CopyToAsync(ms);
                        raw = ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                // 部分播放器（Chrome 网页封面、某些 SMTC 提供方）拿不到缩略图 —— 按"无封面"降级即可
                System.Diagnostics.Debug.WriteLine($"SMTC 封面读取失败: {ex.Message}");
            }

            string? hash = raw == null ? null : Convert.ToHexString(SHA256.HashData(raw));

            // 在状态锁内再次校验 sender/seq：避免"读取期间刚好切歌"时把旧曲目封面
            // 写成新曲目的就绪状态。同曲目同字节只通知一次；换曲后的首个结果一定通知。
            bool accepted = _thumbState.TrySetIfCurrent(
                () => sender == _currentSession && seq == Volatile.Read(ref _mediaUpdateSeq),
                raw, hash, out bool notify);

            if (!accepted)
                return;   // 过期结果：会话已切换 / 已有更新的属性事件发出

            if (notify)
                OnThumbnailUpdated?.Invoke(raw);
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
            if (_currentSession == null) return null;

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                var playback = _currentSession.GetPlaybackInfo();
                var status = playback.PlaybackStatus;

                TimeSpan pos = timeline.Position;

                // Chrome 等浏览器不会像常规播放器那样频繁刷新 SMTC Position/LastUpdatedTime，
                // 旧的 10 秒插值窗口会让进度在播放约 10 秒后停止前进（用户观察到的"卡住"）。
                // 去掉时间窗上限，按 LastUpdatedTime + 播放速率持续外推；
                // 下方 EndTime/0 钳位兜底，确保外推永远不会越过曲目范围。
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    if (_resumeAnchorValid)
                    {
                        // 刚恢复播放：Timeline/LastUpdatedTime 仍停留在暂停时刻，
                        // 必须从"恢复锚点"（进入 Playing 事件瞬间的 Position + 墙钟）外推，
                        // 否则会把整个暂停时长虚增进第一个进度包。
                        var resumePassed = DateTimeOffset.Now - _resumeBaseTime;
                        if (resumePassed.TotalSeconds >= 0)
                        {
                            pos = _resumeBasePosition + TimeSpan.FromTicks(
                                (long)(resumePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
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
                            pos += TimeSpan.FromTicks((long)(timePassed.Ticks * (playback.PlaybackRate ?? 1.0)));
                        }
                    }
                }

                if (pos > timeline.EndTime) pos = timeline.EndTime;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

                return new MediaProgressInfo(
                    pos,
                    timeline.EndTime,
                    MapPlaybackStatus(status)
                );
            }
            catch { return null; }
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
}