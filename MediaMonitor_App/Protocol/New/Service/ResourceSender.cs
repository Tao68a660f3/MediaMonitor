using System;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// 资源内容提供者。
    /// 返回 <c>null</c> = 暂未就绪（回 `ACK(NOT_READY)`，对端稍后用**新 REQUEST_ID** 重试）；
    /// 返回**空资源**（`Format=None, Data.Length=0`）= 确实没有该资源（如无封面），属正常响应。
    /// </summary>
    public delegate NpResourceData? ResourceProvider(byte type, ushort width, ushort height,
                                                     NpResFormat format, byte quality);

    /// <summary>
    /// 资源发送（规范 §9 / §10 / §11）。
    ///
    /// 流程：`REQUEST → ACK(OK) → BEGIN → DATA×N → END → 等 END 的 ACK`。
    /// * **单资源互斥**（§9.7 / N-13）：收到新 REQUEST 时先 `ABORT` 旧资源，再开始新的；
    /// * 分片**一次性入队**，由 <see cref="NewSendScheduler"/> 按 `Resource` 优先级节流发送——
    ///   实时帧（TIMELINE/ACK）天然插空，这就是规范 §20 的"让路"，不需要额外的调度逻辑；
    /// * 资源过期（切歌）由上层调用 <see cref="AbortCurrent"/>。
    /// </summary>
    public sealed class ResourceSender
    {
        private sealed class Transfer
        {
            public byte Type;
            public uint RequestId;
            public NpResourceData Data = null!;
            public uint BeginSeq;
            public uint EndSeq;
        }

        private readonly SessionManager _session;
        private readonly ResourceProvider _provider;
        private readonly int _chunkSize;

        private Transfer? _current;

        public long StatRequests, StatTransfers, StatBytes, StatAborted;
        public long StatNotReady, StatRejected, StatCacheHits, StatAckFail, StatTooLarge;

        public string LastAbortReason { get; private set; } = "";
        public byte LastAckStatus { get; private set; }
        public uint LastRejectDetail { get; private set; }

        public ResourceSender(SessionManager session, ResourceProvider provider, int chunkSize = 1024)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _chunkSize = Math.Clamp(chunkSize, 64, 4096);
        }

        public bool IsBusy => _current != null;
        public byte CurrentType => (_current != null) ? _current.Type : (byte)0;
        public uint CurrentRequestId => (_current != null) ? _current.RequestId : 0u;

        /// <summary>
        /// 统一入口 —— **必须在 <see cref="SessionManager.OnFrame"/> 之前调用**：
        /// 会话层会把 ACK 消费掉，资源发送方需要先看到 END 的 ACK 才能判定传输完成。
        /// </summary>
        public void OnFrame(in NpFrame f)
        {
            if (f.Type == NpType.Lyrics || f.Type == NpType.AlbumCover)
            {
                if (f.Code == NpResCode.Request)
                {
                    HandleRequest(f);
                }
                return;
            }

            if (f.Type == NpType.System && f.Code == NpSysCode.Ack)
            {
                HandleAck(f);
            }
        }

        private void HandleRequest(in NpFrame f)
        {
            StatRequests++;

            // 单资源互斥：新请求到达 → 先 ABORT 旧资源（规范 §9.7）
            if (_current != null)
            {
                AbortCurrent("收到新的 REQUEST，旧的资源作废");
            }

            ushort width = 0;
            ushort height = 0;
            byte quality = 0;
            NpResFormat format = NpResFormat.None;

            if (f.Type == NpType.AlbumCover)
            {
                if (f.PayloadLen < 8u)
                {
                    StatRejected++;
                    _session.SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.Invalid);
                    return;
                }

                ReadOnlySpan<byte> p = f.Payload.Span;
                width = NpReader.ReadU16(p);
                height = NpReader.ReadU16(p[2..]);
                format = (NpResFormat)p[4];
                quality = p[5];
            }

            NpResourceData? data = _provider(f.Type, width, height, format, quality);
            if (data == null)
            {
                StatNotReady++;
                _session.SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.NotReady);
                return;
            }

            // 资源不得超过对端在 HELLO 里声明的 MAX_RESOURCE_SIZE（规范 §5.2 / §10.3）：
            // 否则对端接收缓冲装不下，只会白传一遍再判 OVERFLOW。
            // 这里提前拒绝并回报实际大小，由对端决定改用更小尺寸/更省的格式重新请求。
            uint limit = _session.RemoteCaps?.MaxResourceSize ?? 0u;
            if ((limit != 0u) && ((uint)data.Data.Length > limit))
            {
                StatTooLarge++;
                LastRejectDetail = (uint)data.Data.Length;
                _session.SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.Rejected);
                _session.SendError(NpErrCode.ResourceFail, f.RequestId, (uint)data.Data.Length);
                return;
            }

            var t = new Transfer { Type = f.Type, RequestId = f.RequestId, Data = data };
            _current = t;

            _session.SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.Ok);   // 先接受请求

            t.BeginSeq = SendBegin(t);

            int total = data.Data.Length;
            int off = 0;
            while (off < total)
            {
                int n = Math.Min(_chunkSize, total - off);
                SendData(t, off, n);
                off += n;
            }

            t.EndSeq = SendEnd(t);

            StatTransfers++;
            StatBytes += total;
        }

        private void HandleAck(in NpFrame f)
        {
            if ((f.PayloadLen < 5u) || (_current == null))
            {
                return;
            }

            Transfer t = _current;
            uint acked = NpReader.ReadU32(f.Payload.Span);
            byte status = f.Payload.Span[4];

            if (acked == t.EndSeq)
            {
                _current = null;
                LastAckStatus = status;
                if (status != (byte)NpAckStatus.Ok)
                {
                    StatAckFail++;          // 对端报错（校验失败等）→ 上层可决定重传或提示
                }
            }
            else if ((acked == t.BeginSeq) && (status == (byte)NpAckStatus.Ok))
            {
                // 对端缓存命中（规范 §9.3）：可提前结束本次传输。
                // 注意：V1.x 发送端不等待 BEGIN 的 ACK 就开始发 DATA，因此这里**省不了带宽**，
                // 只省掉对端后续的处理；下位机把缓存命中做得更早即可自然受益。
                StatCacheHits++;
                _current = null;
            }
        }

        /// <summary>中止当前资源传输（切歌 / 资源过期）：发 ABORT，对端应丢弃其缓存</summary>
        public void AbortCurrent(string reason)
        {
            Transfer? t = _current;
            if (t == null)
            {
                return;
            }

            _current = null;
            _session.SendResourceFrame(t.Type, NpResCode.Abort, t.RequestId, null, 0);
            StatAborted++;
            LastAbortReason = reason;
        }

        /* ---------------- 分片构造 ---------------- */

        private uint SendBegin(Transfer t)
        {
            bool isCover = t.Type == NpType.AlbumCover;
            byte[] pay = new byte[isCover ? 13 : 8];

            NpWriter.WriteU32(pay, t.Data.Crc32);
            NpWriter.WriteU32(pay.AsSpan(4), (uint)t.Data.Data.Length);

            if (isCover)
            {
                pay[8] = t.Data.Format;
                NpWriter.WriteU16(pay.AsSpan(9), t.Data.Width);
                NpWriter.WriteU16(pay.AsSpan(11), t.Data.Height);
            }

            return _session.SendResourceFrame(t.Type, NpResCode.Begin, t.RequestId, pay, 0);
        }

        private void SendData(Transfer t, int offset, int length)
        {
            byte[] pay = new byte[8 + length];
            NpWriter.WriteU32(pay, (uint)offset);
            NpWriter.WriteU32(pay.AsSpan(4), (uint)t.Data.Data.Length);
            Buffer.BlockCopy(t.Data.Data, offset, pay, 8, length);

            _session.SendResourceFrame(t.Type, NpResCode.Data, t.RequestId, pay, NpFlag.Fragment);
        }

        private uint SendEnd(Transfer t)
            // §5.6 约定表（V1.1-21）：资源 END 必须置 ACK_REQUIRED=1 —— 接收方校验通过后才回 ACK(OK)；
            // 注意与 needAck（原帧重传槽）无关：资源帧不做原帧重传，丢了由上层用新 REQUEST_ID 重来。
            => _session.SendResourceFrame(t.Type, NpResCode.End, t.RequestId, null, NpFlag.AckRequired);
    }
}
