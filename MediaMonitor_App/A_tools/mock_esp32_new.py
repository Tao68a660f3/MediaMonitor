# -*- coding: utf-8 -*-
"""
mock_esp32_new.py —— New Protocol V1.1 的 ESP32 模拟器（TCP Server）

特点：**协议字节层直接跑 ref_c 编出来的 np_ref.dll**（ctypes 调用），
      所以这台"假 ESP32"用的是与真固件同一份 C 代码；Python 只负责 socket 与打印。
      能跟它握手成功，基本就等于能跟真固件握手成功（差异只剩应用层）。

先构建 DLL（只需一次，改 C 代码后再跑）：
    cd ref_c\\tests && build_dll.bat

用法：
    python mock_esp32_new.py --port 9100
    python mock_esp32_new.py --port 9100 --max-edge 240 --caps lyrics,cover,jpeg,rgb565
    python mock_esp32_new.py --port 9100 --exit-after-active   # 会话建立后自动退出（脚本化验证用）
    python mock_esp32_new.py --dll <别的 np_ref.dll 路径>

PC 侧把「协议模式 = New」「传输 = TCP」「对端 IP/端口」填成本机与上面的端口即可。
"""

import argparse
import ctypes
import os
import socket
import sys
import time

# ---------------------------------------------------------------- 常量

NP_DLL_PAYLOAD_MAX = 1088
NP_FRAME_OUT_SIZE = 24 + NP_DLL_PAYLOAD_MAX      # 结构体应有大小（用于校验 ctypes 布局）

TYPE_NAMES = {1: "SYSTEM", 2: "MEDIA", 3: "TIMELINE", 4: "CONTROL", 5: "LYRICS", 6: "ALBUMCOVER"}
SYS_NAMES = {1: "HELLO", 2: "HELLO_ACK", 3: "SESSION_START", 4: "SESSION_END",
             5: "ACK", 6: "ERROR", 0x10: "LATENCY_REQUEST", 0x11: "LATENCY_RESPONSE",
             0x12: "LATENCY_END"}
RES_NAMES = {1: "REQUEST", 2: "BEGIN", 3: "DATA", 4: "END", 5: "ABORT"}
CTRL_NAMES = {1: "PLAY_PAUSE", 2: "NEXT", 3: "PREVIOUS"}
MEDIA_NAMES = {1: "METADATA"}
TIMELINE_NAMES = {1: "STATE"}
CAP_BITS = {"lyrics": 1, "cover": 2, "jpeg": 4, "png": 8, "rgb565": 16}

STATE_NAMES = {0: "IDLE", 1: "HANDSHAKE", 2: "NEGOTIATING", 3: "WAIT_SESSION_START", 4: "ACTIVE"}

RES_EV_NAMES = {0: "NONE", 1: "BEGIN", 2: "PROGRESS", 3: "DONE", 4: "ERROR", 5: "ABORTED"}
RES_ERR_NAMES = {0: "OK", 1: "NO_BEGIN", 2: "BAD_PAYLOAD", 3: "OVERFLOW", 4: "GAP",
                 5: "SIZE_MISMATCH", 6: "CRC32"}
FMT_EXT = {0x00: "bin", 0x01: "jpg", 0x02: "png", 0x10: "rgb565"}

DEFAULT_DLL = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "..", "RLCCProject", "Q_Series", "Protocol", "ref_c", "build", "np_ref.dll"))


# ---------------------------------------------------------------- ctypes 绑定

class NpFrameOut(ctypes.Structure):
    """与 C 侧 np_frame_out_t 对应（uint32 需 4 字节对齐；ctypes 默认对齐规则与 MSVC 一致）"""
    _fields_ = [
        ("version", ctypes.c_uint16),
        ("flags", ctypes.c_uint8),
        ("type", ctypes.c_uint8),
        ("code", ctypes.c_uint8),
        ("header_len", ctypes.c_uint8),
        ("payload_len", ctypes.c_uint32),
        ("sequence", ctypes.c_uint32),
        ("session_id", ctypes.c_uint32),
        ("request_id", ctypes.c_uint32),
        ("payload", ctypes.c_uint8 * NP_DLL_PAYLOAD_MAX),
    ]


def load_dll(path):
    if not os.path.exists(path):
        print("[mock] 找不到 DLL：%s\n       请先执行 ref_c\\tests\\build_dll.bat" % path)
        sys.exit(2)

    dll = ctypes.CDLL(path)

    dll.np_dll_init.restype = ctypes.c_int
    dll.np_dll_selftest.restype = ctypes.c_int
    dll.np_dll_set_time_ms.argtypes = [ctypes.c_uint32]
    dll.np_dll_get_time_ms.restype = ctypes.c_uint32
    dll.np_dll_feed.argtypes = [ctypes.c_char_p, ctypes.c_int]
    dll.np_dll_poll.argtypes = [ctypes.POINTER(NpFrameOut)]
    dll.np_dll_poll.restype = ctypes.c_int
    dll.np_dll_tx_poll.argtypes = [ctypes.c_void_p, ctypes.c_int]
    dll.np_dll_tx_poll.restype = ctypes.c_int
    dll.np_dll_tx_count.restype = ctypes.c_int
    dll.np_dll_send_control.argtypes = [ctypes.c_int]
    dll.np_dll_send_control.restype = ctypes.c_int
    dll.np_dll_send_request_lyrics.restype = ctypes.c_int
    dll.np_dll_send_request_cover.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int]
    dll.np_dll_send_request_cover.restype = ctypes.c_int

    dll.np_dll_res_init.restype = ctypes.c_int
    dll.np_dll_res_on_frame.argtypes = [ctypes.POINTER(NpFrameOut)]
    dll.np_dll_res_on_frame.restype = ctypes.c_int
    dll.np_dll_res_size.restype = ctypes.c_uint32
    dll.np_dll_res_format.restype = ctypes.c_int
    dll.np_dll_res_width.restype = ctypes.c_int
    dll.np_dll_res_height.restype = ctypes.c_int
    dll.np_dll_res_copy.argtypes = [ctypes.c_void_p, ctypes.c_int]
    dll.np_dll_res_copy.restype = ctypes.c_int
    dll.np_dll_res_stat_err.restype = ctypes.c_uint32
    dll.np_dll_res_last_err.restype = ctypes.c_int
    dll.np_dll_res_is_complete.restype = ctypes.c_int

    dll.np_dll_timeline_init.restype = ctypes.c_int
    dll.np_dll_timeline_on_frame.argtypes = [ctypes.POINTER(NpFrameOut)]
    dll.np_dll_timeline_on_frame.restype = ctypes.c_int
    dll.np_dll_timeline_delivered.restype = ctypes.c_uint32
    dll.np_dll_timeline_last_playing.restype = ctypes.c_int
    dll.np_dll_timeline_last_current_ms.restype = ctypes.c_uint32
    dll.np_dll_timeline_last_total_ms.restype = ctypes.c_uint32
    dll.np_dll_timeline_last_host_tick.restype = ctypes.c_uint32
    dll.np_dll_timeline_last_local_tick.restype = ctypes.c_uint32

    dll.np_dll_latency_respond.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32]
    dll.np_dll_latency_respond.restype = ctypes.c_int

    dll.np_dll_session_init.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32, ctypes.c_int]
    dll.np_dll_session_link_up.restype = None
    dll.np_dll_session_tick.restype = None
    dll.np_dll_session_state.restype = ctypes.c_int
    dll.np_dll_session_state_name.restype = ctypes.c_char_p
    dll.np_dll_session_id.restype = ctypes.c_uint32
    dll.np_dll_session_handshake_failed.restype = ctypes.c_int

    dll.np_dll_stat_frames_rx.restype = ctypes.c_uint32
    dll.np_dll_stat_crc_errors.restype = ctypes.c_uint32
    dll.np_dll_stat_resyncs.restype = ctypes.c_uint32
    dll.np_dll_stat_tx_dropped.restype = ctypes.c_uint32

    if ctypes.sizeof(NpFrameOut) != NP_FRAME_OUT_SIZE:
        print("[mock] NpFrameOut 布局与 C 不一致：%d != %d" % (ctypes.sizeof(NpFrameOut), NP_FRAME_OUT_SIZE))
        sys.exit(2)
    return dll


def frame_name(f):
    if f.type == 1:
        return SYS_NAMES.get(f.code, "SYS_0x%02X" % f.code)
    if f.type == 2:
        return MEDIA_NAMES.get(f.code, "MEDIA_0x%02X" % f.code)
    if f.type == 3:
        return TIMELINE_NAMES.get(f.code, "TL_0x%02X" % f.code)
    if f.type in (5, 6):
        return RES_NAMES.get(f.code, "RES_0x%02X" % f.code)
    if f.type == 4:
        return CTRL_NAMES.get(f.code, "CTRL_0x%02X" % f.code)
    return "CODE_0x%02X" % f.code


def _be16(b, off):
    return (b[off] << 8) | b[off + 1]


def _be32(b, off):
    return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]


def _utf8(b, off, length):
    return bytes(b[off:off + length]).decode("utf-8", "replace")


def decode_media(payload):
    """MEDIA/METADATA（新编码，规范 §6）：u16 BE 长度 + UTF-8 文本 ×3"""
    if len(payload) < 6:
        return "(payload 太短)"

    p = 0
    tlen = _be16(payload, p); p += 2
    title = _utf8(payload, p, tlen); p += tlen
    alen = _be16(payload, p); p += 2
    artist = _utf8(payload, p, alen); p += alen
    blen = _be16(payload, p); p += 2
    album = _utf8(payload, p, blen)
    return "title='%s' artist='%s' album='%s'" % (title, artist, album)


def decode_timeline(payload):
    """TIMELINE/STATE（新编码，规范 §7）：STATE u8 + 3×u32 BE"""
    if len(payload) < 13:
        return "(payload 太短)"
    state = payload[0]
    cur = _be32(payload, 1)
    total = _be32(payload, 5)
    tick = _be32(payload, 9)
    return "state=%d cur=%dms total=%dms tick=%d" % (state, cur, total, tick)


def count_legacy_lyric_frames(data):
    """粗数一下资源里含多少个 Legacy 帧（按 AA <cmd> <lenH> <lenL> ... <XOR> 逐个跳过）"""
    data = bytes(data)
    i = 0
    n = 0
    while i + 5 <= len(data):
        if data[i] != 0xAA:
            i += 1
            continue
        length = (data[i + 2] << 8) | data[i + 3]
        total = 4 + length + 1
        if i + total > len(data):
            break
        n += 1
        i += total
    return n


def dump_resource(dll, rtype, art_dir):
    """把 C 侧收齐并通过 CRC32 校验的资源落盘，便于肉眼验证"""
    size = dll.np_dll_res_size()
    fmt = dll.np_dll_res_format()
    w = dll.np_dll_res_width()
    h = dll.np_dll_res_height()

    buf = ctypes.create_string_buffer(max(size, 1))
    n = dll.np_dll_res_copy(buf, len(buf))
    data = buf.raw[:n]

    kind = "lyrics" if rtype == 5 else "cover"
    path = os.path.join(art_dir, "%s.%s" % (kind, FMT_EXT.get(fmt, "bin")))
    os.makedirs(art_dir, exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(data)

    print("[mock] === 资源收齐：%s  大小=%d  格式=0x%02X  尺寸=%dx%d  （CRC32 由 C 侧校验通过）  -> %s"
          % (kind, size, fmt, w, h, path))

    if rtype == 5:
        print("[mock] 歌词资源内含 Legacy 帧 %d 个（0x15 普通行 / 0x14 逐字 / 0x13 翻译）"
              % count_legacy_lyric_frames(data))


def print_frame(f):
    payload = bytes(f.payload[:f.payload_len])
    extra = payload.hex(" ").upper() if payload else "-"

    if f.type == 2 and f.code == 0x01:
        extra = decode_media(payload)
    elif f.type == 3 and f.code == 0x01:
        extra = decode_timeline(payload)

    print("[mock] <- %-9s %-14s len=%-4d seq=%-4d sid=0x%08X rid=%-4d  %s"
          % (TYPE_NAMES.get(f.type, "?"), frame_name(f), f.payload_len,
             f.sequence, f.session_id, f.request_id, extra))


# ---------------------------------------------------------------- 命令行与主流程

def parse_args():
    ap = argparse.ArgumentParser(description="New Protocol V1.1 ESP32 模拟器（协议层跑 np_ref.dll）")
    ap.add_argument("--ip", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9100)
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--caps", default="lyrics,cover,jpeg,rgb565",
                    help="逗号分隔：lyrics,cover,jpeg,png,rgb565")
    ap.add_argument("--max-edge", type=int, default=240, help="HELLO 里声明能接受的最大封面边长")
    ap.add_argument("--max-resource", type=int, default=262144, help="能接收的最大资源字节数（写进 HELLO）")
    ap.add_argument("--tick-ms", type=int, default=10, help="主循环周期")
    ap.add_argument("--exit-after-active", action="store_true", help="会话建立后自动退出")
    ap.add_argument("--control-every", type=float, default=0.0,
                    help="会话建立后每隔 N 秒发一次 CONTROL(PLAY_PAUSE)，用于验证 PC 侧回控")
    ap.add_argument("--run-seconds", type=float, default=0.0, help="运行 N 秒后自动退出（0 = 一直跑）")
    ap.add_argument("--art-dir", default="art_recv", help="收到的歌词/封面落盘目录")
    ap.add_argument("--cover-format", type=lambda s: int(s, 0), default=0x10,
                    help="请求封面时指定的 FORMAT：0x01=JPEG 0x02=PNG 0x10=RGB565（默认）")
    ap.add_argument("--quiet", action="store_true", help="不逐帧打印")
    return ap.parse_args()


def main():
    args = parse_args()

    caps = 0
    for name in args.caps.split(","):
        name = name.strip().lower()
        if name:
            caps |= CAP_BITS.get(name, 0)

    dll = load_dll(args.dll)

    if dll.np_dll_selftest() != 0:
        print("[mock] DLL 自检失败")
        return 2
    print("[mock] np_ref.dll 自检通过（CRC / 编解码回环）")

    dll.np_dll_init()
    dll.np_dll_session_init(caps, args.max_edge, args.max_resource, 0)   # 0 = ESP32 角色

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.ip, args.port))
    srv.listen(1)

    print("[mock] 监听 %s:%d  CAPS=0x%08X maxEdge=%d maxRes=%d"
          % (args.ip, args.port, caps, args.max_edge, args.max_resource))
    print("[mock] 等待 PC 连接（New 模式 / TCP / 对端填 %s:%d）..." % (args.ip, args.port))

    conn, addr = srv.accept()
    conn.setblocking(False)
    print("[mock] PC 已连接：%s:%d" % (addr[0], addr[1]))

    t0 = time.monotonic()
    dll.np_dll_set_time_ms(0)
    dll.np_dll_session_link_up()          # 链路就绪 → 真 C 代码立刻组出 HELLO
    print("[mock] 已发起 HELLO（ESP32 角色）")

    last_state = None
    active = False
    requested = False
    cover_requested = False
    n_delivered = 0
    n_latency_responded = 0
    last_control = time.monotonic()
    txbuf = ctypes.create_string_buffer(2048)

    try:
        while True:
            dll.np_dll_set_time_ms(int((time.monotonic() - t0) * 1000))

            # 1) socket → DLL
            try:
                data = conn.recv(4096)
                if not data:
                    print("[mock] PC 已断开")
                    break
                dll.np_dll_feed(data, len(data))
            except BlockingIOError:
                pass
            except ConnectionResetError:
                print("[mock] 连接被重置")
                break

            # 2) 打印收到的帧 + 资源接收 + 收到 MEDIA 后拉取资源
            while True:
                f = NpFrameOut()
                if dll.np_dll_poll(ctypes.byref(f)) != 1:
                    break
                if not args.quiet:
                    print_frame(f)

                if f.type in (5, 6):
                    ev = dll.np_dll_res_on_frame(ctypes.byref(f))
                    if ev == 1:
                        print("[mock] 资源 BEGIN（type=%d）" % f.type)
                    elif ev == 3:
                        dump_resource(dll, f.type, args.art_dir)
                        # 单资源互斥由 ESP32 侧保证（规范 §9.7）：等歌词收完再拉封面
                        if (f.type == 5) and (not cover_requested) and active:
                            if dll.np_dll_send_request_cover(args.max_edge, args.max_edge, args.cover_format, 0):
                                cover_requested = True
                                print("[mock] 歌词收齐 → 再发起 ALBUMCOVER REQUEST（封面 %dx%d fmt=0x%02X）"
                                      % (args.max_edge, args.max_edge, args.cover_format))
                    elif ev == 4:
                        print("[mock] 资源接收错误：%s"
                              % RES_ERR_NAMES.get(dll.np_dll_res_last_err(), "?"))
                    elif ev == 5:
                        print("[mock] 资源被 ABORT（对端切歌 / 资源作废）")
                elif (f.type == 3) and (f.code == 0x01):
                    # 时间轴：交给 C 的投递层（真机在这里接 Sync_OnPacketAt）
                    dll.np_dll_timeline_on_frame(ctypes.byref(f))
                    n_delivered += 1
                    if (n_delivered % 20) == 0:
                        print("[mock] 时间轴已投递 %d 帧（最近：playing=%d cur=%dms total=%dms host_tick=%d local_tick=%d）"
                              % (dll.np_dll_timeline_delivered(),
                                 dll.np_dll_timeline_last_playing(),
                                 dll.np_dll_timeline_last_current_ms(),
                                 dll.np_dll_timeline_last_total_ms(),
                                 dll.np_dll_timeline_last_host_tick(),
                                 dll.np_dll_timeline_last_local_tick()))
                elif (f.type == 1) and (f.code == 0x10):
                    # 延迟测量请求：原样回传 T1，并带上"本端处理耗时"（微秒）
                    payload = bytes(f.payload[:f.payload_len])
                    t1 = _be32(payload, 0) if len(payload) >= 4 else 0
                    proc_us = 150                       # 模拟 MCU 处理耗时
                    dll.np_dll_latency_respond(f.request_id, t1, proc_us)
                    n_latency_responded += 1
                elif (f.type == 2) and (not requested):
                    if dll.np_dll_send_request_lyrics():
                        requested = True
                        print("[mock] 收到 MEDIA → 发起 LYRICS REQUEST（拿到后再拉封面）")

            # 3) 让 C 会话机跑一次（重传 / 超时），取出要发的帧发走
            dll.np_dll_session_tick()
            while True:
                n = dll.np_dll_tx_poll(txbuf, len(txbuf))
                if n <= 0:
                    break
                if not args.quiet:
                    print("[mock] -> %d 字节: %s" % (n, txbuf.raw[:n].hex(" ").upper()))
                conn.sendall(txbuf.raw[:n])

            # 4) 状态变化
            st = dll.np_dll_session_state()
            if st != last_state:
                last_state = st
                print("[mock] 会话状态 -> %s" % STATE_NAMES.get(st, st))
                if st == 4:
                    active = True
                    print("[mock] === SESSION ACTIVE, id=0x%08X ===" % dll.np_dll_session_id())
                    if args.exit_after_active:
                        time.sleep(0.3)          # 留点时间把最后的 ACK 发出去
                        break

            # 5) 按键回控（可选）：周期发 CONTROL，验证 PC 侧执行链路
            if active and (args.control_every > 0.0):
                if (time.monotonic() - last_control) >= args.control_every:
                    last_control = time.monotonic()
                    queued = dll.np_dll_send_control(1)      # 1 = PLAY_PAUSE
                    print("[mock] -> CONTROL PLAY_PAUSE（入队=%d）" % queued)

            # 6) 运行时长兜底（便于脚本化验证）
            if (args.run_seconds > 0.0) and ((time.monotonic() - t0) >= args.run_seconds):
                print("[mock] 到达 --run-seconds，退出")
                break

            if dll.np_dll_session_handshake_failed():
                print("[mock] 握手失败（HELLO 重试用尽）")
                break

            time.sleep(args.tick_ms / 1000.0)
    finally:
        print("[mock] 统计：帧=%d crc错=%d 重同步=%d tx丢弃=%d 会话=%s"
              % (dll.np_dll_stat_frames_rx(), dll.np_dll_stat_crc_errors(),
                 dll.np_dll_stat_resyncs(), dll.np_dll_stat_tx_dropped(),
                 STATE_NAMES.get(dll.np_dll_session_state(), "?")))
        print("[mock] 时间轴投递给 sink 的帧数=%d  延迟请求应答数=%d"
              % (dll.np_dll_timeline_delivered(), n_latency_responded))
        conn.close()
        srv.close()

    return 0 if active else 3


if __name__ == "__main__":
    sys.exit(main())
