using System;
using System.Diagnostics;
using System.Threading;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>会话状态（与 C 侧 np_session_state_t 一一对应）</summary>
    public enum ProtocolState
    {
        Idle = 0,
        Handshake = 1,
        Negotiating = 2,          // ESP32 专用（PC 不会进入）
        WaitSessionStart = 3,
        Active = 4
    }

    /// <summary>对端能力（HELLO / HELLO_ACK 的 payload，规范 §5.2）</summary>
    public sealed class NpCaps
    {
        public uint Caps { get; set; }
        public ushort MaxImageEdge { get; set; }
        public uint MaxResourceSize { get; set; }

        public bool Has(uint bit) => (Caps & bit) != 0;

        public byte[] ToPayload()
        {
            byte[] p = new byte[10];
            NpWriter.WriteU32(p, Caps);
            NpWriter.WriteU16(p.AsSpan(4), MaxImageEdge);
            NpWriter.WriteU32(p.AsSpan(6), MaxResourceSize);
            return p;
        }

        public static NpCaps FromPayload(ReadOnlySpan<byte> p)
        {
            if (p.Length < 10)
            {
                return new NpCaps();
            }
            return new NpCaps
            {
                Caps = NpReader.ReadU32(p),
                MaxImageEdge = NpReader.ReadU16(p[4..]),
                MaxResourceSize = NpReader.ReadU32(p[6..])
            };
        }
    }

    /// <summary>
    /// PC 侧会话管理器（规范 §5 / §2.5 / §17.1）—— 是 C 侧 np_session.c 的 PC 角色镜像。
    ///
    /// 与 C 侧行为完全一致的部分：双向 HELLO、撞车收敛、HELLO 重发、ACK 原帧重传、
    /// 超时判定、ACTIVE 时收到 HELLO 的重新握手。
    /// 有意省略：SESSION_START 的**发送**逻辑（那是 ESP32 独有）；
    ///           ESP32 端的权威实现在 ref_c/src/np_session.c，由 mock 与真固件使用。
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        /* ---- 与 C 侧一致的时序常量（规范 §5.4 / §5.6）---- */
        public const int HelloTimeoutMs = 500;
        public const int HelloRetry = 3;
        public const int AckTimeoutMs = 300;
        public const int AckRetry = 3;
        public const int AckSlots = 4;

        private sealed class AckSlot
        {
            public bool Active;
            public int Retry;
            public byte Flags;
            public byte Type;
            public byte Code;
            public byte[] Payload = Array.Empty<byte>();
            public uint Seq;
            public uint Rid;
            public uint Sid;
            public long SentMs;
        }

        private readonly NewSendScheduler _scheduler;
        private readonly NpCaps _localCaps;
        private readonly Func<long> _nowMs;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly AckSlot[] _slots = new AckSlot[AckSlots];
        private readonly object _gate = new object();

        private uint _txSeq;
        private uint _ridNext;
        private long _timerMs;
        private int _retry;

        public ProtocolState State { get; private set; } = ProtocolState.Idle;
        public uint SessionId { get; private set; }
        public bool HandshakeFailed { get; private set; }

        /// <summary>对端能力（来自 HELLO_ACK，供 UI 只读展示）</summary>
        public NpCaps? RemoteCaps { get; private set; }

        public event Action<ProtocolState>? StateChanged;

        /// <summary>业务帧（MEDIA / TIMELINE / CONTROL / 资源 / LATENCY）—— 会话层已处理 SYSTEM</summary>
        public event Action<NpFrame>? BusinessFrame;

        /* ---- 统计（与 C 侧同名）---- */
        public long StatHelloTx, StatHelloRx, StatHelloAckRx, StatSessionStartRx;
        public long StatAckTx, StatAckRx, StatAckTimeout, StatErrorRx, StatBusinessFrames;
        public byte LastErrorCode;

        public SessionManager(NewSendScheduler scheduler, NpCaps? localCaps = null, Func<long>? nowMs = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _localCaps = localCaps ?? new NpCaps
            {
                Caps = NpCapsBits.Lyrics | NpCapsBits.AlbumCover | NpCapsBits.Jpeg | NpCapsBits.Png | NpCapsBits.Rgb565,
                MaxImageEdge = 1024,
                MaxResourceSize = 1024 * 1024
            };
            _nowMs = nowMs ?? (() => _clock.ElapsedMilliseconds);

            for (int i = 0; i < AckSlots; i++)
            {
                _slots[i] = new AckSlot();
            }
        }

        private long Now => _nowMs();

        /* ------------------------------------------------------------------ */

        public void Reset()
        {
            lock (_gate)
            {
                State = ProtocolState.Idle;
                SessionId = 0;
                _retry = 0;
                _timerMs = 0;
                HandshakeFailed = false;
                foreach (AckSlot s in _slots)
                {
                    s.Active = false;
                    s.Retry = 0;
                }
            }
            RaiseStateChanged();
        }

        /// <summary>链路就绪（含重连）→ 归零 SEQUENCE、发 HELLO</summary>
        public void LinkUp()
        {
            Reset();
            lock (_gate)
            {
                _txSeq = 0;
                State = ProtocolState.Handshake;
                _timerMs = Now;
            }
            SendHello();
            StatHelloTx++;
            RaiseStateChanged();
        }

        public bool IsActive => State == ProtocolState.Active;

        private void RaiseStateChanged() => StateChanged?.Invoke(State);

        /* ------------------------------------------------------------------ */
        /* 收帧：SYSTEM 由会话层消费，其余抛给上层                               */
        /* ------------------------------------------------------------------ */

        public bool OnFrame(NpFrame f)
        {
            if (f.Type != NpType.System)
            {
                StatBusinessFrames++;
                BusinessFrame?.Invoke(f);
                return false;
            }

            switch (f.Code)
            {
                case NpSysCode.Hello:
                    StatHelloRx++;
                    if (State == ProtocolState.Active)
                    {
                        Reset();                              // 对端重新握手：旧会话作废（§17.3）
                    }
                    SendHelloAck();                           // §5.3：收到 HELLO 必回 HELLO_ACK
                    if (State == ProtocolState.Idle || State == ProtocolState.Handshake)
                    {
                        lock (_gate)
                        {
                            State = ProtocolState.WaitSessionStart;
                            _timerMs = Now;
                            _retry = 0;
                        }
                        RaiseStateChanged();
                    }
                    return true;

                case NpSysCode.HelloAck:
                    StatHelloAckRx++;
                    RemoteCaps = NpCaps.FromPayload(f.Payload.Span);
                    if (State == ProtocolState.Handshake || State == ProtocolState.Idle)
                    {
                        lock (_gate)
                        {
                            State = ProtocolState.WaitSessionStart;
                            _timerMs = Now;
                            _retry = 0;
                        }
                        RaiseStateChanged();
                    }
                    return true;

                case NpSysCode.SessionStart:
                    StatSessionStartRx++;
                    if (f.PayloadLen >= 4)
                    {
                        SessionId = NpReader.ReadU32(f.Payload.Span);   // PC 全盘镜像 ESP32 的 ID
                        lock (_gate)
                        {
                            State = ProtocolState.Active;
                        }
                        SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.Ok);
                        RaiseStateChanged();
                    }
                    return true;

                case NpSysCode.Ack:
                    if (f.PayloadLen >= 5)
                    {
                        StatAckRx++;
                        HandleAck(NpReader.ReadU32(f.Payload.Span), f.Payload.Span[4]);
                    }
                    return true;

                case NpSysCode.SessionEnd:
                    Reset();
                    return true;

                case NpSysCode.Error:
                    StatErrorRx++;
                    if (f.PayloadLen >= 1)
                    {
                        LastErrorCode = f.Payload.Span[0];
                    }
                    return true;

                default:
                    return false;      // LATENCY_* 交给延迟测量模块
            }
        }

        /* ------------------------------------------------------------------ */
        /* 周期任务：HELLO 重发 + 待 ACK 帧重传                                 */
        /* ------------------------------------------------------------------ */

        public void Tick()
        {
            long now = Now;

            lock (_gate)
            {
                if (State == ProtocolState.Handshake || State == ProtocolState.WaitSessionStart)
                {
                    if (now - _timerMs >= HelloTimeoutMs)
                    {
                        if (_retry < HelloRetry)
                        {
                            _retry++;
                            _timerMs = now;
                            SendHelloLocked();
                            StatHelloTx++;
                        }
                        else
                        {
                            HandshakeFailed = true;              // 上层应据此提示用户
                            State = ProtocolState.Idle;
                        }
                    }
                }

                for (int i = 0; i < AckSlots; i++)
                {
                    AckSlot slot = _slots[i];
                    if (!slot.Active || now - slot.SentMs < AckTimeoutMs)
                    {
                        continue;
                    }

                    if (slot.Retry < AckRetry)
                    {
                        slot.Retry++;
                        slot.SentMs = now;
                        ResendSlotLocked(slot);                  // 原帧重发，SEQUENCE 不变
                    }
                    else
                    {
                        bool wasSessionStart = slot.Type == NpType.System && slot.Code == NpSysCode.SessionStart;
                        slot.Active = false;
                        StatAckTimeout++;
                        if (wasSessionStart)
                        {
                            HandshakeFailed = true;
                            State = ProtocolState.Idle;
                        }
                    }
                }
            }

            RaiseStateChanged();
        }

        private void HandleAck(uint ackedSeq, byte status)
        {
            lock (_gate)
            {
                for (int i = 0; i < AckSlots; i++)
                {
                    AckSlot slot = _slots[i];
                    if (slot.Active && slot.Seq == ackedSeq)
                    {
                        slot.Active = false;
                        return;
                    }
                }
            }
            _ = status;    // 业务帧的 ACK 结果由各自的发送方关心（P3/P4）
        }

        /* ------------------------------------------------------------------ */
        /* 发送                                                                */
        /* ------------------------------------------------------------------ */

        private static NpPriority PriorityOf(byte type)
        {
            return type switch
            {
                NpType.System => NpPriority.System,
                NpType.Control => NpPriority.Control,
                NpType.Timeline => NpPriority.Timeline,
                NpType.Media => NpPriority.Media,
                _ => NpPriority.Resource
            };
        }

        private uint NextSeq() => _txSeq++;

        public uint AllocRid()
        {
            lock (_gate)
            {
                _ridNext++;
                if (_ridNext == 0)
                {
                    _ridNext = 1;      // 0 保留给"无须关联的实时数据"
                }
                return _ridNext;
            }
        }

        private uint SendRaw(uint sid, uint rid, byte flags, byte type, byte code,
                             byte[]? payload, bool needAck)
        {
            uint seq = NextSeq();
            payload ??= Array.Empty<byte>();

            var frame = new NpFrame
            {
                Version = NpConstants.Version11,
                Flags = flags,
                Type = type,
                Code = code,
                HeaderLen = NpConstants.HeaderLenFixed,
                PayloadLen = (uint)payload.Length,
                Sequence = seq,
                SessionId = sid,
                RequestId = rid,
                Payload = payload
            };

            byte[]? bytes = NpEncoder.EncodeToArray(frame);
            if (bytes == null)
            {
                return 0;
            }

            _scheduler.Enqueue(PriorityOf(type), bytes);

            if (needAck)
            {
                StoreSlot(seq, sid, rid, flags, type, code, payload);
            }

            return seq;
        }

        /// <summary>
        /// 发送资源帧（BEGIN / DATA / END / ABORT，规范 §9）；返回该帧使用的 SEQUENCE
        /// （资源发送方需要靠它识别"END 被 ACK 了"或"BEGIN 命中缓存"）。
        /// </summary>
        public uint SendResourceFrame(byte type, byte code, uint requestId, byte[]? payload, byte flags)
            => SendRaw(SessionId, requestId, flags, type, code, payload, false);

        private void StoreSlot(uint seq, uint sid, uint rid, byte flags, byte type, byte code, byte[] payload)
        {
            if (payload.Length > 64)
            {
                return;    // 大帧不做原帧重传（需要 ACK 的都是小帧）
            }

            lock (_gate)
            {
                AckSlot? slot = null;
                foreach (AckSlot s in _slots)
                {
                    if (!s.Active)
                    {
                        slot = s;
                        break;
                    }
                }
                slot ??= _slots[0];

                slot.Active = true;
                slot.Retry = 0;
                slot.Flags = flags;
                slot.Type = type;
                slot.Code = code;
                slot.Payload = payload;
                slot.Seq = seq;
                slot.Rid = rid;
                slot.Sid = sid;
                slot.SentMs = Now;
            }
        }

        /// <summary>原帧重发（调用方须持有 _gate）</summary>
        private void ResendSlotLocked(AckSlot slot)
        {
            var frame = new NpFrame
            {
                Version = NpConstants.Version11,
                Flags = slot.Flags,
                Type = slot.Type,
                Code = slot.Code,
                HeaderLen = NpConstants.HeaderLenFixed,
                PayloadLen = (uint)slot.Payload.Length,
                Sequence = slot.Seq,
                SessionId = slot.Sid,
                RequestId = slot.Rid,
                Payload = slot.Payload
            };

            byte[]? bytes = NpEncoder.EncodeToArray(frame);
            if (bytes != null)
            {
                _scheduler.Enqueue(PriorityOf(slot.Type), bytes);
            }
        }

        private void SendHello()
        {
            SendHelloLocked();
        }

        /// <summary>握手期 SESSION_ID = 0（§2.5）；HELLO 自带本端能力</summary>
        private void SendHelloLocked()
            => SendRaw(0, 0, 0, NpType.System, NpSysCode.Hello, _localCaps.ToPayload(), false);

        private void SendHelloAck()
            => SendRaw(0, 0, 0, NpType.System, NpSysCode.HelloAck, _localCaps.ToPayload(), false);

        public void SendAck(uint ackSequence, uint requestId, byte status)
        {
            byte[] pay = new byte[5];
            NpWriter.WriteU32(pay, ackSequence);
            pay[4] = status;
            StatAckTx++;
            SendRaw(SessionId, requestId, NpFlag.Response, NpType.System, NpSysCode.Ack, pay, false);
        }

        public void SendError(NpErrCode code, uint requestId, uint detail)
        {
            byte[] pay = new byte[9];
            pay[0] = (byte)code;
            NpWriter.WriteU32(pay.AsSpan(1), requestId);
            NpWriter.WriteU32(pay.AsSpan(5), detail);
            SendRaw(SessionId, requestId, NpFlag.Error, NpType.System, NpSysCode.Error, pay, false);
        }

        public void SendControl(byte ctrlCode, out uint rid)
        {
            rid = AllocRid();
            SendRaw(SessionId, rid, NpFlag.AckRequired, NpType.Control, ctrlCode, null, true);
        }

        public void SendRequestLyrics(out uint rid)
        {
            rid = AllocRid();
            SendRaw(SessionId, rid, NpFlag.AckRequired, NpType.Lyrics, NpResCode.Request, null, true);
        }

        public void SendRequestCover(ushort width, ushort height, NpResFormat format, byte quality, out uint rid)
        {
            byte[] pay = new byte[8];
            NpWriter.WriteU16(pay, width);
            NpWriter.WriteU16(pay.AsSpan(2), height);
            pay[4] = (byte)format;
            pay[5] = quality;
            rid = AllocRid();
            SendRaw(SessionId, rid, NpFlag.AckRequired, NpType.AlbumCover, NpResCode.Request, pay, true);
        }

        /// <summary>实时帧（MEDIA / TIMELINE）：REQUEST_ID = 0、不要求 ACK（规范 §6 / §7）</summary>
        public void SendRealtime(byte type, byte code, byte[] payload)
            => SendRaw(SessionId, 0, 0, type, code, payload, false);

        /* ---------------- 延迟测量（规范 §14）---------------- */

        /// <summary>LATENCY_REQUEST：payload = T1（发起方本地 tick，ms）</summary>
        public void SendLatencyRequest(uint requestId, uint t1Ms)
        {
            byte[] pay = new byte[4];
            NpWriter.WriteU32(pay, t1Ms);
            SendRaw(SessionId, requestId, 0, NpType.System, NpSysCode.LatencyRequest, pay, false);
        }

        /// <summary>LATENCY_RESPONSE：payload = T1（原样回传）+ PROC_US（本端处理耗时，微秒）</summary>
        public void SendLatencyResponse(uint requestId, uint t1Ms, uint procUs)
        {
            byte[] pay = new byte[8];
            NpWriter.WriteU32(pay, t1Ms);
            NpWriter.WriteU32(pay.AsSpan(4), procUs);
            SendRaw(SessionId, requestId, NpFlag.Response, NpType.System, NpSysCode.LatencyResponse, pay, false);
        }

        /// <summary>LATENCY_END：payload = SAMPLE_COUNT（本轮有效样本数）</summary>
        public void SendLatencyEnd(uint requestId, ushort sampleCount)
        {
            byte[] pay = new byte[2];
            NpWriter.WriteU16(pay, sampleCount);
            SendRaw(SessionId, requestId, 0, NpType.System, NpSysCode.LatencyEnd, pay, false);
        }

        public void Dispose()
        {
            _clock.Stop();
        }
    }
}




