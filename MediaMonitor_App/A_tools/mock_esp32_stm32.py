# -*- coding: utf-8 -*-
"""
mock_esp32_stm32.py —— 全链路延迟测试的本地模拟器（ESP32 + STM32）

在没有硬件的情况下模拟：
    PC(C#) --UDP 0x1F Ping--> [本脚本: 假装 ESP32 转发 + STM32 处理] --UDP 0xAF Pong--> PC

只使用 Python 标准库（socket / struct / threading / time / random / argparse），无需 pip 安装。

设计要点（都是踩过的坑）：
  1) 定时用 precise_sleep() 自旋补偿：Windows 上 time.sleep 粒度约 15.6ms，
     直接 sleep 会把"声明 0.5~2ms 的 proc_us"实际睡成 8~15.6ms，
     导致上位机测到的延迟虚高好几倍（因为 proc_us 只按声明值扣除）。
  2) 回包放在**独立线程**里做：脚本在主收包循环里 sleep 会阻塞收包，
     上位机业务流量突发时接收缓冲被挤爆 → 丢包 + 积压 → "延迟巨大 / 收不到几个包"。
  3) 接收缓冲放大到 1MB，并对非 Ping 帧的打印按秒节流，避免刷屏淹掉 Ping 行。

用法示例（监听端口需与 C# 的 RemotePort 对齐）：
    python mock_esp32_stm32.py                                        # 监听 0.0.0.0:8080
    python mock_esp32_stm32.py --ip 127.0.0.1 --port 8080             # 同机调试推荐
    python mock_esp32_stm32.py --port 5555                            # 对齐你的远程端口
    python mock_esp32_stm32.py --proc-min 1000 --proc-max 1000 ^
                               --link-min 2 --link-max 2              # 固定耗时，便于校验公式
按 Ctrl+C 退出。
"""

import argparse
import random
import socket
import struct
import threading
import time

# ==== 协议常量（与 C# 的 PackageBuilder / PackageParser 严格对称） ====
PC_TO_MCU = 0xAA                 # 下行包头
MCU_TO_PC = 0xAB                 # 上行包头
CMD_PING = 0x1F                  # 延迟探测 Ping
CMD_PONG = 0xAF                  # 延迟回包 Pong
TARGET_SERIAL_MASTER = 0x01      # 目标设备 ID：0x01 = 串口 STM32 主设备
DEV_ID = 0x01                    # 本模拟器回复的设备 ID
PING_PAYLOAD_LEN = 5             # 1B Target_ID + 4B C#_T1_ms
PONG_PAYLOAD_LEN = 9             # 1B Dev_ID + 4B C#_T1_ms + 4B STM32_Proc_us

# 统计（供多线程回包使用）
_inflight_lock = threading.Lock()
_inflight = 0                    # 正在模拟处理中的回包数
_total = 0                       # 已回包总数


def xor_checksum(frame):
    """全帧异或校验：Head ^ Cmd ^ LenH ^ LenL ^ Payload..."""
    check = 0
    for b in frame:
        check ^= b
    return check


def precise_sleep(seconds):
    """高精度休眠（微秒级）。

    必须用它来代替裸 time.sleep()：Windows 上 time.sleep 的粒度受系统计时器限制
    （实测本机 Py3.9：sleep(0.5ms)≈8ms、sleep(1ms)≈15.2ms、sleep(5ms)≈15.6ms），
    用它模拟 proc_us/链路时延会把"0.5~2ms"睡成"8~15.6ms"，
    这段多出来的时间**不会被上位机的 proc_us 扣除**，直接把测得延迟虚高好几倍。
    这里对 <=30ms 的短延时改用自旋补偿，实测精度约 0.05ms。
    """
    if seconds <= 0:
        return
    end = time.perf_counter() + seconds
    if seconds > 0.030:                     # 只有明显大于系统粒度才值得先粗睡
        time.sleep(seconds - 0.015)
    while time.perf_counter() < end:
        pass


def build_pong(dev_id, t1_ms, proc_us):
    """构造 0xAF Pong：AB AF 00 09 [Dev_ID] [C#_T1_ms 原样] [STM32_Proc_us] [XOR]"""
    payload = struct.pack('<BII', dev_id, t1_ms, proc_us)
    body = bytes([MCU_TO_PC, CMD_PONG, (len(payload) >> 8) & 0xFF, len(payload) & 0xFF]) + payload
    return body + bytes([xor_checksum(body)])


def parse_ping(data):
    """校验下行 0xAA 0x1F 帧，成功返回 (target_id, t1_ms)，失败返回 None"""
    if len(data) < 4 + PING_PAYLOAD_LEN + 1:
        return None
    if data[0] != PC_TO_MCU or data[1] != CMD_PING:
        return None

    length = (data[2] << 8) | data[3]
    if length != PING_PAYLOAD_LEN:
        return None

    # 全帧异或：Head ^ Cmd ^ LenH ^ LenL ^ Payload，与校验位比对
    if xor_checksum(data[:4 + length]) != data[4 + length]:
        return None

    target_id = data[4]
    t1_ms = struct.unpack_from('<I', data, 5)[0]
    return target_id, t1_ms


def serve_ping(sock, addr, t1_ms, proc_us, link_ms, args, idx):
    """在**独立线程**里模拟 STM32 处理耗时 + 链路时延，然后回 0xAF。

    为什么一定要放线程里：脚本早先是在主收包循环里 time.sleep 的，
    睡着的这 24~31ms 内既收不了新包，上位机的业务流量（0x11 进度 / 0x15 歌词）
    还会把 UDP 接收缓冲挤爆 → 丢包 + 队列积压 → 表现就是"延迟非常大、收不到几个包、测不出"。
    真实硬件也不会因为"在处理一个包"就停止收包（UART 有 DMA/中断兜着）。
    """
    global _inflight, _total
    t0 = time.perf_counter()
    try:
        # 与真实时序一致：先"STM32 内部处理"，再"上下行链路"
        precise_sleep(proc_us / 1000000.0)
        precise_sleep(link_ms / 1000.0)
        sock.sendto(build_pong(DEV_ID, t1_ms, proc_us), addr)
    except OSError as e:
        print("[错误] 回包失败: %s" % e)
    finally:
        with _inflight_lock:
            _inflight -= 1
            _total += 1

    if not args.quiet:
        real_ms = (time.perf_counter() - t0) * 1000.0
        expect_ms = proc_us / 1000.0 + link_ms
        print("[%04d] <- Ping(T1=%dms)  回 Pong: Dev=0x%02X proc=%dus link=%.2fms "
              "实际=%.1fms(声明%.1f+线程调度) -> %s:%d"
              % (idx, t1_ms, DEV_ID, proc_us, link_ms, real_ms, expect_ms, addr[0], addr[1]))


def main():
    ap = argparse.ArgumentParser(
        description="ESP32 + STM32 全链路延迟模拟器 (0x1F Ping -> 0xAF Pong)")
    ap.add_argument("--ip", default="0.0.0.0",
                    help="监听地址，默认 0.0.0.0")
    ap.add_argument("--port", type=int, default=8080,
                    help="监听端口，默认 8080（需与 C# 的 RemotePort 一致）")
    ap.add_argument("--proc-min", type=int, default=500,
                    help="STM32 内部处理耗时下限(us)，默认 500")
    ap.add_argument("--proc-max", type=int, default=2000,
                    help="STM32 内部处理耗时上限(us)，默认 2000")
    ap.add_argument("--link-min", type=float, default=2.0,
                    help="链路往返时延下限(ms)，默认 2.0")
    ap.add_argument("--link-max", type=float, default=10.0,
                    help="链路往返时延上限(ms)，默认 10.0")
    ap.add_argument("--loss", type=float, default=0.0,
                    help="模拟丢包率 0.0~1.0，默认 0（用于验证上位机的超时保护）")
    ap.add_argument("--quiet", action="store_true",
                    help="不逐帧打印，只输出汇总")
    args = ap.parse_args()

    if args.proc_min > args.proc_max:
        args.proc_min, args.proc_max = args.proc_max, args.proc_min
    if args.link_min > args.link_max:
        args.link_min, args.link_max = args.link_max, args.link_min

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    # 放大接收缓冲（1MB）：上位机业务流量突发时也不会把 Ping 挤掉
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
    sock.bind((args.ip, args.port))
    rcvbuf_kb = sock.getsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF) // 1024

    print("=" * 66)
    print(" ESP32 + STM32 全链路延迟模拟器 (0x1F Ping -> 0xAF Pong)")
    print("=" * 66)
    print("  监听           : %s:%d" % (args.ip, args.port))
    print("  接收缓冲       : %d KB" % rcvbuf_kb)
    print("  STM32 proc_us  : %d ~ %d us" % (args.proc_min, args.proc_max))
    print("  链路往返时延   : %.2f ~ %.2f ms" % (args.link_min, args.link_max))
    print("  定时方式       : 自旋补偿（Windows time.sleep 粒度约 15.6ms，直接 sleep 会把延迟测虚高几倍）")
    print("  回包方式       : 独立线程（收包循环永不阻塞，避免积压/丢包）")
    print("  模拟丢包率     : %.1f %%" % (args.loss * 100))
    print("  目标设备过滤   : 仅响应 Target_ID = 0x%02X" % TARGET_SERIAL_MASTER)
    print("  期望的单向延迟 : (link/2) = %.2f ~ %.2f ms" %
          (args.link_min / 2.0, args.link_max / 2.0))
    print("  Ctrl+C 退出")
    print("=" * 66)

    global _inflight
    seq = 0
    ignored = 0
    last_ignore_log = 0.0

    try:
        while True:
            data, addr = sock.recvfrom(2048)

            parsed = parse_ping(data)
            if parsed is None:
                # 非 Ping 帧（上位机的 0x10/0x11/0x15 等常规帧）：按秒节流提示，
                # 免得把真正的 "<- Ping" 行淹掉
                ignored += 1
                now = time.perf_counter()
                if not args.quiet and now - last_ignore_log > 1.0:
                    last_ignore_log = now
                    print("[忽略] 非 Ping 帧累计 %d 个（上位机的常规同步帧），最新 %dB: %s"
                          % (ignored, len(data), data[:16].hex(' ').upper()))
                continue

            target_id, t1_ms = parsed

            # 主从过滤：其他设备收到静默丢弃（与 STM32 裸机逻辑一致）
            if target_id != TARGET_SERIAL_MASTER:
                if not args.quiet:
                    print("[丢弃] Target_ID=0x%02X 非主设备，静默忽略" % target_id)
                continue

            seq += 1

            if args.loss > 0 and random.random() < args.loss:
                if not args.quiet:
                    print("[%04d] 模拟丢包: T1=%dms 不回包" % (seq, t1_ms))
                continue

            proc_us = random.randint(args.proc_min, args.proc_max)
            link_ms = random.uniform(args.link_min, args.link_max)

            # 丢到独立线程里做"模拟 proc + 链路时延 + 回包"，主循环立刻回去继续收包
            with _inflight_lock:
                _inflight += 1
                busy = _inflight
            if busy > 200 and not args.quiet:
                print("[警告] 同时在途回包 %d 个：对端已处理不过来（上位机业务流量太大？）" % busy)

            threading.Thread(target=serve_ping,
                             args=(sock, addr, t1_ms, proc_us, link_ms, args, seq),
                             daemon=True).start()
    except KeyboardInterrupt:
        print("\n已退出。共响应 %d 个 Ping 帧。" % _total)
    finally:
        sock.close()


if __name__ == "__main__":
    main()

