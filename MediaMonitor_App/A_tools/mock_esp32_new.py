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
CAP_BITS = {"lyrics": 1, "cover": 2, "jpeg": 4, "png": 8, "rgb565": 16}

STATE_NAMES = {0: "IDLE", 1: "HANDSHAKE", 2: "NEGOTIATING", 3: "WAIT_SESSION_START", 4: "ACTIVE"}

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
    if f.type in (5, 6):
        return RES_NAMES.get(f.code, "RES_0x%02X" % f.code)
    if f.type == 4:
        return CTRL_NAMES.get(f.code, "CTRL_0x%02X" % f.code)
    return "CODE_0x%02X" % f.code


def print_frame(f):
    payload = bytes(f.payload[:f.payload_len])
    extra = payload.hex(" ").upper() if payload else "-"
    print("[mock] <- %-9s %-14s len=%-4d seq=%-4d sid=0x%08X rid=%-4d  payload=%s"
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
    ap.add_argument("--max-resource", type=int, default=65536, help="能接收的最大资源字节数")
    ap.add_argument("--tick-ms", type=int, default=10, help="主循环周期")
    ap.add_argument("--exit-after-active", action="store_true", help="会话建立后自动退出")
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

            # 2) 打印收到的帧
            while True:
                f = NpFrameOut()
                if dll.np_dll_poll(ctypes.byref(f)) != 1:
                    break
                if not args.quiet:
                    print_frame(f)

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

            if dll.np_dll_session_handshake_failed():
                print("[mock] 握手失败（HELLO 重试用尽）")
                break

            time.sleep(args.tick_ms / 1000.0)
    finally:
        print("[mock] 统计：帧=%d crc错=%d 重同步=%d tx丢弃=%d 会话=%s"
              % (dll.np_dll_stat_frames_rx(), dll.np_dll_stat_crc_errors(),
                 dll.np_dll_stat_resyncs(), dll.np_dll_stat_tx_dropped(),
                 STATE_NAMES.get(dll.np_dll_session_state(), "?")))
        conn.close()
        srv.close()

    return 0 if active else 3


if __name__ == "__main__":
    sys.exit(main())
