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
        private int _syncTickCounter = 0;

        // --- 统计学习变量 ---
        private double _lastSmtcMediaSec = -1;
        private double _lastSmtcWallSec = -1;
        private double _totalSeconds = 0;
        private bool _isPlaying = false;
        private readonly List<double> _wallIntervalSamples = new List<double>();

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
                _lyricService.LoadAndParse(props.Title, props.Artist);
                Invalidate(); // 切歌强制清空账本
                SendMetadata(props.Title, props.Artist, props.AlbumTitle);
            };

            // 监听播放状态
            _smtc.PlaybackChanged += status =>
            {
                _isPlaying = (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
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
                await Task.Delay(50, token); // 50ms 逻辑帧周期
            }
        }

        private void ProcessTick()
        {
            var prog = _smtc.GetCurrentProgress();
            if (prog == null)
                return;

            UpdateStatistics(prog);

            double cur = prog.CurrentSeconds;
            int cIdx = _lyricService.Lines.FindLastIndex(l => l.Time <= TimeSpan.FromSeconds(cur));

            LyricChanged?.Invoke(cIdx, _lyricService.GetLine(cIdx));

            if (!_transport.IsConnected)
                return;

            // 1. 同步包发送 (不涉及竞争变量，不需要锁)
            int syncThreshold = Math.Max(1, Config.SyncIntervalMs / 50);
            if (++_syncTickCounter >= syncThreshold)
            {
                if (Config.IsAdvancedMode)
                {
                    var p = PackageBuilder.BuildSync(_isPlaying, (uint)(cur * 1000), (uint)(_totalSeconds * 1000));
                    _transport.Send(p);
                }
                _syncTickCounter = 0;
            }

            // 2. 歌词行变动处理
            // 注意：_lastProcessedCIdx 也是竞争资源，读取和修改它时需要轻量锁
            lock (_syncLock)
            {
                if (cIdx != _lastProcessedCIdx)
                {
                    bool isJump = (cIdx < _lastProcessedCIdx) || Math.Abs(cIdx - _lastProcessedCIdx) > 1;
                    _lastProcessedCIdx = cIdx;

                    // 将关键的发送逻辑移出锁区，或者在里面只做判定
                    HandleOutput(cIdx, isJump);
                }
            }
        }

        private void HandleOutput(int cIdx, bool forceRefresh)
        {
            var targetSlots = new HashSet<string>();
            var dataToSync = new Dictionary<string, (byte[]? AdvData, string RawText)>();

            int currentPhysicalRow = 0;
            int lyricIdx = cIdx - Config.Offset;

            // 构建当前视野内的所有槽位 (Slot)
            while (currentPhysicalRow < Config.LineLimit)
            {
                var line = _lyricService.GetLine(lyricIdx);

                // 区分 0x12(逐行) 和 0x14(逐字)
                string cmdType = (line.Words.Count > 0) ? "0x14" : "0x12";
                string mKey = $"{lyricIdx}_{cmdType}";
                targetSlots.Add(mKey);

                string mText = line.Content ?? "";
                dataToSync[mKey] = (Config.IsAdvancedMode ? (line.Words.Count > 0
                    ? PackageBuilder.BuildWordByWord((short)lyricIdx, line.Time, line.Words)
                    : PackageBuilder.BuildLyricLine((short)lyricIdx, line.Time, mText)) : null, mText);

                currentPhysicalRow++;

                // 翻译槽位 (0x13)
                string tText = line.Translation ?? "";
                if (!string.IsNullOrEmpty(tText))
                {
                    if (!Config.TransOccupies || currentPhysicalRow < Config.LineLimit)
                    {
                        string tKey = $"{lyricIdx}_0x13";
                        targetSlots.Add(tKey);
                        dataToSync[tKey] = (Config.IsAdvancedMode ? PackageBuilder.BuildTranslationLine((short)lyricIdx, line.Time, tText) : null, tText);
                        if (Config.TransOccupies)
                            currentPhysicalRow++;
                    }
                }

                lyricIdx++;
                if (lyricIdx > _lyricService.Lines.Count + Config.LineLimit)
                    break;
            }

            // 核心差分判定：如果是增量模式且非跳转，则剔除已发送槽位
            List<string> toNotify;
            lock (_syncLock)
            {
                toNotify = (Config.IsIncremental && !forceRefresh)
                           ? targetSlots.Except(_syncedSlots).ToList()
                           : targetSlots.ToList();
            }

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
                    // 文本模式退化逻辑 (带换行符)
                    byte[] raw = Config.Encoding.GetBytes(pack.RawText + "\n");
                    _transport.Send(raw);
                }
            }

            // 更新账本并清理视野外槽位
            lock (_syncLock)
            {
                _syncedSlots = targetSlots;
            }
        }

        private void UpdateStatistics(MediaProgressInfo info)
        {
            double nowWall = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
            if (info.Status == "Playing" && _lastSmtcWallSec > 0)
            {
                double deltaWall = nowWall - _lastSmtcWallSec;
                double deltaMedia = info.CurrentSeconds - _lastSmtcMediaSec;

                // 采样节奏：过滤暂停与 Seek
                if (Math.Abs(deltaMedia - deltaWall) < 0.2)
                {
                    _wallIntervalSamples.Add(deltaWall);
                    if (_wallIntervalSamples.Count > 10)
                        _wallIntervalSamples.RemoveAt(0);
                }
                // Seek 判定：超过 2 秒偏差则清空账本重刷
                else if (Math.Abs(deltaMedia) > 2.0 || deltaMedia < 0)
                {
                    Invalidate();
                }
            }
            _lastSmtcMediaSec = info.CurrentSeconds;
            _lastSmtcWallSec = nowWall;
            _totalSeconds = info.TotalSeconds;
            _isPlaying = (info.Status == "Playing");
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