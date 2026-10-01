using System;
using MediaMonitor.Protocol.New.Codec;

namespace MediaMonitor.Protocol.New.Service
{
    /// <summary>
    /// CONTROL 处理（规范 §8）：把 ESP32 的按键回控映射成 Legacy 的媒体键命令字节，
    /// 交给注入的执行器执行（PC 应用里就是 <c>MediaKeyInvoker.Instance.EnqueueCommand</c>）。
    ///
    /// 映射沿用 Legacy 已验证的通道（MediaKeyInvoker 内部走 Windows SMTC）：
    ///   `PLAY_PAUSE → 0xA3`、`NEXT → 0xA1`、`PREVIOUS → 0xA2`；
    /// 未知 CODE → 回 `ERROR(UNSUPPORTED_CODE)`（规范 §5.7）。
    /// </summary>
    public sealed class ControlReceiver
    {
        public const byte LegacyNext = 0xA1;
        public const byte LegacyPrevious = 0xA2;
        public const byte LegacyPlayPause = 0xA3;

        private readonly SessionManager _session;
        private readonly Action<byte> _executeLegacy;

        public long StatControlRx;
        public long StatUnknownRx;
        public byte LastLegacyCmd;

        public ControlReceiver(SessionManager session, Action<byte> executeLegacyControl)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _executeLegacy = executeLegacyControl ?? throw new ArgumentNullException(nameof(executeLegacyControl));
        }

        /// <summary>处理一帧；返回 true 表示这是 CONTROL 帧并已处理（上层不必再分发）</summary>
        public bool OnFrame(NpFrame f)
        {
            if (f.Type != NpType.Control)
            {
                return false;
            }

            byte legacy;
            switch (f.Code)
            {
                case NpCtrlCode.PlayPause: legacy = LegacyPlayPause; break;
                case NpCtrlCode.Next: legacy = LegacyNext; break;
                case NpCtrlCode.Previous: legacy = LegacyPrevious; break;
                default:
                    StatUnknownRx++;
                    _session.SendError(NpErrCode.UnsupportedCode, f.RequestId, f.Code);
                    return true;
            }

            StatControlRx++;
            LastLegacyCmd = legacy;
            _executeLegacy(legacy);

            if ((f.Flags & NpFlag.AckRequired) != 0)
            {
                _session.SendAck(f.Sequence, f.RequestId, (byte)NpAckStatus.Ok);
            }
            return true;
        }
    }
}
