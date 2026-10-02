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
        /// 当前曲目的封面「原始字节」（SMTC 直出，未做任何缩放/重编码）；null = 无封面或当前无会话。
        ///
        /// <para>本类只负责<b>抓取 + 在内存里存着</b>：要压缩到指定分辨率请用 <c>Tools/ArtworkProcessor</c>，
        /// 将来新协议下发也直接从这里取，不必再动抓取层。</para>
        /// <para>注意：当前的 0xAA/0xAB 协议属于 legacy，<b>不会</b>用到这个字段。</para>
        /// </summary>
        public byte[]? CurrentThumbnail { get; private set; }

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

        // 封面内容摘要（SHA256 十六进制）：SMTC 在同一首歌上会反复发 MediaPropertiesChanged，
        // 用它去重，避免重复解码、重复通知；切会话时连同缓存一起清掉，保证新会话首帧一定生效。
        private string? _thumbHash;
        /// <summary>
        /// 是否已经把"当前曲目有/无封面"这件事通知过上层。
        /// 必须与 `_thumbHash` 一起参与去重：单靠 hash 会把"无封面（null）"的首个事件也挡掉
        /// —— 上层就永远等不到通知，只能一直回 `ACK(NOT_READY)`（实测踩过：mock 重试 10 次放弃）。
        /// </summary>
        private bool _thumbNotified;

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

            // 封面缓存同理（不触发 OnThumbnailUpdated：会话消失按既有约定不下发、不提示）
            CurrentThumbnail = null;
            _thumbHash = null;
            _thumbNotified = false;      // 下次会话的首个封面事件必须能发出去

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

                    // 是否换曲：换曲时封面去重状态要清零 —— 否则"新曲目封面字节与上一首相同"
                    // （同专辑/同封面）会被哈希去重挡掉，上层等不到 OnThumbnailUpdated，
                    // 对端请求封面时只能一直收到 ACK(NOT_READY)（实测踩过）。
                    bool trackChanged = (CurrentTitle != props.Title)
                                     || (CurrentArtist != props.Artist)
                                     || (CurrentAlbum != props.AlbumTitle);

                    CurrentTitle = props.Title; // 赋值
                    CurrentArtist = props.Artist; // 赋值
                    CurrentAlbum = props.AlbumTitle;
                    OnMediaUpdated?.Invoke(props);

                    // 封面走独立异步支线：不阻塞元数据/歌词路径；
                    // 内部自带 try/catch 与 sender/seq 校验，不会产生未观察异常、也不会让旧封面盖新封面。
                    _ = UpdateThumbnailAsync(sender, props.Thumbnail, seq, trackChanged);
                }
            }
            catch (Exception ex)
            {
                // 捕获 COMException (0x80030070) 等，保持程序不崩溃
                System.Diagnostics.Debug.WriteLine($"SMTC 属性获取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取 SMTC 封面原始字节并缓存到 <see cref="CurrentThumbnail"/>（不做缩放/重编码，压缩交给 Tools/ArtworkProcessor）。
        ///
        /// <para>三点防护与既有元数据路径完全同构：</para>
        /// <list type="bullet">
        /// <item>整段 try/catch：会话消亡（播放器退出/切歌瞬间）OpenReadAsync 会抛 COMException(0x80030070)；</item>
        /// <item>await 完成后再校验 sender + seq：切歌/切会话瞬间系统会连发多个事件，
        /// 且完成顺序不保证与触发顺序一致，旧事件的延迟完成必须丢弃；</item>
        /// <item>内容摘要去重：同一首歌反复触发 MediaPropertiesChanged（Chrome 尤甚）时不重复处理、不重复通知。
        /// <b>但去重只针对"同一曲目内的重复事件"</b>：每个曲目的**首个**事件必须发出去（哪怕结果是"无封面"），
        /// 否则上层无法区分"这首没封面"（应按 §11 发空资源）与"还没读出来"（回 ACK(NOT_READY)）。</item>
        /// </list>
        /// </summary>
        private async Task UpdateThumbnailAsync(
            GlobalSystemMediaTransportControlsSession sender,
            IRandomAccessStreamReference? reference,
            long seq,
            bool trackChanged)
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

            // 过期结果：会话已切换，或已有更新的属性事件发出 → 直接丢弃
            if (sender != _currentSession || seq != Volatile.Read(ref _mediaUpdateSeq))
                return;

            if (trackChanged)
            {
                // 换曲：去重状态清零（否则"与上一首封面字节相同"的新曲目会被当成重复事件挡掉）
                _thumbHash = null;
                _thumbNotified = false;
            }

            string? hash = raw == null ? null : Convert.ToHexString(SHA256.HashData(raw));

            // 去重只针对"同一曲目内的重复事件"：_thumbNotified 保证**每个曲目的首个事件一定发出去**
            // —— 哪怕 raw == null（= 这首真的没封面）。少这一条，上层就永远等不到通知，
            // 只能一直回 ACK(NOT_READY)（规范 §11 要求"无封面"用空资源 FORMAT=0x00 表达）。
            if (_thumbNotified && (hash == _thumbHash))
                return;

            _thumbHash = hash;
            _thumbNotified = true;
            CurrentThumbnail = raw;
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
}