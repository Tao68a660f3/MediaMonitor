using System;

namespace MediaMonitor.Protocol.New.Codec
{
    /// <summary>
    /// 字节流帧解析器（规范 §18）。
    ///
    /// 实现方式与 C 侧（ref_c/src/np_codec.c）逐条对应：
    /// 线性缓冲 + 反复从队首抽取完整帧，可读性优先；每次抽取一次拷贝，
    /// 量级在微秒级，远低于任何链路的帧间隔。
    ///
    /// 处理规则（§18.3）：
    ///   1. 丢弃 MAGIC 之前的垃圾；
    ///   2. 凑满 24B 才读 PAYLOAD_LEN（偏移 8；HEADER_LEN 在偏移 7）；
    ///   3. HEADER_LEN / PAYLOAD_LEN 越界 → 丢 1 字节重搜；
    ///   4. CRC16 不符 → 丢 1 字节重搜；
    ///   5. 帧内不搜索 MAGIC（按长度语义消费），payload 里的 5A A5 不受影响。
    ///
    /// 线程安全：<see cref="Feed"/> 内部加锁；帧回调在锁内触发，
    /// 因此回调**只应做轻量入队**，不要做耗时操作（否则会阻塞收包线程）。
    /// </summary>
    public sealed class NpStreamParser
    {
        private readonly object _gate = new object();
        private readonly byte[] _buf = new byte[NpConstants.MaxFrameLen];
        private int _got;

        /// <summary>每解出一帧触发一次</summary>
        public event Action<NpFrame>? FrameReceived;

        public NpParserStats Stats { get; } = new NpParserStats();

        /// <summary>清空半帧（统计保留）</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _got = 0;
            }
        }

        /// <summary>喂入任意长度的字节流：半帧 / 整帧 / 多帧粘包 / 跨调用分片都支持。</summary>
        public void Feed(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                Stats.BytesIn += data.Length;

                while (data.Length > 0)
                {
                    int space = _buf.Length - _got;
                    if (space == 0)
                    {
                        ExtractFrames();
                        space = _buf.Length - _got;
                        if (space == 0)
                        {
                            // 防御：保证循环推进（正常路径不会走到这里）
                            Buffer.BlockCopy(_buf, 1, _buf, 0, _got - 1);
                            _got--;
                            Stats.Resyncs++;
                            space = _buf.Length - _got;
                        }
                    }

                    int n = Math.Min(space, data.Length);
                    data[..n].CopyTo(_buf.AsSpan(_got));
                    _got += n;
                    data = data[n..];

                    ExtractFrames();
                }
            }
        }

        private void ExtractFrames()
        {
            while (true)
            {
                // 1) 丢掉 MAGIC 之前的垃圾字节
                int scan = 0;
                while (scan + 1 < _got &&
                       !(_buf[scan] == NpConstants.Magic0 && _buf[scan + 1] == NpConstants.Magic1))
                {
                    scan++;
                }
                if (scan > 0)
                {
                    Buffer.BlockCopy(_buf, scan, _buf, 0, _got - scan);
                    _got -= scan;
                    Stats.Resyncs++;
                }

                if (_got < NpConstants.HeaderLenFixed)
                {
                    return;
                }

                // 2) 头部范围校验
                byte headerLen = _buf[NpConstants.OffHeaderLen];
                uint payloadLen = NpReader.ReadU32(_buf.AsSpan(NpConstants.OffPayloadLen));

                if (headerLen < NpConstants.HeaderLenFixed ||
                    headerLen > NpConstants.MaxHeaderLen ||
                    payloadLen > NpConstants.MaxPayloadLen)
                {
                    Buffer.BlockCopy(_buf, 1, _buf, 0, _got - 1);
                    _got--;
                    Stats.DroppedBadLen++;
                    Stats.Resyncs++;
                    continue;
                }

                // 3) 等整帧到齐
                int need = headerLen + (int)payloadLen + 2;
                if (_got < need)
                {
                    return;
                }

                // 4) CRC16 覆盖 [2, need-2)
                if (Crc16CcittFalse.Compute(_buf.AsSpan(2, need - 4)) ==
                    NpReader.ReadU16(_buf.AsSpan(need - 2)))
                {
                    var payload = new byte[(int)payloadLen];
                    if (payloadLen > 0)
                    {
                        Buffer.BlockCopy(_buf, headerLen, payload, 0, (int)payloadLen);
                    }

                    var frame = new NpFrame
                    {
                        Version = NpReader.ReadU16(_buf.AsSpan(NpConstants.OffVersion)),
                        Flags = _buf[NpConstants.OffFlags],
                        Type = _buf[NpConstants.OffType],
                        Code = _buf[NpConstants.OffCode],
                        HeaderLen = headerLen,
                        PayloadLen = payloadLen,
                        Sequence = NpReader.ReadU32(_buf.AsSpan(NpConstants.OffSequence)),
                        SessionId = NpReader.ReadU32(_buf.AsSpan(NpConstants.OffSessionId)),
                        RequestId = NpReader.ReadU32(_buf.AsSpan(NpConstants.OffRequestId)),
                        Payload = payload
                    };

                    Stats.FramesOk++;

                    Buffer.BlockCopy(_buf, need, _buf, 0, _got - need);
                    _got -= need;

                    FrameReceived?.Invoke(frame);
                    continue;
                }

                // CRC 不符：丢 1 字节重搜
                Buffer.BlockCopy(_buf, 1, _buf, 0, _got - 1);
                _got--;
                Stats.CrcErrors++;
                Stats.Resyncs++;
            }
        }
    }
}
