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
    python mock_esp32_new.py --port 9100 --max-edge 240 --caps lyrics,cover,jpeg,png,rgb565
    python mock_esp32_new.py --port 9100 --exit-after-active   # 会话建立后自动退出（脚本化验证用）
    python mock_esp32_new.py --port 9100 --latency-samples 30  # 会话建立后主动测一轮延迟（对端应答）
    python mock_esp32_new.py --dll <别的 np_ref.dll 路径>

两种传输（协议字节层完全一样，只是链路不同）：
    --transport tcp --ip 127.0.0.1 --port 9100     上位机：传输=TCP，对端 IP/端口填这里
    --transport com --com COM23 --baud 115200      上位机：传输=COM，端口填**配对的另一端**
                                                   （com0com 之类的虚拟串口对，例：上位机 COM22 ↔ mock COM23）

合规行为（都是规范里对 ESP32 的硬要求，mock 照做才能当真机用）：
    * §2.5  每次新会话都换新的 SESSION_ID（宿主 tick 的高 16 位按"第几次连接"打包，
            所以 0x0001xxxx / 0x0002xxxx… 一眼能看出是第几次会话；串口下重新握手也会换 ID）；
    * §9.5  收到资源 END 且 CRC32 校验通过 → **必须回 ACK(OK)**（校验失败 → ACK(ERROR)+ERROR）；
    * §9.7  接收方自己把两种资源**串行化**：同一时刻只让一个资源在途；
    * §9.8  收到 ACK(NOT_READY) 后 200ms 用**新的 REQUEST_ID** 重发（上限 10 次）；
    * §9.6  资源被 ABORT / 校验失败 → 用新的 REQUEST_ID 重新 REQUEST；
    * §14   **双向**延迟测量：既能应答对端（0x10 → 0x11），也能自己发起整轮
            （0x10×N → 收 0x11 算 RTT → 0x12 收尾），统计口径与上位机一致。

PC 侧把「协议模式 = New」「传输 = TCP / COM」按上面的说明填好即可。
"""

import argparse
import ctypes
import os
import socket
import sys
import time
import collections
import queue
import threading

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

# ACK STATUS（规范 §5.6）
ACK_OK = 0x00
ACK_REJECTED = 0x01
ACK_INVALID = 0x02
ACK_BUSY = 0x03
ACK_ERROR = 0x04
ACK_NOT_READY = 0x05
ACK_STATUS_NAMES = {ACK_OK: "OK", ACK_REJECTED: "REJECTED", ACK_INVALID: "INVALID",
                    ACK_BUSY: "BUSY", ACK_ERROR: "ERROR", ACK_NOT_READY: "NOT_READY"}

# FLAGS（规范 §3）
NP_FLAG_ACK_REQUIRED = 0x01        # 「要不要回 ACK」的唯一判据（§5.6 约定表，V1.1-21）
NP_FLAG_RESPONSE = 0x04
NP_FLAG_FRAGMENT = 0x08
NP_FLAG_ERROR = 0x10

# ERROR CODE（规范 §5.7）
NP_ERR_RESOURCE_FAIL = 0x05        # 资源校验/分片失败（§9.5 与 ACK(ERROR) 成对出现）

# 资源请求策略（规范 §9.6 / §9.7 / §9.8）
RES_RETRY_NOT_READY_S = 0.2        # §9.8-3：NOT_READY → 200ms 后用新 REQUEST_ID 重发
RES_RETRY_BUSY_S = 0.5             # §9.7：对端 BUSY → 稍后再要
RES_MAX_RETRY = 10                 # §9.8-3：建议重试上限 10 次
RES_IDLE_TIMEOUT_S = 3.0           # 在途资源"一点动静都没有"超过这个时间：当成丢了，换 rid 重来
KIND_NAMES = {5: "lyrics", 6: "cover"}
KIND_TYPES = {"lyrics": 5, "cover": 6}

# 延迟测量（规范 §14）
LAT_WINDOW = 30                    # 一轮样本数（与上位机 LatencyManager.WindowSize 一致）
LAT_INTERVAL_S = 0.1               # 两次 REQUEST 的间隔（100ms）
LAT_TIMEOUT_S = 5.0                # §14.4：整轮 5s 无任何有效样本 → END + 异常结束
LAT_SIM_PROC_US = 150              # 模拟 MCU 处理耗时（应答端回给对端的 PROC_US）

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
    dll.np_dll_latency_request.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
    dll.np_dll_latency_request.restype = ctypes.c_uint32      # 返回本轮 rid（0 = 未入队）
    dll.np_dll_latency_end.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
    dll.np_dll_latency_end.restype = ctypes.c_int
    dll.np_dll_last_rid.restype = ctypes.c_uint32             # 最近一次主动发送用的 REQUEST_ID
    dll.np_dll_send_ack.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint8]
    dll.np_dll_send_ack.restype = ctypes.c_int
    dll.np_dll_send_error.argtypes = [ctypes.c_uint8, ctypes.c_uint32, ctypes.c_uint32]
    dll.np_dll_send_error.restype = ctypes.c_int

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


def decode_system(payload, code):
    """SYSTEM 帧 payload 的人眼解读（ACK / ERROR / LATENCY_*）——诊断"点了没反应"时最有用的几行"""
    if (code == 0x05) and (len(payload) >= 5):                 # ACK: SEQUENCE u32 + STATUS u8
        return "ack_seq=%d status=%s(%d)" % (_be32(payload, 0),
                                             ACK_STATUS_NAMES.get(payload[4], "?"), payload[4])
    if (code == 0x06) and (len(payload) >= 9):                 # ERROR: CODE u8 + RID u32 + DETAIL u32
        return "err=0x%02X rid=%d detail=%d" % (payload[0], _be32(payload, 1), _be32(payload, 5))
    if (code == 0x10) and (len(payload) >= 4):
        return "T1=%d" % _be32(payload, 0)
    if (code == 0x11) and (len(payload) >= 8):
        return "T1=%d PROC_US=%d" % (_be32(payload, 0), _be32(payload, 4))
    if (code == 0x12) and (len(payload) >= 2):
        return "SAMPLE_COUNT=%d" % _be16(payload, 0)
    return payload.hex(" ").upper() if payload else "-"


def print_frame(f):
    payload = bytes(f.payload[:f.payload_len])
    extra = payload.hex(" ").upper() if payload else "-"

    if f.type == 1:
        extra = decode_system(payload, f.code)
    elif f.type == 2 and f.code == 0x01:
        extra = decode_media(payload)
    elif f.type == 3 and f.code == 0x01:
        extra = decode_timeline(payload)

    print("[mock] <- %-9s %-14s len=%-4d seq=%-4d sid=0x%08X rid=%-4d  %s"
          % (TYPE_NAMES.get(f.type, "?"), frame_name(f), f.payload_len,
             f.sequence, f.session_id, f.request_id, extra))


# ---------------------------------------------------------------- 资源内容解码（GUI 用）

def _le16(b, off):
    return b[off] | (b[off + 1] << 8)


def _le32(b, off):
    return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)


def _parse_lyric_payload(cmd, payload):
    """单条 Legacy 歌词帧 → dict（载荷布局见 MediaMonitor_App/Tools/PackageBuilder.cs）"""
    if len(payload) < 6:
        return None

    idx = _le16(payload, 0)
    start = _le32(payload, 2)

    if (cmd == 0x15) and (len(payload) >= 10):                      # 普通行（带结束时间）
        return {"idx": idx, "start_ms": start, "end_ms": _le32(payload, 6), "kind": "line",
                "text": payload[10:].decode("utf-8", "replace"), "words": []}

    if cmd == 0x13:                                                # 翻译行
        return {"idx": idx, "start_ms": start, "end_ms": None, "kind": "trans",
                "text": payload[6:].decode("utf-8", "replace"), "words": []}

    if (cmd == 0x14) and (len(payload) >= 7):                      # 逐字行
        n = payload[6]
        p = 7
        words = []
        for _ in range(n):
            if p + 3 > len(payload):
                break
            off = _le16(payload, p)
            ln = payload[p + 2]
            p += 3
            words.append((off, payload[p:p + ln].decode("utf-8", "replace")))
            p += ln
        return {"idx": idx, "start_ms": start, "end_ms": None, "kind": "word",
                "text": "".join(w for _, w in words), "words": words}

    return None


def parse_lyric_lines(data):
    """歌词资源（Legacy 帧字节流）→ 行列表；每行 = dict（见 _parse_lyric_payload）"""
    data = bytes(data)
    out = []
    i = 0
    while i + 5 <= len(data):
        if data[i] != 0xAA:
            i += 1
            continue

        length = (data[i + 2] << 8) | data[i + 3]
        total = 4 + length + 1
        if i + total > len(data):
            break

        item = _parse_lyric_payload(data[i + 1], data[i + 4:i + 4 + length])
        if item:
            out.append(item)
        i += total
    return out


def decode_rgb565_to_ppm(data, width, height):
    """RGB565（低字节在前，与 WPF Bgr565 输出一致）→ PPM(P6) 字节，供 Tk PhotoImage 直接显示"""
    if (width <= 0) or (height <= 0) or (len(data) < width * height * 2):
        return None

    header = b"P6\n%d %d\n255\n" % (width, height)
    body = bytearray(width * height * 3)
    j = 0
    for i in range(width * height):
        v = data[2 * i] | (data[2 * i + 1] << 8)
        r = (v >> 11) & 0x1F
        g = (v >> 5) & 0x3F
        b = v & 0x1F
        body[j] = (r * 255) // 31
        body[j + 1] = (g * 255) // 63
        body[j + 2] = (b * 255) // 31
        j += 3
    return header + bytes(body)


# ---------------------------------------------------------------- 链路（TCP / COM 抽象）

class LinkClosed(Exception):
    """链路断开（TCP 对端关闭 / 串口异常）"""


def load_serial():
    """按需导入 pyserial（只在用 COM 时才需要，纯 TCP 用户不用装）"""
    try:
        import serial                                     # noqa: F401
        return serial
    except Exception as ex:
        raise LinkClosed("需要 pyserial 才能用 COM 传输：pip install pyserial（%s）" % ex)


class TcpLink:
    """TCP 服务端的一条连接（非阻塞 recv；返回 None = 这一刻没有数据）"""

    kind = "TCP"

    def __init__(self, conn, addr):
        self.conn = conn
        self.label = "%s:%d" % (addr[0], addr[1])

    def read(self, n):
        try:
            data = self.conn.recv(n)
        except BlockingIOError:
            return None
        except (ConnectionResetError, ConnectionAbortedError) as ex:
            raise LinkClosed("连接被重置：%s" % ex)
        except OSError as ex:
            raise LinkClosed("socket 异常：%s" % ex)
        if not data:
            raise LinkClosed("对端关闭了连接")
        return data

    def write(self, data):
        try:
            self.conn.sendall(data)
        except (ConnectionResetError, ConnectionAbortedError, OSError) as ex:
            raise LinkClosed("发送失败：%s" % ex)

    def close(self):
        try:
            self.conn.close()
        except OSError:
            pass


class ComLink:
    """串口链路（点对点）：timeout=0 → 没有数据时 read() 返回 b""，这里翻译成 None"""

    kind = "COM"

    def __init__(self, ser, port, baud):
        self.ser = ser
        self.label = "%s@%d" % (port, baud)

    def read(self, n):
        serial = load_serial()
        try:
            data = self.ser.read(n)
        except (serial.SerialException, OSError) as ex:
            raise LinkClosed("串口读失败：%s" % ex)
        return data if data else None

    def write(self, data):
        serial = load_serial()
        try:
            self.ser.write(data)
        except (serial.SerialException, OSError) as ex:
            raise LinkClosed("串口写失败：%s" % ex)

    def close(self):
        try:
            self.ser.close()
        except Exception:
            pass


def open_com(port, baud):
    """打开串口：8N1、无流控、timeout=0（轮询式读，和 TCP 那边的非阻塞语义一致）"""
    serial = load_serial()
    return serial.Serial(port=port, baudrate=int(baud), bytesize=8, parity="N", stopbits=1,
                         timeout=0, write_timeout=1.0, xonxoff=False, rtscts=False, dsrdtr=False)


# ---------------------------------------------------------------- 延迟统计

def build_lat_stats(samples):
    """与上位机 NpLatencyStats 同口径（§14.3）：Base=最小值，Avg=均值，Jitter=相对 Base 的平均绝对偏差"""
    if not samples:
        return None
    base = min(samples)
    avg = sum(samples) / len(samples)
    jitter = sum(abs(v - base) for v in samples) / len(samples)
    return {"count": len(samples), "base": base, "avg": avg, "jitter": jitter, "last": samples[-1]}


def format_lat(stats):
    if not stats:
        return "无样本"
    return "Base=%.2fms Avg=%.2fms Jitter=%.2fms 样本=%d" % (
        stats["base"], stats["avg"], stats["jitter"], stats["count"])


# ---------------------------------------------------------------- 服务端（CLI 与 GUI 共用）

class MockServer:
    """
    ESP32 模拟器的服务端核心：socket + 真 C 协议栈（np_ref.dll）的完整循环。

    设计要点：
      * **所有 DLL 调用都发生在本对象的后台线程里** —— C 侧是全局单例状态，多线程乱调必崩；
      * 外部（CLI / GUI）只通过 `snapshot()` 读状态、`post()` 投命令，全线程安全；
      * `serve_forever=True` 时断线后继续监听（GUI 场景：关掉上位机再开还能重连）；
      * 每次新连接都会 `np_dll_init / session_init / res_init / timeline_init`，避免上一次的残留状态 →
        所以 `id_counter` 每次归零，**SESSION_ID 只能靠宿主 tick 的高 16 位区分**：
        这里把 tick 打包成 `(第几次连接 << 16) | 连接内毫秒`（规范 §2.5：每次会话必须换新 ID）。
    """

    def __init__(self, dll, ip="127.0.0.1", port=9100, caps=0x1F, max_edge=240,
                 max_resource=262144, tick_ms=10, art_dir="art_recv", cover_format=0x10,
                 control_every=0.0, exit_after_active=False, run_seconds=0.0,
                 latency_samples=0, latency_interval_ms=100,
                 transport="tcp", com_port="COM23", baud=115200,
                 quiet=False, cli=False, serve_forever=False, on_event=None):
        self.dll = dll
        self.ip = ip
        self.port = port
        self.transport = (transport or "tcp").strip().lower()   # tcp / com
        self.com_port = com_port
        self.baud = int(baud)
        self.caps = caps
        self.max_edge = max_edge
        self.max_resource = max_resource
        self.tick_ms = tick_ms
        self.art_dir = art_dir
        self.cover_format = cover_format
        self.control_every = control_every
        self.exit_after_active = exit_after_active
        self.run_seconds = run_seconds
        self.latency_samples = latency_samples              # >0 = 会话建立后自动测一轮（0 = 只等人点）
        self.latency_interval_ms = latency_interval_ms
        self.quiet = quiet
        self.cli = cli                      # True = 同时打印到控制台（保持 CLI 行为）
        self.serve_forever = serve_forever
        self.on_event = on_event

        self.thread = None
        self.exit_code = None
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._events = collections.deque(maxlen=500)
        self._cmds = queue.Queue()
        self._conn_seq = 0                  # 第几次连接（决定 SESSION_ID 的高 16 位）

        self._st = {
            "running": False,
            "phase": "未启动",
            "peer": "",
            "conn_seq": 0,
            "session_state": 0,
            "session_name": "IDLE",
            "session_id": 0,
            "frames_rx": 0,
            "crc_errors": 0,
            "resyncs": 0,
            "tx_dropped": 0,
            "media": None,
            "timeline": None,
            "lyrics": None,
            "cover": None,
            "latency_responded": 0,
            "control_sent": 0,
            "ack_sent": 0,              # 本端回给对端的 ACK 数（资源 END / 校验失败）
            "resource": None,           # 资源请求状态机快照（在途 / 重试 / 上次 ACK）
            "latency": None,            # 最近一轮自测延迟的统计快照
        }

    # ---- 线程安全的对外接口 ----

    def _set(self, **kw):
        with self._lock:
            self._st.update(kw)

    def _bump(self, key, n=1):
        with self._lock:
            self._st[key] = self._st.get(key, 0) + n

    def snapshot(self):
        """给 GUI 用的状态快照（含最近日志；都是拷贝，调用方怎么改都不影响服务端）"""
        with self._lock:
            st = dict(self._st)
            st["events"] = list(self._events)
        return st

    def log(self, text):
        line = "[%s] %s" % (time.strftime("%H:%M:%S"), text)
        with self._lock:
            self._events.append(line)
        if self.cli:
            # ⚠ 别让日志打死服务端线程：Windows 控制台/重定向文件常见 GBK，
            # 打不出 ✓ ✗ ⚠ 这类字符会抛 UnicodeEncodeError（实测：整个 mock 线程直接挂掉，
            # 之后 MEDIA/时间轴/回控全断，看起来像"协议不对"）。
            try:
                print("[mock] %s" % text)
            except UnicodeEncodeError:
                enc = getattr(sys.stdout, "encoding", None) or "gbk"
                print(("[mock] %s" % text).encode(enc, "replace").decode(enc, "replace"))
            except Exception:
                pass
        if self.on_event:
            try:
                self.on_event(line)
            except Exception:
                pass

    def post(self, cmd, arg=None):
        """投一条命令给后台线程执行（GUI 按钮用）；带投递时刻，过时的命令会被丢弃"""
        self._cmds.put((cmd, arg, time.monotonic()))

    def start(self):
        if (self.thread is not None) and self.thread.is_alive():
            return
        self._stop.clear()
        self.thread = threading.Thread(target=self._run, name="mock-esp32", daemon=True)
        self.thread.start()

    def stop(self):
        self._stop.set()
        t = self.thread
        if (t is not None) and t.is_alive():
            t.join(timeout=2.0)
        self._set(running=False, phase="已停止")

    def is_alive(self):
        return (self.thread is not None) and self.thread.is_alive()

    # ---- 内部：命令队列（GUI 按钮 / CLI 投进来的动作）----

    CMD_STALE_S = 3.0                   # 投递后这么久还没轮到执行 → 当时肯定没连上，直接丢弃

    def _drain_cmds(self, active):
        while True:
            try:
                cmd, arg, posted = self._cmds.get_nowait()
            except queue.Empty:
                return

            age = time.monotonic() - posted
            if age > self.CMD_STALE_S:
                self.log("丢弃过时命令 %s（%.1fs 前投递时还没有上位机连上）" % (cmd, age))
                continue

            if cmd == "control":
                queued = self.dll.np_dll_send_control(arg)
                self._bump("control_sent")
                self.log("-> CONTROL %s（入队=%d）%s"
                         % (CTRL_NAMES.get(arg, arg), queued,
                            "" if active else "   ⚠ 会话未建立，对端可能不理它"))

            elif cmd == "req_lyrics":
                self._res_want("lyrics", "手动", force=True, active=active)

            elif cmd == "req_cover":
                self._res_want("cover", "手动", force=True, active=active)

            elif cmd == "latency":
                self.start_latency(arg, active=active)

            else:
                self.log("未知命令：%s（忽略）" % cmd)


    # ---- 内部：监听 + 每个连接的服务循环 ----

    def _run(self):
        self._set(running=True, phase="监听中")
        try:
            if self.transport == "com":
                self._run_com()
            else:
                self._run_tcp()
        except Exception as ex:                     # 兜底：别让后台线程带着裸 traceback 死掉
            self.log("✗ 服务端异常退出：%s" % ex)
            if self.cli:
                try:
                    import traceback
                    print(traceback.format_exc())
                except Exception:
                    pass
            self._set(running=False, phase="异常退出")
            if self.exit_code is None:
                self.exit_code = 4

    def _run_tcp(self):
        self.log("传输=TCP；监听 %s:%d  CAPS=0x%08X maxEdge=%d maxRes=%d"
                 % (self.ip, self.port, self.caps, self.max_edge, self.max_resource))
        self.log("等待上位机连接（New 模式 / TCP / 对端填 %s:%d）..." % (self.ip, self.port))

        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind((self.ip, self.port))
        srv.listen(1)
        srv.settimeout(0.3)                     # 让 stop() 能及时退出

        try:
            while not self._stop.is_set():
                try:
                    conn, addr = srv.accept()
                except socket.timeout:
                    continue
                except OSError:
                    break

                conn.setblocking(False)
                link = TcpLink(conn, addr)
                try:
                    self._serve(link)
                except LinkClosed as ex:
                    self.log("链路断开：%s" % ex)
                finally:
                    link.close()

                if not self.serve_forever:
                    break
        finally:
            try:
                srv.close()
            except OSError:
                pass
            self._set(running=False, phase="已停止", peer="")
            self.log("服务端已停止")

    def _run_com(self):
        """串口是点对点链路：打开一次，一直服务；出错（拔线/对端异常）后重开。"""
        self.log("传输=COM；串口 %s @ %d  CAPS=0x%08X maxEdge=%d maxRes=%d（8N1，无流控）"
                 % (self.com_port, self.baud, self.caps, self.max_edge, self.max_resource))
        self.log("等待上位机（New 模式 / 传输=COM / 端口填配对的那一端，例如 com0com 的另一半）...")

        try:
            link = ComLink(open_com(self.com_port, self.baud), self.com_port, self.baud)
        except Exception as ex:
            self.log("✗ 打开串口 %s 失败：%s" % (self.com_port, ex))
            self.log("   请检查：串口号是否正确 / 是否被浏览器或串口助手占用 / "
                     "是否装好了虚拟串口对（com0com 等：上位机一端、mock 另一端）")
            self._set(running=False, phase="串口打开失败")
            self.exit_code = 4
            return

        self.log("串口已打开：%s" % link.label)
        try:
            while not self._stop.is_set():
                try:
                    self._serve(link)
                except LinkClosed as ex:
                    self.log("链路断开：%s" % ex)
                if not self.serve_forever:
                    break
                if self._stop.is_set():
                    break
                self.log("等待对端重新连接（串口保持打开）...")
                time.sleep(0.3)
        finally:
            link.close()
            self._set(running=False, phase="已停止", peer="")
            self.log("服务端已停止")

    def _serve(self, link):
        # 规范 §2.5：每次会话的 SESSION_ID 必须是新值。C 侧用 (now_ms 高 16 位 | id_counter) 生成，
        # 而这里每次连接都会重新 init（id_counter 归零），所以只能靠宿主 tick 的高 16 位来区分会话：
        # 把 tick 打包成 (第几次连接 << 16) | 连接内毫秒 → SESSION_ID 一眼可读（0x0001xxxx / 0x0002xxxx…）。
        self._conn_seq += 1
        epoch = self._conn_seq & 0xFFFF
        tick_base = epoch << 16

        self._set(peer=link.label, phase="握手…", conn_seq=self._conn_seq)
        self.log("上位机已连接：%s（第 %d 次会话，链路=%s）" % (link.label, self._conn_seq, link.kind))

        t0 = time.monotonic()
        self.dll.np_dll_set_time_ms(tick_base)
        self.dll.np_dll_init()
        self.dll.np_dll_res_init()
        self.dll.np_dll_timeline_init()
        self.dll.np_dll_session_init(self.caps, self.max_edge, self.max_resource, 0)   # 0 = ESP32 角色

        active = False
        self._delivered = 0
        self._media_key = None
        self._last_sid = 0
        self._reset_res_state()
        self._reset_lat_state()
        last_state = None
        last_control = time.monotonic()
        txbuf = ctypes.create_string_buffer(2048)

        try:
            # 链路就绪的判定：TCP = accept 成功即对端在；COM = 串口是点对点，open 成功**不代表**上位机在。
            # 实测踩过：上位机比 mock 晚 2 秒打开串口，mock 的 HELLO 重发（500ms×3）早已用尽 → 直接"握手失败"退出。
            # 所以串口下先等到上位机的第一个字节，再决定要不要主动发 HELLO。
            if (link.kind == "COM") and (self.dll.np_dll_session_state() == 0):
                self.log("串口已打开，等待上位机第一个字节（收到后才开始握手）...")
                while not self._stop.is_set():
                    self.dll.np_dll_set_time_ms(tick_base | (int((time.monotonic() - t0) * 1000) & 0xFFFF))
                    data = link.read(4096)
                    if data is not None:
                        self.dll.np_dll_feed(data, len(data))
                        self.log("收到上位机 %d 字节 → 链路就绪" % len(data))
                        break
                    if (self.run_seconds > 0.0) and ((time.monotonic() - t0) >= self.run_seconds):
                        self.log("等上位机超时（--run-seconds=%.0fs）" % self.run_seconds)
                        return
                    time.sleep(0.01)

            if self.dll.np_dll_session_state() == 0:
                self.dll.np_dll_session_link_up()   # 链路就绪 → 真 C 代码立刻组出 HELLO
                self.log("已发起 HELLO（ESP32 角色，主动）；tick 基址=0x%08X → 本次 SESSION_ID 形如 0x%04Xxxxx"
                         % (tick_base, epoch))
            else:
                self.log("对端已先发起 HELLO → 本端只应答并开会话（不重复 link_up，省一轮往返）")

            while not self._stop.is_set():
                # 连接内毫秒（高 16 位留给会话序号，所以这里要掩掉）
                self.dll.np_dll_set_time_ms(tick_base | (int((time.monotonic() - t0) * 1000) & 0xFFFF))

                # 0) 执行 GUI/CLI 投进来的命令
                self._drain_cmds(active)

                # 1) 链路 → C 解析器
                data = link.read(4096)
                if data is not None:
                    self.dll.np_dll_feed(data, len(data))

                # 2) 取出解析好的帧 → 资源 / 时间轴 / 延迟 / ACK / 触发拉资源
                self._dispatch_frames(active)

                # 2.5) 资源请求状态机（NOT_READY 重试 / 串行化 / 超时）与延迟测量节拍
                self._res_tick(active)
                self._lat_tick(active)

                # 3) 让 C 会话机跑一次（重传 / 超时），取出要发的帧发走
                self.dll.np_dll_session_tick()
                while True:
                    n = self.dll.np_dll_tx_poll(txbuf, len(txbuf))
                    if n <= 0:
                        break
                    if not self.quiet:
                        print("[mock] -> %d 字节: %s" % (n, txbuf.raw[:n].hex(" ").upper()))
                    link.write(txbuf.raw[:n])

                # 3.5) 会话 ID 变化（重连 / 串口下重新握手）→ 上一会话的资源请求状态作废
                sid = self.dll.np_dll_session_id()
                if sid != self._last_sid:
                    self._last_sid = sid
                    if sid != 0:
                        self.log("新的 SESSION_ID=0x%08X → 清空上一会话的媒体/资源请求状态" % sid)
                    self._media_key = None
                    self._reset_res_state()

                # 4) 会话状态
                st = self.dll.np_dll_session_state()
                if st != last_state:
                    last_state = st
                    self.log("会话状态 -> %s" % STATE_NAMES.get(st, st))
                    self._set(session_state=st, session_name=STATE_NAMES.get(st, "?"),
                              session_id=self.dll.np_dll_session_id())
                    if st == 4:
                        active = True
                        self._set(phase="SESSION ACTIVE")
                        self.log("=== SESSION ACTIVE, id=0x%08X ===" % self.dll.np_dll_session_id())
                        if (self.latency_samples > 0) and (not self._lat["auto_done"]):
                            self._lat["auto_done"] = True
                            self.start_latency(self.latency_samples, active=True)
                        if self.exit_after_active:
                            time.sleep(0.3)          # 留点时间把最后的 ACK 发出去
                            break

                # 5) 按键回控（可选）：周期发 CONTROL，验证上位机侧执行链路
                if active and (self.control_every > 0.0):
                    if (time.monotonic() - last_control) >= self.control_every:
                        last_control = time.monotonic()
                        queued = self.dll.np_dll_send_control(1)      # 1 = PLAY_PAUSE
                        self._bump("control_sent")
                        self.log("-> CONTROL PLAY_PAUSE（入队=%d）" % queued)

                # 6) 运行时长兜底（便于脚本化验证）
                if (self.run_seconds > 0.0) and ((time.monotonic() - t0) >= self.run_seconds):
                    self.log("到达 --run-seconds，退出")
                    break

                if self.dll.np_dll_session_handshake_failed():
                    self.log("握手失败（HELLO 重试用尽）—— 对端没应答？")
                    break

                time.sleep(self.tick_ms / 1000.0)
        finally:
            self._refresh_stats()
            self.log("统计：帧=%d crc错=%d 重同步=%d tx丢弃=%d 会话=%s"
                     % (self._st["frames_rx"], self._st["crc_errors"], self._st["resyncs"],
                        self._st["tx_dropped"], STATE_NAMES.get(self.dll.np_dll_session_state(), "?")))
            self.log("时间轴投递给 sink 的帧数=%d  延迟请求应答数=%d  ACK 发出=%d"
                     % (self.dll.np_dll_timeline_delivered(), self._st["latency_responded"],
                        self._st["ack_sent"]))
            self.log("资源请求小结：%s" % self._res_summary())
            if self._lat["stats"] is not None:
                self.log("本轮延迟（本端发起，rid=%d）：%s" % (self._lat["rid"], format_lat(self._lat["stats"])))
            self._set(phase="监听中" if self.serve_forever else "已结束")
            self.exit_code = 0 if active else 3
            if not self.serve_forever:
                self._stop.set()            # CLI：一次连接结束就退出

    # ---- 内部：单帧处理（CLI 与 GUI 共用同一份逻辑） ----

    def _dispatch_frames(self, active):
        while True:
            f = NpFrameOut()
            if self.dll.np_dll_poll(ctypes.byref(f)) != 1:
                break
            if not self.quiet:
                print_frame(f)

            # §5.6 约定表（V1.1-21）：回不回 ACK 只看 FLAGS.ACK_REQUIRED。
            # "要不要回"由位决定，"回什么 STATUS"由处理结果决定 —— 下面谁处理了谁回。
            need_ack = (f.flags & NP_FLAG_ACK_REQUIRED) != 0
            acked = False

            if f.type in (5, 6):
                ev = self.dll.np_dll_res_on_frame(ctypes.byref(f))
                if ev == 1:
                    self.log("资源 BEGIN（%s rid=%d）" % (KIND_NAMES.get(f.type, f.type), f.request_id))
                    self._res_activity(f)
                elif ev == 2:
                    self._res_activity(f)                 # 有数据在进来 → 刷新在途看门狗
                elif ev == 3:
                    # 收齐且 CRC32 通过：先记结果，再**立刻回 ACK**（本帧就是 END、带着位）——
                    # 顺序很重要：ACK 必须早于 _res_on_done 排出的下一个 REQUEST，
                    # 否则发送方还没收到这份 END 的 ACK 就先看到新 REQUEST，会按 §9.7 N-13 ABORT 掉这一份
                    # （实测：ABORT 计数从 0 变 2；rid 守卫让它不至于变成风暴，但语义已经错了）。
                    self._res_result = {"rid": f.request_id, "status": ACK_OK}
                    acked = self._ack_if_needed(f, ACK_OK, "资源 END（§9.5）")
                    self._on_resource_done(f.type)
                    self._res_on_done(f.type, f.request_id)   # 收齐 → 串行队列里的下一个
                elif ev == 4:
                    err = self.dll.np_dll_res_last_err()
                    self.log("资源接收错误：%s（§9.5：回 ACK(ERROR) + ERROR(RESOURCE_FAIL) 后用新 REQUEST_ID 重来）"
                             % RES_ERR_NAMES.get(err, "?"))
                    self._res_result = {"rid": f.request_id, "status": ACK_ERROR}
                    acked = self._ack_if_needed(f, ACK_ERROR, "资源校验失败")
                    self.dll.np_dll_send_error(NP_ERR_RESOURCE_FAIL, f.request_id, err)
                    self._res_retry_after_loss(f.type, f.request_id, "校验/分片错误")
                elif ev == 5:
                    # ABORT 不带 ACK（§9.1 时序图 / §5.6 约定表）；只有"当前在途的那一份"才需要重来（§9.6）
                    self._res_retry_after_loss(f.type, f.request_id, "被 ABORT")

                # 补一条 ACK：**空资源（TOTAL_SIZE=0）**时 C 侧在 BEGIN 帧就报 DONE（BEGIN 没有位 → 不回），
                # 等 END 帧到达时 ev 已经是 NONE —— 只按事件回 ACK 就会漏掉它，
                # 发送方会一直认为没传完（实测：上位机 _current 挂着，切歌时多发一次 ABORT）。
                if (not acked) and (f.code == 0x04) and (self._res_result is not None) \
                        and (self._res_result["rid"] == f.request_id):
                    acked = self._ack_if_needed(f, self._res_result["status"], "资源 END（空资源，§9.5）")

            elif (f.type == 1) and (f.code == 0x05):
                self._on_ack(f)                            # ACK(NOT_READY/BUSY) → 换新 rid 重试（§9.8）

            elif (f.type == 1) and (f.code == 0x06):
                payload = bytes(f.payload[:f.payload_len])
                self.log("对端 ERROR：code=0x%02X rid=%d detail=%d（封面 REJECTED 时 detail=资源字节数）"
                         % (payload[0] if payload else 0, f.request_id,
                            _be32(payload, 5) if len(payload) >= 9 else 0))

            elif (f.type == 3) and (f.code == 0x01):
                # 时间轴：交给 C 的投递层（真机在这里接 Sync_OnPacketAt）
                self.dll.np_dll_timeline_on_frame(ctypes.byref(f))
                self._delivered += 1
                self._on_timeline()
                if (self._delivered % 20) == 0:
                    self.log("时间轴已投递 %d 帧（playing=%d cur=%dms total=%dms host_tick=%d local_tick=%d）"
                             % (self.dll.np_dll_timeline_delivered(),
                                self.dll.np_dll_timeline_last_playing(),
                                self.dll.np_dll_timeline_last_current_ms(),
                                self.dll.np_dll_timeline_last_total_ms(),
                                self.dll.np_dll_timeline_last_host_tick(),
                                self.dll.np_dll_timeline_last_local_tick()))

            elif (f.type == 1) and (f.code == 0x10):
                # 应答端：原样回传 T1，并带上"本端处理耗时"（微秒）
                payload = bytes(f.payload[:f.payload_len])
                t1 = _be32(payload, 0) if len(payload) >= 4 else 0
                self.dll.np_dll_latency_respond(f.request_id, t1, LAT_SIM_PROC_US)
                self._bump("latency_responded")
                lat = self._lat
                lat["peer_req"] += 1
                now = time.monotonic()
                if (lat["peer_req"] == 1) or ((now - lat["peer_req_log"]) > 2.0):
                    lat["peer_req_log"] = now
                    self.log("对端发起延迟测量（rid=%d）→ 本端应答（PROC_US=%d）%s"
                             % (f.request_id, LAT_SIM_PROC_US,
                                "；本端自测不受影响（§15：ESP32 优先）" if lat["running"] else ""))

            elif (f.type == 1) and (f.code == 0x11):
                self._lat_on_response(f)

            elif (f.type == 1) and (f.code == 0x12):
                self._lat_on_end(f)

            elif f.type == 2:
                self._on_media(f, active)

            # 安全网（§5.6 约定表）：置了 ACK_REQUIRED 却没有处理层回 ACK 的帧，明确报出来 ——
            # 否则对端会一直等 ACK 到超时，而日志里什么都看不到（这正是"END 漏 ACK"当初的样子）。
            if need_ack and (not acked):
                self.log("⚠ T=0x%02X C=0x%02X 置了 ACK_REQUIRED 但本端没有对应处理层 → 未回 ACK"
                         "（收到该帧的一方本应回 ACK，§5.6 约定表）" % (f.type, f.code))

    # ---- 内部：资源请求状态机（规范 §9.6 / §9.7 / §9.8）----

    def _reset_res_state(self, clear_dll=True):
        """每次新连接（= 新会话）或切歌时都要重来一遍"""
        if clear_dll:
            self.dll.np_dll_res_init()        # C 侧接收器也清干净，免得旧资源残留
        self._res = {
            "queue": [],                      # 待请求的资源：靠队列把两种资源串行化（§9.7）
            "inflight": None,                 # {"kind","rid","phase","sent","deadline"}
            "retry": {"lyrics": 0, "cover": 0},
            "done": {},                       # kind → "已收齐" / "放弃：原因"
            "last_ack": "",
        }
        self._res_result = None               # 最近一份资源的处理结果 {"rid","status"}，供 END 帧回 ACK 用
        self._res_push_state()

    def _res_push_state(self):
        infl = self._res["inflight"]
        self._set(resource={
            "inflight": ("%s rid=%d %s（重试 %d）"
                         % (infl["kind"], infl["rid"], infl["phase"], self._res["retry"][infl["kind"]])
                         if infl else None),
            "queue": list(self._res["queue"]),
            "done": dict(self._res["done"]),
            "last_ack": self._res["last_ack"],
        })

    def _res_want(self, kind, reason, force=False, active=True):
        """登记「想要这个资源」；force = 手工按钮（同种资源在途时立刻重来，对端会 ABORT 旧的）"""
        r = self._res
        if not active:
            self.log("⚠ 会话未建立 → 先排队要 %s（%s），SESSION ACTIVE 后再发" % (kind, reason))

        if force:
            r["retry"][kind] = 0
            r["done"].pop(kind, None)
            infl = r["inflight"]
            if (infl is not None) and (infl["kind"] == kind):
                self.log("%s：手工重来 → 丢掉在途的这一轮（对端会 ABORT 旧资源，§9.7）" % kind)
                r["inflight"] = None
                self.dll.np_dll_res_init()
            if kind in r["queue"]:
                r["queue"].remove(kind)
        elif (kind in r["queue"]) or ((r["inflight"] is not None) and (r["inflight"]["kind"] == kind)):
            return                                     # 已经排上 / 在途 → 不重复插队

        r["queue"].append(kind)
        self.log("计划请求 %s（%s）%s"
                 % (kind, reason,
                    "；当前在途 %s，按 §9.7 串行排队" % r["inflight"]["kind"] if r["inflight"] else ""))
        self._res_push_state()
        self._res_pump(active)

    def _res_pump(self, active):
        """队列非空且在途为空 → 发下一个（§9.7：同一时刻只允许一个资源在途）"""
        r = self._res
        if (not active) or (r["inflight"] is not None) or (not r["queue"]):
            return
        self._res_send(r["queue"].pop(0))

    def _res_send(self, kind, why="首次"):
        """真正发一条 REQUEST；rid 由 C 侧分配（每次调用都是新 rid）"""
        if kind == "lyrics":
            queued = self.dll.np_dll_send_request_lyrics()
        else:
            queued = self.dll.np_dll_send_request_cover(self.max_edge, self.max_edge,
                                                        self.cover_format, 0)

        if not queued:
            self.log("⚠ %s REQUEST 入队失败（会话未建立 / 发送队列满）" % kind)
            self._res["queue"].insert(0, kind)         # 放回队首，下个 tick 再试
            return

        rid = self.dll.np_dll_last_rid()
        now = time.monotonic()
        retry = self._res["retry"][kind]
        self._res["inflight"] = {"kind": kind, "rid": rid, "phase": "等 ACK",
                                 "sent": now, "deadline": now}
        extra = (" %dx%d fmt=0x%02X" % (self.max_edge, self.max_edge, self.cover_format)
                 if kind == "cover" else "")
        self.log("-> %s REQUEST%s rid=%d（%s%s）"
                 % (kind.upper(), extra, rid, why,
                    "，第 %d/%d 次" % (retry, RES_MAX_RETRY) if retry else ""))
        self._res_push_state()

    def _res_tick(self, active):
        r = self._res
        if not active:
            return
        self._res_pump(active)

        infl = r["inflight"]
        if infl is None:
            return

        now = time.monotonic()
        if infl["phase"] == "等重试":
            if now < infl["deadline"]:
                return
            kind = infl["kind"]                            # §9.8-3：用新的 REQUEST_ID 重发
            r["inflight"] = None
            self._res_send(kind, "NOT_READY/BUSY 后换新 rid 重发")
            return

        if (now - infl["sent"]) > RES_IDLE_TIMEOUT_S:
            kind = infl["kind"]
            self.log("⚠ %s 在途 %.1fs 毫无动静 → 换新 rid 重发" % (kind, now - infl["sent"]))
            r["retry"][kind] += 1
            r["inflight"] = None
            if r["retry"][kind] > RES_MAX_RETRY:
                self._res_give_up(kind, "在途超时且重试用尽")
            else:
                self._res_send(kind, "超时重发")

    def _res_activity(self, f):
        """BEGIN / 进度：刷新在途看门狗（有字节进来就不算卡住）"""
        infl = self._res["inflight"]
        if (infl is not None) and (f.request_id == infl["rid"]):
            infl["sent"] = time.monotonic()

    def _res_on_done(self, rtype, rid=None):
        """资源收齐 → 出队，继续拉下一个（歌词收齐才会轮到封面）"""
        kind = KIND_NAMES.get(rtype)
        if kind is None:
            return
        r = self._res
        infl = r["inflight"]
        if (infl is not None) and (rid is not None) and (rid != infl["rid"]):
            self.log("收到旧 rid=%d 的 %s 收齐事件（当前在途 rid=%d）→ 只记录，不改状态"
                     % (rid, kind, infl["rid"]))
            return
        r["retry"][kind] = 0
        r["done"][kind] = "已收齐"
        if (infl is not None) and (infl["kind"] == kind):
            r["inflight"] = None
        self._res_push_state()
        self._res_pump(True)

    def _res_retry_after_loss(self, rtype, rid, why):
        """§9.6 / §9.7：资源没了（ABORT / 校验失败）→ 用新的 REQUEST_ID 重新 REQUEST。

        关键：只对**当前在途那一份**重来。对端在收到新 REQUEST 时会 ABORT 旧的（§9.7 N-13），
        那个 ABORT 指的是已经作废/已收齐的旧 rid —— 若跟着重发就会变成 ABORT↔REQUEST 风暴。
        """
        kind = KIND_NAMES.get(rtype)
        if kind is None:
            return
        r = self._res
        infl = r["inflight"]

        if infl is None:
            self.log("收到 %s 的 ABORT rid=%d（本端当前没有在途资源：多半是上一次已收齐/已放弃的）→ 不重发"
                     % (kind, rid))
            return
        if (rid is not None) and (rid != infl["rid"]):
            self.log("忽略旧 rid=%d 的 %s ABORT（当前在途 rid=%d）→ 不重发，避免风暴"
                     % (rid, kind, infl["rid"]))
            return

        self.log("%s 被 %s（当前在途 rid=%d）" % (kind, why, infl["rid"]))
        r["inflight"] = None
        self.dll.np_dll_res_init()
        r["retry"][kind] += 1
        if r["retry"][kind] > RES_MAX_RETRY:
            self._res_give_up(kind, why)
            return
        self.log("%s 重新 REQUEST（%s，第 %d/%d 次）" % (kind, why, r["retry"][kind], RES_MAX_RETRY))
        if kind in r["queue"]:
            r["queue"].remove(kind)
        r["queue"].insert(0, kind)
        self._res_push_state()
        self._res_pump(True)

    def _ack_if_needed(self, f, status, why=""):
        """§5.6 约定表（V1.1-21）：**只有置了 `ACK_REQUIRED` 的帧才回 ACK**（位是唯一判据）"""
        if (f.flags & NP_FLAG_ACK_REQUIRED) == 0:
            self.log("本帧未置 ACK_REQUIRED → 按 §5.6 不回 ACK（T=0x%02X C=0x%02X%s）"
                     % (f.type, f.code, "，" + why if why else ""))
            return False
        self._ack(f, status, why)
        return True

    def _ack(self, f, status, why=""):
        """回一条 ACK（§5.6）：SEQUENCE / REQUEST_ID 沿用被 ACK 的帧"""
        ok = self.dll.np_dll_send_ack(f.sequence, f.request_id, status)
        self._bump("ack_sent")
        # DATA 不逐片 ACK（§9.4），所以这里只可能是 BEGIN/END 级别的应答，日志不会刷屏
        self.log("-> ACK(%s) ack_seq=%d rid=%d%s（入队=%d）"
                 % (ACK_STATUS_NAMES.get(status, status), f.sequence, f.request_id,
                    "  " + why if why else "", ok))

    def _res_give_up(self, kind, why):
        self._res["retry"][kind] = 0
        self._res["done"][kind] = "放弃：%s" % why
        self.log("✗ 放弃 %s：%s（可点对应按钮再来一次）" % (kind, why))
        if (kind == "cover") and ("NOT_READY" in why):
            # 规范 §11：**"这首没有封面"必须用空资源（FORMAT=0x00 / TOTAL_SIZE=0）表达**；
            # NOT_READY 只表示"暂时还没读出来"（§9.8-3），不该被用来表达"就是没有"。
            # 若对端一直回 NOT_READY：先看它的事件日志里有没有"封面：SMTC 已通知本曲目无封面"那一行
            # （没有的话多半是它没把"无封面"这个状态通知给资源层，例如封面去重把首个 null 事件挡掉了）。
            self.log("   提示（§11 / §9.8-3）：一直 NOT_READY 通常是上位机把\"没有封面\"当成了\"还没就绪\" —— "
                     "规范要求无封面用**空资源 FORMAT=0x00** 表达；可核对上位机日志里是否有"
                     "\"封面：SMTC 已通知本曲目无封面\"")
        self._res_push_state()
        self._res_pump(True)                            # 放弃一个不代表放弃另一个

    def _res_summary(self):
        r = self._res
        parts = ["%s=%s" % (k, r["done"].get(k, "未请求")) for k in ("lyrics", "cover")]
        infl = r["inflight"]
        if infl:
            parts.append("在途=%s(rid=%d,%s)" % (infl["kind"], infl["rid"], infl["phase"]))
        if r["queue"]:
            parts.append("排队=%s" % ",".join(r["queue"]))
        return "  ".join(parts)

    def _on_ack(self, f):
        """对端对 REQUEST 的应答：OK 等数据；NOT_READY/BUSY 换新 rid 重试；其余记录并放弃"""
        payload = bytes(f.payload[:f.payload_len])
        if len(payload) < 5:
            self.log("收到 ACK 但 payload 不足 5 字节（忽略）")
            return

        status = payload[4]
        name = ACK_STATUS_NAMES.get(status, "0x%02X" % status)
        r = self._res
        infl = r["inflight"]

        if (infl is None) or (f.request_id != infl["rid"]):
            self.log("ACK(%s) rid=%d —— 非资源请求的应答（SESSION_START / CONTROL / 延迟之类），只记一笔"
                     % (name, f.request_id))
            return

        kind = infl["kind"]
        r["last_ack"] = "%s → %s" % (kind, name)

        if status == ACK_OK:
            infl["phase"] = "收数据"
            infl["sent"] = time.monotonic()
            self.log("ACK(OK) %s rid=%d → 等 BEGIN/DATA/END" % (kind, infl["rid"]))
        elif status in (ACK_NOT_READY, ACK_BUSY):
            r["retry"][kind] += 1
            if r["retry"][kind] > RES_MAX_RETRY:
                r["inflight"] = None
                self._res_give_up(kind, "连续 ACK(%s) 达 %d 次" % (name, RES_MAX_RETRY))
                return
            wait = RES_RETRY_NOT_READY_S if status == ACK_NOT_READY else RES_RETRY_BUSY_S
            infl["phase"] = "等重试"
            infl["deadline"] = time.monotonic() + wait
            self.log("ACK(%s) %s rid=%d → %.0fms 后用新 REQUEST_ID 重试（第 %d/%d 次，§9.8）"
                     % (name, kind, infl["rid"], wait * 1000, r["retry"][kind], RES_MAX_RETRY))
        else:
            self.log("ACK(%s) %s rid=%d → 放弃本轮（不自动重试，可点手工按钮再来）"
                     % (name, kind, infl["rid"]))
            r["inflight"] = None
            r["done"][kind] = "放弃：ACK(%s)" % name

        self._res_push_state()
        if r["inflight"] is None:
            self._res_pump(True)

    # ---- 内部：延迟测量（规范 §14 / §15；本端也能当发起方）----

    def _reset_lat_state(self):
        self._lat = {"running": False, "rid": 0, "target": LAT_WINDOW, "interval": LAT_INTERVAL_S,
                     "sent": 0, "recv": 0, "samples": [], "pending": {}, "last_t1": 0,
                     "last_sent": 0.0, "last_recv": 0.0, "stats": None,
                     "auto_done": False, "peer_req": 0, "peer_req_log": 0.0}
        self._set(latency=None)

    def start_latency(self, samples=None, active=True):
        """发起一轮延迟测量（§14）：整轮共用一个 REQUEST_ID，窗口满/超时后用 LATENCY_END 收尾"""
        if not active:
            self.log("⚠ 延迟测量需要会话已建立（SESSION ACTIVE）—— 现在没连上，忽略本次请求")
            return False
        if self._lat["running"]:
            self.log("本端已有一轮延迟测量在进行（rid=%d，样本 %d/%d）→ 不重复发起"
                     % (self._lat["rid"], len(self._lat["samples"]), self._lat["target"]))
            return False

        n = int(samples or LAT_WINDOW)
        n = max(1, min(n, 1000))
        lat = self._lat
        lat.update({"running": True, "rid": 0, "target": n, "sent": 0, "recv": 0,
                    "samples": [], "pending": {}, "last_t1": 0, "stats": None})
        lat["interval"] = max(0.02, self.latency_interval_ms / 1000.0)
        lat["last_sent"] = -1e9
        lat["last_recv"] = time.monotonic()
        self.log("开始延迟测量（本端发起 %d 样本，间隔 %dms；rid 交给 C 侧分配）"
                 % (n, self.latency_interval_ms))
        self._lat_send()
        return True

    def _lat_send(self):
        lat = self._lat
        # T1 = 本端 tick（发起方本地 tick，规范 §14.2）；保证严格递增，避免配对歧义
        t1 = self.dll.np_dll_get_time_ms()
        if lat["pending"] and (t1 <= lat["last_t1"]):
            t1 = (lat["last_t1"] + 1) & 0xFFFFFFFF

        rid = self.dll.np_dll_latency_request(lat["rid"], t1)     # rid=0 → C 侧分配本轮 rid 并返回
        if rid == 0:
            self.log("✗ LATENCY_REQUEST 入队失败（会话掉了？）→ 结束本轮")
            self._lat_finish("发送失败", send_end=False)
            return

        lat["rid"] = rid
        lat["last_t1"] = t1
        lat["pending"][t1] = time.monotonic() * 1000.0
        lat["sent"] += 1
        lat["last_sent"] = time.monotonic()

    def _lat_tick(self, active):
        lat = self._lat
        if not lat["running"]:
            return
        if not active:
            self.log("会话断开 → 本轮延迟测量作废（rid=%d）" % lat["rid"])
            lat["running"] = False
            self._set(latency=None)
            return

        now = time.monotonic()
        if (lat["sent"] < lat["target"]) and ((now - lat["last_sent"]) >= lat["interval"]):
            self._lat_send()
            return

        # §14.4：整轮 5s 没有任何有效样本 → 发 END 并标记异常结束
        if (lat["sent"] > 0) and ((now - lat["last_recv"]) > LAT_TIMEOUT_S):
            self.log("整轮 %.0fs 无任何有效回包（超时）" % LAT_TIMEOUT_S)
            self._lat_finish("超时：连续无回包", send_end=True)

    def _lat_on_response(self, f):
        """收到 RESPONSE：用 T1 精确配对，RTT 与单向延迟都按 §14.3 计算"""
        payload = bytes(f.payload[:f.payload_len])
        if len(payload) < 8:
            return

        lat = self._lat
        if (not lat["running"]) or (f.request_id != lat["rid"]):
            return

        t1 = _be32(payload, 0)
        proc_us = _be32(payload, 4)
        sent_ms = lat["pending"].pop(t1, None)
        if sent_ms is None:
            self.log("收到 T1=%d 的 LATENCY_RESPONSE 但没有对应记录（上一轮残留？）→ 丢弃" % t1)
            return

        rtt = max(0.0, (time.monotonic() * 1000.0) - sent_ms)          # T2 - T1
        one_way = max(0.0, (rtt - (proc_us / 1000.0)) / 2.0)           # §14.3
        lat["recv"] += 1
        lat["last_recv"] = time.monotonic()
        lat["samples"].append(one_way)
        lat["stats"] = build_lat_stats(lat["samples"])
        self._set(latency={"running": True, "sent": lat["sent"], "recv": lat["recv"],
                           "stats": lat["stats"], "note": "测量中"})

        n = len(lat["samples"])
        if (n == 1) or ((n % 10) == 0):
            self.log("延迟样本 %d/%d：RTT=%.2fms PROC_US=%d → 单向 %.2fms（%s）"
                     % (n, lat["target"], rtt, proc_us, one_way, format_lat(lat["stats"])))

        if lat["recv"] >= lat["target"]:
            self._lat_finish("窗口已满", send_end=True)

    def _lat_on_end(self, f):
        payload = bytes(f.payload[:f.payload_len])
        count = _be16(payload, 0) if len(payload) >= 2 else 0
        lat = self._lat
        if lat["running"] and (f.request_id == lat["rid"]):
            self.log("对端发来 LATENCY_END（样本数=%d）→ 结束本轮" % count)
            self._lat_finish("对端结束本轮", send_end=False)
        else:
            self.log("对端结束了一轮延迟测量（rid=%d 样本数=%d；本轮不是本端发起的）" % (f.request_id, count))

    def _lat_finish(self, reason, send_end):
        lat = self._lat
        if not lat["running"]:
            return

        lat["running"] = False
        stats = lat["stats"]
        if send_end:
            queued = self.dll.np_dll_latency_end(lat["rid"], lat["recv"])
            self.log("-> LATENCY_END（rid=%d 样本=%d 入队=%d）" % (lat["rid"], lat["recv"], queued))

        if stats:
            self.log("=== 本轮延迟结束（%s）：%s" % (reason, format_lat(stats)))
        else:
            self.log("=== 本轮延迟结束（%s）：没有拿到任何有效样本" % reason)
        self._set(latency={"running": False, "sent": lat["sent"], "recv": lat["recv"],
                           "stats": stats, "note": reason})

    # ---- 内部：状态快照更新 ----

    def _on_media(self, f, active=True):
        payload = bytes(f.payload[:f.payload_len])
        if len(payload) < 6:
            return

        p = 0
        tlen = _be16(payload, p); p += 2
        title = _utf8(payload, p, tlen); p += tlen
        alen = _be16(payload, p); p += 2
        artist = _utf8(payload, p, alen); p += alen
        blen = _be16(payload, p); p += 2
        album = _utf8(payload, p, blen)

        self._set(media={"title": title, "artist": artist, "album": album})

        key = (title, artist, album)
        changed = (key != self._media_key)
        self._media_key = key
        self.log("MEDIA：%s - %s（专辑 %s）%s"
                 % (title or "(空)", artist or "(空)", album or "(空)",
                    "" if changed else "   [同曲目重复帧，不重复拉资源]"))
        if not changed:
            return

        # 曲目变了（或本次会话第一次收到）→ 重新计划两种资源；队列保证"先歌词后封面"（§9.7）
        if not (title or artist or album):
            self.log("空元数据（当前无媒体）→ 不请求资源，仅清屏（§6）")
            self._reset_res_state()
            return

        self._reset_res_state()
        self._res_want("lyrics", "收到 MEDIA（新曲目）→ 自动拉歌词", active=active)
        self._res_want("cover", "收到 MEDIA（新曲目）→ 自动拉封面", active=active)

    def _on_timeline(self):
        self._set(timeline={
            "playing": self.dll.np_dll_timeline_last_playing() == 1,
            "cur_ms": self.dll.np_dll_timeline_last_current_ms(),
            "total_ms": self.dll.np_dll_timeline_last_total_ms(),
            "host_tick": self.dll.np_dll_timeline_last_host_tick(),
            "local_tick": self.dll.np_dll_timeline_last_local_tick(),
            "delivered": self.dll.np_dll_timeline_delivered(),
        })

    def _on_resource_done(self, rtype):
        size = self.dll.np_dll_res_size()
        fmt = self.dll.np_dll_res_format()
        w = self.dll.np_dll_res_width()
        h = self.dll.np_dll_res_height()

        buf = ctypes.create_string_buffer(max(size, 1))
        n = self.dll.np_dll_res_copy(buf, len(buf))
        data = bytes(buf.raw[:n])

        kind = "lyrics" if rtype == 5 else "cover"
        path = os.path.join(self.art_dir, "%s.%s" % (kind, FMT_EXT.get(fmt, "bin")))
        try:
            os.makedirs(self.art_dir, exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
        except OSError as ex:
            self.log("资源落盘失败：%s" % ex)

        if rtype == 5:
            lines = parse_lyric_lines(data)
            self._set(lyrics={"size": size, "path": path, "format": fmt,
                              "frames": count_legacy_lyric_frames(data), "lines": lines})
            self.log("=== 歌词收齐：%d 字节 / %d 行（CRC32 由 C 侧校验通过）→ %s"
                     % (size, len(lines), path))
        else:
            self._set(cover={"size": size, "path": path, "format": fmt,
                             "w": w, "h": h, "data": data})
            if size == 0 or fmt == 0x00:
                # §11：**空资源（FORMAT=0x00 / TOTAL_SIZE=0）= 对端明确告知"这首没有封面"**，
                # ESP32 收到即清除封面区；这是正常响应，不是错误，更不是 NOT_READY（§9.8-4 同理）。
                self.log("=== 对端无封面（空资源 FORMAT=0x00）→ 已清空封面区（§11）")
            else:
                self.log("=== 封面收齐：%d 字节 格式=0x%02X %dx%d（CRC32 由 C 侧校验通过）→ %s"
                         % (size, fmt, w, h, path))

    def _refresh_stats(self):
        self._set(frames_rx=self.dll.np_dll_stat_frames_rx(),
                  crc_errors=self.dll.np_dll_stat_crc_errors(),
                  resyncs=self.dll.np_dll_stat_resyncs(),
                  tx_dropped=self.dll.np_dll_stat_tx_dropped())


# ---------------------------------------------------------------- 命令行入口

def setup_utf8_console():
    """Windows 控制台 / 重定向文件的编码常是 GBK，打不出 ✓ ✗ ⚠ 这类字符（会抛 UnicodeEncodeError）。
    这里能改就改成 UTF-8；改不了也无所谓 —— MockServer.log() 里还有一层降级保护。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def parse_args():
    ap = argparse.ArgumentParser(description="New Protocol V1.1 ESP32 模拟器（协议层跑 np_ref.dll）")
    ap.add_argument("--transport", choices=("tcp", "com"), default="tcp",
                    help="链路：tcp（默认，上位机填「传输=TCP」）或 com（串口，上位机填「传输=COM」）")
    ap.add_argument("--ip", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9100)
    ap.add_argument("--com", default="COM23",
                    help="串口号（默认 COM23；要填与上位机「COM 端口」**配对**的那一端）")
    ap.add_argument("--baud", type=int, default=115200, help="串口波特率（与上位机 config.new.json 一致）")
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--caps", default="lyrics,cover,jpeg,png,rgb565",
                    help="逗号分隔：lyrics,cover,jpeg,png,rgb565（默认全给：模拟器这三种封面格式都收得下）")
    ap.add_argument("--max-edge", type=int, default=240, help="HELLO 里声明能接受的最大封面边长")
    ap.add_argument("--max-resource", type=int, default=262144, help="能接收的最大资源字节数（写进 HELLO）")
    ap.add_argument("--tick-ms", type=int, default=10, help="主循环周期")
    ap.add_argument("--exit-after-active", action="store_true", help="会话建立后自动退出")
    ap.add_argument("--control-every", type=float, default=0.0,
                    help="会话建立后每隔 N 秒发一次 CONTROL(PLAY_PAUSE)，用于验证 PC 侧回控")
    ap.add_argument("--run-seconds", type=float, default=0.0, help="运行 N 秒后自动退出（0 = 一直跑）")
    ap.add_argument("--art-dir", default="art_recv", help="收到的歌词/封面落盘目录")
    ap.add_argument("--cover-format", type=lambda s: int(s, 0), default=0x01,
                    help="请求封面时指定的 FORMAT：0x01=JPEG（默认，比 RGB565 小一个数量级）"
                         "0x02=PNG 0x10=RGB565")
    ap.add_argument("--latency-samples", type=int, default=0,
                    help="会话建立后自动主动测一轮延迟（本端发起、对端应答）；0 = 不自动测")
    ap.add_argument("--latency-interval-ms", type=int, default=100,
                    help="延迟测量两次 LATENCY_REQUEST 的间隔（默认 100ms）")
    ap.add_argument("--quiet", action="store_true", help="不逐帧打印")
    return ap.parse_args()


def main():
    args = parse_args()

    setup_utf8_console()

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

    server = MockServer(dll, ip=args.ip, port=args.port, caps=caps,
                        max_edge=args.max_edge, max_resource=args.max_resource,
                        tick_ms=args.tick_ms, art_dir=args.art_dir,
                        cover_format=args.cover_format, control_every=args.control_every,
                        exit_after_active=args.exit_after_active, run_seconds=args.run_seconds,
                        latency_samples=args.latency_samples,
                        latency_interval_ms=args.latency_interval_ms,
                        transport=args.transport, com_port=args.com, baud=args.baud,
                        quiet=args.quiet, cli=True, serve_forever=False)
    server.start()

    try:
        while server.is_alive():
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("[mock] ^C —— 退出")
    finally:
        server.stop()

    return server.exit_code if server.exit_code is not None else 0


if __name__ == "__main__":
    sys.exit(main())
