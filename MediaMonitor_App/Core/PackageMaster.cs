using MediaMonitor.Services;
using MediaMonitor.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Shapes;
using Windows.Media.Control;

namespace MediaMonitor.Core
{
    public class PackageMaster
    {
        private readonly object _syncLock = new object(); // 定义同步锁

        public event Action<int, LyricLine>? LyricChanged;

        private IMediaTransport _transport;
        private readonly LyricService _lyricService;
        private readonly SmtcService _smtc;

        // --- 核心严谨账本 (HashSet) ---
        private HashSet<string> _syncedSlots = new HashSet<string>();
        private int _lastProcessedCIdx = -2;

        // --- 歌词代际检测（切歌瞬间 Lyrics 原子替换后强制作废旧账本，防止新旧歌词混杂漏发）---
        private int _lastLrcGeneration = -1;

        // --- 同步包调度：使用真实时间戳，避免 Task.Delay 精度不足导致的间隔漂移 ---
        private long _lastSyncTimeMs = 0;

        // --- Seek/进度跳变检测（切歌的大跳变也归入 Seek，立即补发同步包）---
        private double _lastSeenPositionMs = -1;   // 上一帧看到的播放器进度

        // --- 当前绑定的媒体会话（用于识别“切换会话”并跳过第一帧，避免误判 Seek / 发旧歌词）---
        private GlobalSystemMediaTransportControlsSession? _lastSession;

        /// <summary>
        /// 两个会话是否属于同一个媒体源（AUMID 相同）。
        /// 播放器切歌时经常“销毁旧会话对象 + 创建同 AUMID 的新对象”，
        /// 这属于同一媒体源内部迁移，不应清空歌词账本。
        /// </summary>
        private static bool SameSource(
            GlobalSystemMediaTransportControlsSession? a,
            GlobalSystemMediaTransportControlsSession? b)
        {
            try
            {
                string? aumidA = a?.SourceAppUserModelId;
                string? aumidB = b?.SourceAppUserModelId;
                return !string.IsNullOrEmpty(aumidA)
                    && string.Equals(aumidA, aumidB, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        // --- 统计学习变量 ---
        private double _lastSmtcMediaSec = -1;
        private double _lastSmtcWallSec = -1;
        private double _totalSeconds = 0;
        private bool _isPlaying = false;
        private readonly List<double> _wallIntervalSamples = new List<double>();

        // --- 逻辑帧间隔(ms) ---
        private const int _frameInterval = 10;

        public PackageConfig Config { get; private set; } = new PackageConfig();
        private CancellationTokenSource? _loopCts;

        public PackageMaster(IMediaTransport transport, LyricService lyricService, SmtcService smtc)
        {
            _transport = transport;
            _lyricService = lyricService;
            _smtc = smtc;

            // 监听媒体更新（用 +=：OnMediaUpdated 是事件，与 NewProtocolStack 的订阅共存，
            // 构造顺序不再影响彼此 —— 过去用字段赋值，后构造的一方会静默覆盖前面的订阅）
            _smtc.OnMediaUpdated += props =>
            {
                // 过渡期空属性事件不触发加载/发送，避免清空歌词和发出空元数据包（双保险）
                if (string.IsNullOrWhiteSpace(props.Title))
                    return;

                _lyricService.LoadAndParse(props.Title, props.Artist, props.AlbumTitle);
                Invalidate(); // 切歌强制清空账本
                SendMetadata(props.Title, props.Artist, props.AlbumTitle);

                // 切歌后立即发送一次同步包（重置硬件端时间轴）
                var prog = _smtc.GetCurrentProgress();
                if (prog != null)
                {
                    SendSyncPacket(prog.Position.TotalMilliseconds);
                    _lastSeenPositionMs = prog.Position.TotalMilliseconds;
                    _lastSyncTimeMs = Environment.TickCount64;
                }
            };

            // 监听播放状态
            _smtc.PlaybackChanged += status =>
            {
                // 新会话的首个播放状态：此时元数据/新歌词可能还没就绪，
                // 先不发同步包（由 OnMediaUpdated / ProcessTick 重置后发），避免 0x11 抢在 0x10 前面。
                var session = _smtc.CurrentSession;
                if (!ReferenceEquals(session, _lastSession) || _smtc.MediaUpdatePending)
                {
                    return;
                }

                _isPlaying = (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

                // 立即获取当前进度并同步给硬件（单位：毫秒）
                var prog = _smtc.GetCurrentProgress();
                if (prog != null)
                {
                    SendSyncPacket(prog.Position.TotalMilliseconds);

                    // 重置 Seek 基线，防止 Play 瞬间被误判为 Seek 又补一包
                    _lastSeenPositionMs = prog.Position.TotalMilliseconds;

                    // 同时更新上次同步时间戳，避免后台循环紧接着又补发一包造成重复
                    _lastSyncTimeMs = Environment.TickCount64;
                }
            };

            // 会话消失（播放器退出/关闭）：只清上位机（歌词列表 + 通知界面复位），
            // 按约定不向硬件下发任何清场/停止包，硬件保留最后一帧画面。
            _smtc.MediaCleared += () =>
            {
                _isPlaying = false;
                _lastSession = null;
                _lastSeenPositionMs = -1;
                _totalSeconds = 0;
                _lastSmtcMediaSec = -1;
                _lastSmtcWallSec = -1;

                _lyricService.LoadAndParse("", "", "");   // 闸门 1 → 发布空歌词（不会产生占位行）
                Invalidate();                         // 清上位机账本；恢复播放时首帧整屏重发

                // ProcessTick 此时已因 prog == null 停摆，不会自己刷新界面 → 主动清掉歌词预览
                LyricChanged?.Invoke(-1, new LyricLine());
            };
        }

        public void Invalidate()
        {
            lock (_syncLock)
            {
                _syncedSlots.Clear();
                _lastProcessedCIdx = -2;
            }
        }

        public void UpdateConfig(PackageConfig cfg)
        {
            Config = cfg;
            Invalidate();
        }

        public void Start()
        {
            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
            Task.Run(() => BackgroundLoop(_loopCts.Token));
        }

        private async Task BackgroundLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ProcessTick();
                }
                catch { }
                await Task.Delay(_frameInterval, token); // 10ms 逻辑帧周期
            }
        }

        private void ProcessTick()
        {
            var prog = _smtc.GetCurrentProgress();
            if (prog == null)
                return;

            UpdateStatistics(prog);

            double cur = prog.Position.TotalMilliseconds;

            // 切换会话 / 播放器重建会话：第一帧只重置基线，不发任何旧帧。
            // 否则新会话位置相对上一首会跳变 → 被误判为 Seek → 先发同步包/旧账本差分歌词，
            // 之后 OnMediaUpdated 才发出新元数据和新歌词。
            var session = _smtc.CurrentSession;
            if (!ReferenceEquals(session, _lastSession))
            {
                bool sameSource = SameSource(session, _lastSession);

                _lastSession = session;
                _lastSeenPositionMs = cur;
                _lastSyncTimeMs = Environment.TickCount64;

                // 同一个 AUMID 的会话对象重建（播放器切歌常见）：媒体源没变，
                // 屏幕上的歌词账本仍然有效，不能因为对象身份变了就整屏重发。
                // 如果确实是新曲目，ApplyMedia 的 trackChanged=True 会触发 OnMediaUpdated，
                // 由那里负责 LoadAndParse + Invalidate。
                if (!sameSource)
                {
                    Invalidate();
                }
                return;
            }

            // 媒体属性/歌词正在更新（含同一会话内切歌）：不要用旧账本/旧歌词发帧，
            // 等 OnMediaUpdated 把新内容准备好后再发。
            if (_smtc.MediaUpdatePending)
            {
                return;
            }

            // ① 原子捕获歌词快照：整帧内所有歌词读取基于同一实例，
            // 与 LoadAndParse 的局部构建 + 一次性发布配合，保证绝不读到新旧混杂的半成品
            var lrc = _lyricService.Lines;

            // ② 歌词代际检测：Lyrics 被原子替换（切歌/换歌）时强制作废旧帧账本并重发当前行
            if (_lyricService.Generation != _lastLrcGeneration)
            {
                _lastLrcGeneration = _lyricService.Generation;
                Invalidate();
            }

            // 注意：部分播放器会先重置进度并触发本循环，稍后才发 MediaPropertiesChanged。
            // 新歌词代际尚未更新时，本帧可能仍按上一首歌词发送（事件顺序决定的瞬态）；
            // OnMediaUpdated 到达后由 Invalidate 立即纠正。
            int cIdx = lrc.FindLastIndex(l => l.Time <= prog.Position);

            LyricChanged?.Invoke(cIdx, _lyricService.GetLine(cIdx, lrc));

            if (!_transport.IsConnected)
                return;

            // 1. 同步包发送：基于真实时间戳调度 + Seek/切歌即时补发
            long nowMs = Environment.TickCount64;

            // --- Seek 检测：当前位置比上一帧跳变超过 1.5s（支持正跳、回跳及切歌大跳变）---
            bool isSeek = _lastSeenPositionMs >= 0 &&
                          Math.Abs(cur - _lastSeenPositionMs) > 1500;
            _lastSeenPositionMs = cur;

            if (isSeek ||
                nowMs - _lastSyncTimeMs >= Config.SyncIntervalMs)
            {
                SendSyncPacket(cur);
                _lastSyncTimeMs = nowMs;
            }

            // 2. 歌词行变动处理
            // 注意：_lastProcessedCIdx 也是竞争资源，读取和修改它时需要轻量锁
            bool shouldOutput = false;
            bool isJump = false;
            lock (_syncLock)
            {
                if (cIdx != _lastProcessedCIdx)
                {
                    shouldOutput = true;
                    isJump = cIdx < _lastProcessedCIdx;
                    _lastProcessedCIdx = cIdx;
                }
            }

            // 关键：在锁外面执行耗时的发送逻辑
            if (shouldOutput)
            {
                HandleOutput(cIdx, isJump, lrc);
            }
        }

        private void HandleOutput(int cIdx, bool forceRefresh, IReadOnlyList<LyricLine> lrc)
        {
            // 基础校验
            if (!_transport.IsConnected)
                return;

            var targetSlots = new HashSet<string>();
            var dataToSync = new Dictionary<string, (byte[]? AdvData, string RawText)>();

            int currentPhysicalRow = 0;
            int lyricIdx = cIdx - Config.Offset;

            // --- 核心循环修复 ---
            while (currentPhysicalRow < Config.LineLimit)
            {
                var line = _lyricService.GetLine(lyricIdx, lrc);

                // 1. 处理原文槽位 (强制占用 1 个物理行)
                // 普通行已升级为 0x15 增强原文行（带结束时间），账本键同步更新
                string cmdType = (line.Words.Count > 0) ? "0x14" : "0x15";
                string mKey = $"{lyricIdx}_{cmdType}";
                targetSlots.Add(mKey);

                //================

                // 1. 先准备好基础数据，确保内容永远不是 null
                string mText = line?.Content ?? "";
                byte[]? advPackage = null;

                // 2. 根据模式和内容深度，确定“高级包”到底发什么
                if (Config.IsAdvancedMode)
                {
                    if (line?.Words?.Count > 0)
                    {
                        // 逐字模式包
                        advPackage = PackageBuilder.BuildWordByWord((short)lyricIdx, line.Time, line.Words);
                    }
                    else
                    {
                        // 普通行模式包：0x15 增强原文行（带结束时间）
                        // _totalSeconds 来自 SmtcService.GetCurrentProgress().Duration（当前曲目总时长）
                        var endTime = _lyricService.GetEndTime(lyricIdx, lrc, TimeSpan.FromSeconds(_totalSeconds));
                        advPackage = PackageBuilder.BuildEnhancedLyricLine((short)lyricIdx, line.Time, endTime, mText);
                    }
                }
                else
                {
                    // 文本模式（Raw模式）：不需要二进制包，所以显式设为 null
                    // 注意：这里的 null 是安全的，因为发送逻辑会通过后面的 mText 处理
                    advPackage = null;
                }

                // 3. 最后统一塞进字典，账本（Key）和内容（RawText）一个都不能少
                dataToSync[mKey] = (advPackage, mText);

                //================

                currentPhysicalRow++; // 原文占掉一行

                // 2. 处理翻译槽位
                string tText = line.Translation ?? "";
                if (!string.IsNullOrEmpty(tText))
                {
                    // 只有当【不占行】或者【占行且当前物理行未满】时，才处理翻译
                    if (!Config.TransOccupies || currentPhysicalRow < Config.LineLimit)
                    {
                        string tKey = $"{lyricIdx}_0x13";
                        targetSlots.Add(tKey);
                        dataToSync[tKey] = (Config.IsAdvancedMode
                            ? PackageBuilder.BuildTranslationLine((short)lyricIdx, line.Time, tText)
                            : null, tText);

                        if (Config.TransOccupies)
                        {
                            currentPhysicalRow++; // 翻译占掉下一行
                        }
                    }
                }

                lyricIdx++;

                // 兜底安全跳出，防止在极端配置下死循环
                if (lyricIdx > lrc.Count + Config.LineLimit)
                    break;
            }

            // --- 差分判定逻辑 ---
            List<string> toNotify;
            lock (_syncLock)
            {
                // 如果是增量模式且非强制刷新，则只发送账本中不存在的新槽位
                toNotify = (Config.IsIncremental && !forceRefresh)
                            ? targetSlots.Except(_syncedSlots).ToList()
                            : targetSlots.ToList();
            }

            // --- 执行发送 ---
            foreach (var slot in toNotify)
            {
                if (!dataToSync.TryGetValue(slot, out var pack))
                    continue;

                if (Config.IsAdvancedMode)
                {
                    if (pack.AdvData != null)
                    {
                        _transport.Send(pack.AdvData);
                    }
                }
                else
                {
                    // 文本模式退化逻辑
                    byte[] raw = Config.Encoding.GetBytes(pack.RawText + "\n");
                    _transport.Send(raw);
                }
            }

            // --- 更新账本 ---
            lock (_syncLock)
            {
                // 这一步至关重要：同步后，上位机账本必须与当前屏幕视野（targetSlots）完全一致
                _syncedSlots = targetSlots;
            }
        }

        private void UpdateStatistics(MediaProgressInfo info)
        {
            double nowWall = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
            if (info.Status == PlaybackState.Playing && _lastSmtcWallSec > 0)
            {
                double deltaWall = nowWall - _lastSmtcWallSec;
                double deltaMedia = info.Position.TotalSeconds - _lastSmtcMediaSec;

                // 采样节奏：过滤暂停与 Seek
                if (Math.Abs(deltaMedia - deltaWall) < 0.2)
                {
                    _wallIntervalSamples.Add(deltaWall);
                    if (_wallIntervalSamples.Count > 10)
                        _wallIntervalSamples.RemoveAt(0);
                }

                // 账本自己会决定重发，不需要这个
                //// Seek 判定：超过 2 秒偏差则清空账本重刷
                //else if (Math.Abs(deltaMedia) > 2.0 || deltaMedia < 0)
                //{
                //    Invalidate();
                //}
            }
            _lastSmtcMediaSec = info.Position.TotalSeconds;
            _lastSmtcWallSec = nowWall;

            // 总时长只影响“最后一行普通歌词(0x15)”的结束时间；
            // 只有它已经发送过（在账本里）时才需要清账本重发。
            double newTotalSeconds = info.Duration.TotalSeconds;
            if (Math.Abs(newTotalSeconds - _totalSeconds) >= 1.0)
            {
                InvalidateLedgerIfLastLineEndTimeChanged();
            }
            _totalSeconds = newTotalSeconds;

            _isPlaying = (info.Status == PlaybackState.Playing);
        }

        /// <summary>
        /// 曲目总时长只影响“最后一行普通歌词（0x15）”的结束时间；
        /// 只有它已经发送过（在账本里）时才需要修正。
        ///
        /// <para>屏幕是“收到一行新的就丢掉一行旧的”的滚动窗口，不能只补发这一行来改内容；
        /// 账本一旦需要变化就清空整屏重发（这是允许的）。
        /// 最后一行是逐字行(0x14)时不依赖总时长，什么都不做。</para>
        /// </summary>
        private void InvalidateLedgerIfLastLineEndTimeChanged()
        {
            if (!Config.IsAdvancedMode)
            {
                return;                         // 文本模式没有 0x15 结束时间
            }

            var lines = _lyricService.Lines;
            if (lines.Count == 0)
            {
                return;
            }

            int last = lines.Count - 1;
            if (lines[last].Words.Count > 0)
            {
                return;                         // 最后一行是 0x14，没有结束时间
            }

            bool sent;
            lock (_syncLock)
            {
                sent = _syncedSlots.Contains($"{last}_0x15");
            }

            if (sent)
            {
                Invalidate();                   // 清账本 + _lastProcessedCIdx=-2，整屏重发
            }
        }

        private void SendSyncPacket(double currentMs)
        {
            if (!_transport.IsConnected || !Config.IsAdvancedMode)
                return;

            uint safeMs;

            // 零点保护：真实进度落在 0 时不加偏移，直接发真实进度。
            // 正偏移会在该区间制造"假进度"（0→10ms），负偏移会因钳位丢失进度细节（50ms→0ms），
            // 都会破坏硬件端"0ms = 起点锚点"的语义。
            if (currentMs == 0)
            {
                safeMs = (uint)Math.Max(0, currentMs);
            }
            else
            {
                // 只有在播放时才应用偏移；暂停/停止时透传真实进度，避免硬件端位置漂移
                double offset = _isPlaying ? Config.SyncCurrentOffsetMs : 0;
                // 超出偏移区间后才应用偏移 + 下限保护（防负偏移导致 uint 溢出成巨数）
                safeMs = (uint)Math.Max(0, currentMs + offset);
            }

            // 构造并发送同步包（单位：毫秒，直接透传不再转换）
            var p = PackageBuilder.BuildSync(_isPlaying, safeMs, (uint)(_totalSeconds * 1000));
            _transport.Send(p);
        }

        public void SendMetadata(string title, string artist, string album)
        {
            if (Config.IsAdvancedMode)
            {
                byte[] p = PackageBuilder.BuildMetadata(title, artist, album);
                _transport.Send(p);
            }
            else
            {
                string raw = $">> {title} / {artist} / {album}\n";
                byte[] b = Config.Encoding.GetBytes(raw);
                _transport.Send(b);
            }
        }

        // 在 PackageMaster 类中添加以下方法

        public void UpdateTransport(IMediaTransport newTransport)
        {
            lock (_syncLock)
            {
                _transport?.Disconnect();
                // 这里可以直接赋值，因为我们要切换底层协议
                // 注意：如果你需要更严谨，可以重新绑定事件
                _transport = newTransport;
            }
        }

        public void ReconnectTransport()
        {
            _transport?.Connect();
        }

        public void SendTimeSync()
        {
            if (!Config.IsAdvancedMode)
                return;
            var p = PackageBuilder.BuildTimeSync();
            _transport.Send(p);
        }
    }
}