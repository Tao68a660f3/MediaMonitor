using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaMonitor.Protocol.New.Transport;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>发送优先级（规范 §20：高 → 低）</summary>
    public enum NpPriority
    {
        System = 0,     // ACK / ERROR / SESSION_* / LATENCY
        Control = 1,    // 按键回控（要求低延迟）
        Timeline = 2,   // 实时时间轴
        Media = 3,      // 切歌时一次
        Resource = 4    // 歌词 / 封面分片（永远排最后）
    }

    /// <summary>
    /// 出站发送调度器（规范 §20）。
    ///
    /// 实现就是"每轮取最高优先级队列的队首"——资源帧（最低优先级）天然被实时帧插空，
    /// 即"让路式"，不做优先级抢占、不需要复杂队列：
    ///   * 入队即返回，不阻塞业务线程；
    ///   * 单后台线程按 <c>frameIntervalMs</c> 节流（默认 10ms，与 Legacy 的 SendIntervalMs 同量级）；
    ///   * 同一 REQUEST_ID 的 BEGIN/DATA/END 顺序天然保持（同一队列 FIFO）。
    /// </summary>
    public sealed class NewSendScheduler : IDisposable
    {
        internal const int PriorityCount = 5;

        private readonly INewTransport _transport;
        private readonly Queue<byte[]>[] _queues = new Queue<byte[]>[PriorityCount];
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, int.MaxValue);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly int _frameIntervalMs;

        private Task? _loop;

        public long SentFrames { get; private set; }
        public long DroppedFrames { get; private set; }

        public NewSendScheduler(INewTransport transport, int frameIntervalMs = 10)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _frameIntervalMs = Math.Clamp(frameIntervalMs, 0, 200);

            for (int i = 0; i < PriorityCount; i++)
            {
                _queues[i] = new Queue<byte[]>();
            }
        }

        public void Start()
        {
            _loop ??= Task.Run(LoopAsync);
        }

        /// <summary>入队即返回</summary>
        public void Enqueue(NpPriority priority, byte[] frame)
        {
            if ((frame == null) || (frame.Length == 0))
            {
                return;
            }

            lock (_gate)
            {
                _queues[(int)priority].Enqueue(frame);
            }
            _signal.Release();
        }

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    for (int i = 0; i < PriorityCount; i++)
                    {
                        n += _queues[i].Count;
                    }
                    return n;
                }
            }
        }

        /// <summary>等队列排空（自测 / 关闭流程用）</summary>
        public async Task<bool> WaitDrainedAsync(int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (PendingCount == 0)
                {
                    return true;
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            return PendingCount == 0;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                byte[]? frame = DequeueHighest();
                if (frame == null)
                {
                    continue;
                }

                if (!_transport.IsConnected)
                {
                    DroppedFrames++;
                    continue;
                }

                await _transport.SendAsync(frame).ConfigureAwait(false);
                SentFrames++;

                if (_frameIntervalMs > 0)
                {
                    try
                    {
                        await Task.Delay(_frameIntervalMs, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>取最高优先级队列的队首（这就是"让路"的全部实现）</summary>
        private byte[]? DequeueHighest()
        {
            lock (_gate)
            {
                for (int i = 0; i < PriorityCount; i++)
                {
                    if (_queues[i].Count > 0)
                    {
                        return _queues[i].Dequeue();
                    }
                }
            }
            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
