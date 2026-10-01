# -*- coding: utf-8 -*-
"""
mock_esp32_gui.py —— New Protocol V1.1 的 **ESP32 模拟器（图形版）**

和 mock_esp32_new.py 是同一个内核（协议字节层跑 ref_c 编出来的 np_ref.dll），
区别只在于：收到的东西不再只刷控制台，而是**解码后画在窗口里**：

    * 会话状态 / SESSION_ID / 帧数 / CRC 错误 / 重同步
    * MEDIA：标题 / 艺术家 / 专辑
    * TIMELINE：播放状态 + 进度条 + cur/total/host_tick/local_tick（+ 收帧计数）
    * LYRICS：歌词行（`[mm:ss.mmm] 正文`，翻译行带缩进，逐字行标出词数）
    * ALBUMCOVER：封面预览（RGB565 直接解码；JPEG/PNG 需要 PIL，没有就只显示信息）
    * 手动按钮：PLAY_PAUSE / NEXT / PREV / 请求歌词 / 请求封面

用法：
    python mock_esp32_gui.py                            # 默认 127.0.0.1:9100
    python mock_esp32_gui.py --port 9200
    python mock_esp32_gui.py --headless --seconds 20     # 无窗口自检（脚本化验证用）

依赖：Python 标准库（tkinter 随 Python 安装；PIL 可选）。
"""

import argparse
import os
import sys
import tempfile
import time
import tkinter as tk
from tkinter import ttk, scrolledtext

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import mock_esp32_new as core          # 复用：DLL 加载 + MockServer + 资源解码

try:
    from PIL import Image, ImageTk      # 可选：能直接显示 JPEG/PNG 封面
    HAVE_PIL = True
except Exception:
    HAVE_PIL = False

FONT = ("Microsoft YaHei UI", 9)
FONT_BOLD = ("Microsoft YaHei UI", 10, "bold")
FONT_MONO = ("Consolas", 9)


def fmt_ms(ms):
    ms = int(ms or 0)
    return "%02d:%02d.%03d" % (ms // 60000, (ms // 1000) % 60, ms % 1000)


class MockGui:
    def __init__(self, root, dll, args):
        self.root = root
        self.dll = dll
        self.args = args
        self.server = None
        self._ppm_path = None
        self._photo = None
        self._pil_photo = None
        self._cover_key = None
        self._log_lines = 0

        root.title("对端模拟器 · ESP32 / New Protocol V1.1")
        root.geometry("1180x820")
        root.minsize(980, 700)

        self._build_widgets()
        root.after(200, self._tick)
        root.after(300, self.on_start)      # 开窗即监听：这是调试工具，少点一次是一次


    # ------------------------------------------------------------------ 界面

    def _build_widgets(self):
        pad = {"padx": 8, "pady": 6}

        # --- 顶栏：监听地址 + 启停 ---
        top = ttk.Frame(self.root)
        top.grid(row=0, column=0, sticky="ew", **pad)

        ttk.Label(top, text="监听地址:", font=FONT).pack(side="left")
        self.var_ip = tk.StringVar(value=self.args.ip)
        ttk.Entry(top, textvariable=self.var_ip, width=12, font=FONT).pack(side="left", padx=4)
        ttk.Label(top, text="端口:", font=FONT).pack(side="left")
        self.var_port = tk.StringVar(value=str(self.args.port))
        ttk.Entry(top, textvariable=self.var_port, width=6, font=FONT).pack(side="left", padx=4)

        self.btn_start = ttk.Button(top, text="启动服务端", command=self.on_start)
        self.btn_start.pack(side="left", padx=6)
        self.btn_stop = ttk.Button(top, text="停止", command=self.on_stop, state="disabled")
        self.btn_stop.pack(side="left")

        self.lbl_phase = ttk.Label(top, text="未启动", font=FONT_BOLD, foreground="#555555")
        self.lbl_phase.pack(side="left", padx=16)

        ttk.Label(top, text="上位机填 → New 模式 / TCP / 对端 = 本机 IP:端口",
                  font=FONT, foreground="#888888").pack(side="left")

        # --- 会话与统计 ---
        info = ttk.LabelFrame(self.root, text=" 会话与统计 ")
        info.grid(row=1, column=0, sticky="ew", padx=8)

        self.lbl_session = ttk.Label(info, text="会话: -", font=FONT_BOLD)
        self.lbl_session.grid(row=0, column=0, sticky="w", padx=8, pady=4)
        self.lbl_sid = ttk.Label(info, text="SESSION_ID: -", font=FONT_MONO)
        self.lbl_sid.grid(row=0, column=1, sticky="w", padx=8)
        self.lbl_stats = ttk.Label(info, text="帧=0  crc错=0  重同步=0", font=FONT_MONO)
        self.lbl_stats.grid(row=0, column=2, sticky="w", padx=8)
        self.lbl_extra = ttk.Label(info, text="延迟应答=0  回控发出=0", font=FONT_MONO)
        self.lbl_extra.grid(row=0, column=3, sticky="w", padx=8)

        # --- 媒体 / 时间轴 / 封面 ---
        mid = ttk.Frame(self.root)
        mid.grid(row=2, column=0, sticky="nsew", padx=8, pady=4)
        mid.columnconfigure(0, weight=3)
        mid.columnconfigure(1, weight=2)
        self.root.rowconfigure(2, weight=3)

        left = ttk.Frame(mid)
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 6))

        media = ttk.LabelFrame(left, text=" MEDIA（解码自 METADATA 帧） ")
        media.pack(fill="x")
        self.lbl_title = ttk.Label(media, text="（还没收到）", font=("Microsoft YaHei UI", 16, "bold"),
                                   wraplength=560, justify="left")
        self.lbl_title.pack(anchor="w", padx=10, pady=(10, 2))
        self.lbl_artist = ttk.Label(media, text="-", font=("Microsoft YaHei UI", 11), foreground="#444444",
                                    wraplength=560, justify="left")
        self.lbl_artist.pack(anchor="w", padx=10)
        self.lbl_album = ttk.Label(media, text="-", font=FONT, foreground="#777777",
                                   wraplength=560, justify="left")
        self.lbl_album.pack(anchor="w", padx=10, pady=(0, 8))

        tl = ttk.LabelFrame(left, text=" TIMELINE（解码自 TIMELINE 帧） ")
        tl.pack(fill="x", pady=(6, 0))
        self.pb = ttk.Progressbar(tl, orient="horizontal", length=520, mode="determinate")
        self.pb.pack(fill="x", padx=8, pady=(8, 4))
        self.lbl_time = ttk.Label(tl, text="--:--.--- / --:--.---    播放中: -", font=FONT_MONO)
        self.lbl_time.pack(anchor="w", padx=8)
        self.lbl_tick = ttk.Label(tl, text="HOST_TICK=-  LOCAL_TICK=-   已投递 0 帧",
                                  font=FONT_MONO, foreground="#666666")
        self.lbl_tick.pack(anchor="w", padx=8, pady=(0, 8))

        cover = ttk.LabelFrame(mid, text=" ALBUMCOVER（已过 C 侧 CRC32 校验） ")
        cover.grid(row=0, column=1, sticky="nsew")
        self.canvas = tk.Canvas(cover, width=490, height=470, background="#F2F2F2",
                                highlightthickness=1, highlightbackground="#CCCCCC")
        self.canvas.pack(padx=8, pady=8)
        self.canvas.create_text(245, 235, text="（等待封面…）", fill="#999999", font=FONT)
        self.lbl_cover = ttk.Label(cover, text="-", font=FONT_MONO, foreground="#666666",
                                   wraplength=470, justify="left")
        self.lbl_cover.pack(anchor="w", padx=8, pady=(0, 8))

        self.root.columnconfigure(0, weight=1)

        # --- 歌词 ---
        lyr = ttk.LabelFrame(self.root, text=" LYRICS（歌词资源 → Legacy 帧 → 行） ")
        lyr.grid(row=3, column=0, sticky="nsew", padx=8)
        self.root.rowconfigure(3, weight=2)
        self.txt_lyrics = scrolledtext.ScrolledText(lyr, height=9, font=FONT_MONO, wrap="none")
        self.txt_lyrics.pack(fill="both", expand=True, padx=8, pady=8)

        # --- 手动按钮 ---
        ctl = ttk.Frame(self.root)
        ctl.grid(row=4, column=0, sticky="ew", padx=8, pady=4)
        ttk.Label(ctl, text="手动（都走真 C 代码入队）:", font=FONT).pack(side="left")
        for text, code in (("PLAY_PAUSE", 1), ("NEXT", 2), ("PREV", 3)):
            ttk.Button(ctl, text=text, command=lambda c=code: self.on_control(c)).pack(side="left", padx=4)
        ttk.Button(ctl, text="请求歌词", command=lambda: self.on_cmd("req_lyrics")).pack(side="left", padx=12)
        ttk.Button(ctl, text="请求封面", command=lambda: self.on_cmd("req_cover")).pack(side="left", padx=4)

        # --- 事件日志 ---
        logf = ttk.LabelFrame(self.root, text=" 事件日志 ")
        logf.grid(row=5, column=0, sticky="nsew", padx=8, pady=(0, 8))
        self.root.rowconfigure(5, weight=2)
        self.txt_log = scrolledtext.ScrolledText(logf, height=8, font=FONT_MONO, background="#1E1E1E",
                                                 foreground="#CCCCCC", insertbackground="#CCCCCC")
        self.txt_log.pack(fill="both", expand=True, padx=8, pady=8)

    # ------------------------------------------------------------------ 动作

    def on_start(self):
        try:
            port = int(self.var_port.get())
        except ValueError:
            self._append_log("端口不合法")
            return

        self.server = core.MockServer(
            self.dll,
            ip=self.var_ip.get().strip() or "127.0.0.1",
            port=port,
            caps=self.args.caps_bits,
            max_edge=self.args.max_edge,
            max_resource=self.args.max_resource,
            tick_ms=self.args.tick_ms,
            art_dir=self.args.art_dir,
            cover_format=self.args.cover_format,
            quiet=True,                      # GUI 里不逐帧刷控制台
            cli=False,
            serve_forever=True)              # 断线后继续监听，方便反复联调
        self.server.start()

        self.btn_start.config(state="disabled")
        self.btn_stop.config(state="normal")
        self._append_log("已启动服务端（内核 np_ref.dll —— 与真固件同一份 C 代码）")

    def on_stop(self):
        if self.server is not None:
            self.server.stop()
        self.btn_start.config(state="normal")
        self.btn_stop.config(state="disabled")
        self._append_log("已停止服务端")

    def on_control(self, code):
        if self.server is None:
            self._append_log("服务端未启动")
            return
        self.server.post("control", code)

    def on_cmd(self, cmd):
        if self.server is None:
            self._append_log("服务端未启动")
            return
        self.server.post(cmd)

    # ------------------------------------------------------------------ 刷新

    def _tick(self):
        if self.server is not None:
            self._render(self.server.snapshot())
        self.root.after(200, self._tick)

    def _render(self, st):
        # 顶栏
        self.lbl_phase.config(text=st["phase"],
                              foreground="#0A7D28" if st["session_state"] == 4 else "#B36B00")

        # 会话与统计
        self.lbl_session.config(text="会话: %s" % st["session_name"],
                                foreground="#0A7D28" if st["session_state"] == 4 else "#444444")
        self.lbl_sid.config(text="SESSION_ID: 0x%08X" % st["session_id"])
        self.lbl_stats.config(text="帧=%d  crc错=%d  重同步=%d  tx丢弃=%d"
                                   % (st["frames_rx"], st["crc_errors"], st["resyncs"], st["tx_dropped"]))
        self.lbl_extra.config(text="延迟应答=%d  回控发出=%d  对端=%s"
                                   % (st["latency_responded"], st["control_sent"], st["peer"] or "-"))

        # 媒体
        media = st["media"]
        if media:
            self.lbl_title.config(text=media["title"] or "(空标题)")
            self.lbl_artist.config(text=media["artist"] or "-")
            self.lbl_album.config(text=media["album"] or "-")

        # 时间轴
        tl = st["timeline"]
        if tl:
            total = max(tl["total_ms"], 1)
            self.pb.config(maximum=total, value=min(tl["cur_ms"], total))
            self.lbl_time.config(text="%s / %s    播放中: %s"
                                      % (fmt_ms(tl["cur_ms"]), fmt_ms(tl["total_ms"]),
                                         "是" if tl["playing"] else "否"))
            self.lbl_tick.config(text="HOST_TICK=%d  LOCAL_TICK=%d   已投递 %d 帧"
                                      % (tl["host_tick"], tl["local_tick"], tl["delivered"]))

        # 歌词（资源变了才重画）
        lyrics = st["lyrics"]
        if lyrics and (self.txt_lyrics.get("1.0", "2.0").strip() != "// " + lyrics["path"]):
            self.txt_lyrics.delete("1.0", "end")
            self.txt_lyrics.insert("end", "// %s（%d 字节）\n\n" % (lyrics["path"], lyrics["size"]))
            for ln in lyrics["lines"]:
                mark = {"line": "  ", "trans": "    ↳", "word": "  ~"}[ln["kind"]]
                tail = "（逐字 %d 词）" % len(ln["words"]) if ln["kind"] == "word" else ""
                self.txt_lyrics.insert("end", "%s[%s] %s%s\n"
                                       % (mark, fmt_ms(ln["start_ms"]), ln["text"], tail))

        # 封面
        cover = st["cover"]
        if cover:
            self.lbl_cover.config(text="格式=0x%02X  %dx%d  %d 字节  → %s"
                                       % (cover["format"], cover["w"], cover["h"],
                                          cover["size"], cover["path"]))
            key = (cover["path"], cover["format"], cover["w"], cover["h"], cover["size"])
            if key != self._cover_key:
                self._cover_key = key
                self._show_cover(cover)

        # 事件日志（增量追加）
        events = st["events"]
        if len(events) > self._log_lines:
            for line in events[self._log_lines:]:
                self._append_log(line)
            self._log_lines = len(events)

    def _show_cover(self, cover):
        fmt, w, h, data = cover["format"], cover["w"], cover["h"], cover["data"]

        if fmt == 0x10:                                  # RGB565 → PPM（Tk 原生支持）
            ppm = core.decode_rgb565_to_ppm(data, w, h)
            if ppm is None:
                self._append_log("封面 RGB565 数据长度与尺寸不符，无法渲染")
                return
            try:
                if self._ppm_path is None:
                    fd, self._ppm_path = tempfile.mkstemp(suffix=".ppm")
                    os.close(fd)
                with open(self._ppm_path, "wb") as fh:
                    fh.write(ppm)

                photo = tk.PhotoImage(file=self._ppm_path)
                zoom = 2 if max(w, h) <= 260 else 1
                if zoom > 1:
                    photo = photo.zoom(zoom)
                self._photo = photo
                self._set_canvas_image(photo)
                self._append_log("封面已渲染：RGB565 %dx%d（放大 %dx）" % (w, h, zoom))
            except Exception as ex:
                self._append_log("封面渲染失败：%s" % ex)
            return

        if fmt in (0x01, 0x02) and HAVE_PIL:             # JPEG / PNG（需要 PIL）
            try:
                import io
                img = Image.open(io.BytesIO(data))
                self._pil_photo = ImageTk.PhotoImage(img)
                self._set_canvas_image(self._pil_photo)
                self._append_log("封面已渲染：%s %dx%d" % ("JPEG" if fmt == 0x01 else "PNG", w, h))
            except Exception as ex:
                self._append_log("封面渲染失败：%s" % ex)
            return

        if fmt in (0x01, 0x02):
            self.canvas.delete("all")
            self.canvas.create_text(
                245, 235,
                text="收到 %s %dx%d，%d 字节\n（未安装 PIL，无法解码预览；\n文件已落盘：%s）"
                     % ("JPEG" if fmt == 0x01 else "PNG", w, h, len(data), cover["path"]),
                fill="#666666", font=FONT, justify="center")
            return

        self.canvas.delete("all")
        self.canvas.create_text(245, 235, text="FORMAT=0x%02X（%d 字节）" % (fmt, len(data)),
                                fill="#666666", font=FONT)

    def _set_canvas_image(self, photo):
        self.canvas.delete("all")
        self.canvas.create_image(245, 235, image=photo)

    def _append_log(self, text):
        self.txt_log.insert("end", text + "\n")
        lines = int(self.txt_log.index("end-1c").split(".")[0])
        if lines > 600:
            self.txt_log.delete("1.0", "150.0")
        self.txt_log.see("end")


# ---------------------------------------------------------------- 入口

def parse_args():
    ap = argparse.ArgumentParser(description="New Protocol V1.1 ESP32 模拟器（图形版，内核 = 真 C 代码）")
    ap.add_argument("--ip", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9100)
    ap.add_argument("--dll", default=core.DEFAULT_DLL)
    ap.add_argument("--caps", default="lyrics,cover,jpeg,rgb565")
    ap.add_argument("--max-edge", type=int, default=240)
    ap.add_argument("--max-resource", type=int, default=262144)
    ap.add_argument("--tick-ms", type=int, default=10)
    ap.add_argument("--art-dir", default="art_recv")
    ap.add_argument("--cover-format", type=lambda s: int(s, 0), default=0x10)
    ap.add_argument("--headless", action="store_true", help="不开窗口，只跑服务端（脚本化验证用）")
    ap.add_argument("--seconds", type=float, default=0.0, help="headless 模式跑 N 秒后打印摘要并退出")
    return ap.parse_args()

def headless_run(dll, args, caps):
    """无窗口自检：跑服务端，结束时打印解码摘要（给自动化验证用）"""
    server = core.MockServer(dll, ip=args.ip, port=args.port, caps=caps,
                             max_edge=args.max_edge, max_resource=args.max_resource,
                             tick_ms=args.tick_ms, art_dir=args.art_dir,
                             cover_format=args.cover_format,
                             quiet=True, cli=True, serve_forever=True)
    server.start()

    t0 = time.monotonic()
    last_peer = None
    while (time.monotonic() - t0) < max(args.seconds, 1.0):
        time.sleep(0.3)
        st = server.snapshot()
        if st["peer"] != last_peer:
            last_peer = st["peer"]
            print("[gui] 对端 = %s" % (st["peer"] or "-"))

    st = server.snapshot()
    print("[gui] 会话=%s  sid=0x%08X  帧=%d  crc错=%d  重同步=%d"
          % (st["session_name"], st["session_id"], st["frames_rx"], st["crc_errors"], st["resyncs"]))
    if st["media"]:
        print("[gui] MEDIA：%s - %s（%s）"
              % (st["media"]["title"], st["media"]["artist"], st["media"]["album"]))
    if st["timeline"]:
        tl = st["timeline"]
        print("[gui] TIMELINE：%s / %s  playing=%s  已投递 %d 帧"
              % (fmt_ms(tl["cur_ms"]), fmt_ms(tl["total_ms"]),
                 "是" if tl["playing"] else "否", tl["delivered"]))
    if st["lyrics"]:
        print("[gui] LYRICS：%d 字节 / %d 行（前 8 行）"
              % (st["lyrics"]["size"], len(st["lyrics"]["lines"])))
        for ln in st["lyrics"]["lines"][:8]:
            print("        [%s] %s%s" % (fmt_ms(ln["start_ms"]), "  " if ln["kind"] != "trans" else "↳ ",
                                        ln["text"]))
    if st["cover"]:
        c = st["cover"]
        print("[gui] COVER：格式=0x%02X  %dx%d  %d 字节 → %s"
              % (c["format"], c["w"], c["h"], c["size"], c["path"]))

    server.stop()
    return 0


def main():
    args = parse_args()

    caps = 0
    for name in args.caps.split(","):
        name = name.strip().lower()
        if name:
            caps |= core.CAP_BITS.get(name, 0)
    args.caps_bits = caps

    dll = core.load_dll(args.dll)
    if dll.np_dll_selftest() != 0:
        print("[gui] DLL 自检失败")
        return 2
    print("[gui] np_ref.dll 自检通过（协议字节层与真固件同一份 C 代码）")

    if args.headless:
        return headless_run(dll, args, caps)

    try:
        root = tk.Tk()
    except tk.TclError as ex:
        print("[gui] 无法创建窗口（%s）；无显示环境请用 --headless" % ex)
        return 1

    MockGui(root, dll, args)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
