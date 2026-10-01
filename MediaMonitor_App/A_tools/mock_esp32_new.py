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
    python mock_esp32_new.py --dll <别的 np_ref.dll 路径>

PC 侧把「协议模式 = New」「传输 = TCP」「对端 IP/端口」填成本机与上面的端口即可。
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


# ---------------------------------------------------------------- 服务端（CLI 与 GUI 共用）

class MockServer:
    """
    ESP32 模拟器的服务端核心：socket + 真 C 协议栈（np_ref.dll）的完整循环。

    设计要点：
      * **所有 DLL 调用都发生在本对象的后台线程里** —— C 侧是全局单例状态，多线程乱调必崩；
      * 外部（CLI / GUI）只通过 `snapshot()` 读状态、`post()` 投命令，全线程安全；
      * `serve_forever=True` 时断线后继续监听（GUI 场景：关掉上位机再开还能重连）；
      * 每次新连接都会 `np_dll_init / session_init / res_init / timeline_init`，避免上一次的残留状态。
    """

    def __init__(self, dll, ip="127.0.0.1", port=9100, caps=0x1F, max_edge=240,
                 max_resource=262144, tick_ms=10, art_dir="art_recv", cover_format=0x10,
                 control_every=0.0, exit_after_active=False, run_seconds=0.0,
                 quiet=False, cli=False, serve_forever=False, on_event=None):
        self.dll = dll
        self.ip = ip
        self.port = port
        self.caps = caps
        self.max_edge = max_edge
        self.max_resource = max_resource
        self.tick_ms = tick_ms
        self.art_dir = art_dir
        self.cover_format = cover_format
        self.control_every = control_every
        self.exit_after_active = exit_after_active
        self.run_seconds = run_seconds
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

        self._st = {
            "running": False,
            "phase": "未启动",
            "peer": "",
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
            print("[mock] %s" % text)
        if self.on_event:
            try:
                self.on_event(line)
            except Exception:
                pass

    def post(self, cmd, arg=None):
        """投一条命令给后台线程执行（GUI 按钮用）"""
        self._cmds.put((cmd, arg))

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

    # ---- 内部：命令队列 ----

    def _drain_cmds(self, active):
        while True:
            try:
                cmd, arg = self._cmds.get_nowait()
            except queue.Empty:
                return

            if cmd == "control":
                queued = self.dll.np_dll_send_control(arg)
                self._bump("control_sent")
                self.log("-> CONTROL %s（入队=%d）%s"
                         % (CTRL_NAMES.get(arg, arg), queued,
                            "" if active else "   ⚠ 会话未建立，对端可能不理它"))

            elif cmd == "req_lyrics":
                queued = self.dll.np_dll_send_request_lyrics()
                self.log("-> LYRICS REQUEST（入队=%d）%s"
                         % (queued, "" if active else "   ⚠ 会话未建立"))

            elif cmd == "req_cover":
                queued = self.dll.np_dll_send_request_cover(self.max_edge, self.max_edge,
                                                            self.cover_format, 0)
                self.log("-> ALBUMCOVER REQUEST %dx%d fmt=0x%02X（入队=%d）"
                         % (self.max_edge, self.max_edge, self.cover_format, queued))


    # ---- 内部：监听 + 每个连接的服务循环 ----

    def _run(self):
        self._set(running=True, phase="监听中")
        self.log("监听 %s:%d  CAPS=0x%08X maxEdge=%d maxRes=%d"
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

                try:
                    self._serve(conn, addr)
                finally:
                    try:
                        conn.close()
                    except Exception:
                        pass

                if not self.serve_forever:
                    break
        finally:
            try:
                srv.close()
            except Exception:
                pass
            self._set(running=False, phase="已停止", peer="")
            self.log("服务端已停止")

    def _serve(self, conn, addr):
        conn.setblocking(False)

        self._set(peer="%s:%d" % (addr[0], addr[1]), phase="握手…")
        self.log("上位机已连接：%s:%d" % (addr[0], addr[1]))

        t0 = time.monotonic()
        self.dll.np_dll_set_time_ms(0)
        self.dll.np_dll_init()
        self.dll.np_dll_res_init()
        self.dll.np_dll_timeline_init()
        self.dll.np_dll_session_init(self.caps, self.max_edge, self.max_resource, 0)   # 0 = ESP32 角色
        self.dll.np_dll_session_link_up()      # 链路就绪 → 真 C 代码立刻组出 HELLO
        self.log("已发起 HELLO（ESP32 角色）")

        active = False
        self._requested = False
        self._cover_requested = False
        self._delivered = 0
        last_state = None
        last_control = time.monotonic()
        txbuf = ctypes.create_string_buffer(2048)

        try:
            while not self._stop.is_set():
                self.dll.np_dll_set_time_ms(int((time.monotonic() - t0) * 1000))

                # 0) 执行 GUI/CLI 投进来的命令
                self._drain_cmds(active)

                # 1) socket → C 解析器
                try:
                    data = conn.recv(4096)
                    if not data:
                        self.log("上位机已断开")
                        break
                    self.dll.np_dll_feed(data, len(data))
                except BlockingIOError:
                    pass
                except ConnectionResetError:
                    self.log("连接被重置")
                    break
                except OSError as ex:
                    self.log("socket 异常：%s" % ex)
                    break

                # 2) 取出解析好的帧 → 资源 / 时间轴 / 延迟 / 触发拉资源
                self._dispatch_frames(active)

                # 3) 让 C 会话机跑一次（重传 / 超时），取出要发的帧发走
                self.dll.np_dll_session_tick()
                while True:
                    n = self.dll.np_dll_tx_poll(txbuf, len(txbuf))
                    if n <= 0:
                        break
                    if not self.quiet:
                        print("[mock] -> %d 字节: %s" % (n, txbuf.raw[:n].hex(" ").upper()))
                    conn.sendall(txbuf.raw[:n])

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
            self.log("时间轴投递给 sink 的帧数=%d  延迟请求应答数=%d"
                     % (self.dll.np_dll_timeline_delivered(), self._st["latency_responded"]))
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

            if f.type in (5, 6):
                ev = self.dll.np_dll_res_on_frame(ctypes.byref(f))
                if ev == 1:
                    self.log("资源 BEGIN（type=%d/%s）"
                             % (f.type, "lyrics" if f.type == 5 else "cover"))
                elif ev == 3:
                    self._on_resource_done(f.type)
                    # 单资源互斥由 ESP32 侧保证（规范 §9.7）：等歌词收完再拉封面
                    if (f.type == 5) and (not self._cover_requested) and active:
                        if self.dll.np_dll_send_request_cover(self.max_edge, self.max_edge,
                                                              self.cover_format, 0):
                            self._cover_requested = True
                            self.log("歌词收齐 → 再发起 ALBUMCOVER REQUEST（%dx%d fmt=0x%02X）"
                                     % (self.max_edge, self.max_edge, self.cover_format))
                elif ev == 4:
                    self.log("资源接收错误：%s" % RES_ERR_NAMES.get(self.dll.np_dll_res_last_err(), "?"))
                elif ev == 5:
                    self.log("资源被 ABORT（对端切歌 / 资源作废）")

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
                # 延迟测量请求：原样回传 T1，并带上"本端处理耗时"（微秒）
                payload = bytes(f.payload[:f.payload_len])
                t1 = _be32(payload, 0) if len(payload) >= 4 else 0
                self.dll.np_dll_latency_respond(f.request_id, t1, 150)   # 150us 模拟 MCU 处理耗时
                self._bump("latency_responded")

            elif f.type == 2:
                self._on_media(f)
                if (not self._requested) and active:
                    if self.dll.np_dll_send_request_lyrics():
                        self._requested = True
                        self.log("收到 MEDIA → 发起 LYRICS REQUEST（拿到后再拉封面）")

    # ---- 内部：状态快照更新 ----

    def _on_media(self, f):
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
        self.log("MEDIA：%s - %s（专辑 %s）" % (title or "(空)", artist or "(空)", album or "(空)"))

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
            self.log("=== 封面收齐：%d 字节 格式=0x%02X %dx%d（CRC32 由 C 侧校验通过）→ %s"
                     % (size, fmt, w, h, path))

    def _refresh_stats(self):
        self._set(frames_rx=self.dll.np_dll_stat_frames_rx(),
                  crc_errors=self.dll.np_dll_stat_crc_errors(),
                  resyncs=self.dll.np_dll_stat_resyncs(),
                  tx_dropped=self.dll.np_dll_stat_tx_dropped())


# ---------------------------------------------------------------- 命令行入口

def parse_args():
    ap = argparse.ArgumentParser(description="New Protocol V1.1 ESP32 模拟器（协议层跑 np_ref.dll）")
    ap.add_argument("--ip", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9100)
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

    server = MockServer(dll, ip=args.ip, port=args.port, caps=caps,
                        max_edge=args.max_edge, max_resource=args.max_resource,
                        tick_ms=args.tick_ms, art_dir=args.art_dir,
                        cover_format=args.cover_format, control_every=args.control_every,
                        exit_after_active=args.exit_after_active, run_seconds=args.run_seconds,
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
