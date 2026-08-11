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

        // --- 同步包调度：使用真实时间戳，避免 Task.Delay 精度不足导致的间隔漂移 ---
        private long _lastSyncTimeMs = 0;

        // --- Seek/进度跳变检测（切歌的大跳变也归入 Seek，立即补发同步包）---
        private double _lastSeenPositionMs = -1;   // 上一帧看到的播放器进度

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

            // 监听媒体更新
            _smtc.OnMediaUpdated = props =>
            {
                // 过渡期空属性事件不触发加载/发送，避免清空歌词和发出空元数据包（双保险）
                if (string.IsNullOrWhiteSpace(props.Title))
                    return;

                _lyricService.LoadAndParse(props.Title, props.Artist);
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
            int cIdx = _lyricService.Lines.FindLastIndex(l => l.Time <= prog.Position);

            LyricChanged?.Invoke(cIdx, _lyricService.GetLine(cIdx));

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
                HandleOutput(cIdx, isJump);
            }
        }

        private void HandleOutput(int cIdx, bool forceRefresh)
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
                var line = _lyricService.GetLine(lyricIdx);

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
                        var endTime = _lyricService.GetEndTime(lyricIdx, TimeSpan.FromSeconds(_totalSeconds));
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
                if (lyricIdx > _lyricService.Lines.Count + Config.LineLimit)
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
            _totalSeconds = info.Duration.TotalSeconds;
            _isPlaying = (info.Status == PlaybackState.Playing);
        }

        private void SendSyncPacket(double currentMs)
        {
            if (!_transport.IsConnected || !Config.IsAdvancedMode)
                return;

            uint safeMs;
            double offset = Config.SyncCurrentOffsetMs;

            // 零点保护：真实进度落在 0 时不加偏移，直接发真实进度。
            // 正偏移会在该区间制造"假进度"（0→10ms），负偏移会因钳位丢失进度细节（50ms→0ms），
            // 都会破坏硬件端"0ms = 起点锚点"的语义。
            if (currentMs == 0)
            {
                safeMs = (uint)Math.Max(0, currentMs);
            }
            else
            {
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