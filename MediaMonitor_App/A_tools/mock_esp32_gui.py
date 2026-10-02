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
    * 手动按钮：PLAY_PAUSE / NEXT / PREV / 请求歌词 / 请求封面 / 测延迟（本端发起一轮延迟测量）

窗口布局（左列自上而下）：MEDIA → LYRICS → TIMELINE，右列 ALBUMCOVER，底部事件日志。

用法：
    python mock_esp32_gui.py                            # 默认 TCP 127.0.0.1:9100
    python mock_esp32_gui.py --port 9200
    python mock_esp32_gui.py --transport com --com COM23 --baud 115200   # 串口（上位机传输=COM）
    python mock_esp32_gui.py --headless --seconds 20     # 无窗口自检（脚本化验证用）

窗口里也能直接切「传输」：TCP（填 IP/端口）或 COM（填串口号/波特率，用 com0com 之类的虚拟串口对即可）。

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

# 封面格式（与规范 §11 的 FORMAT 字段一致）
FMT_NAMES = {0x00: "NONE", 0x01: "JPEG", 0x02: "PNG", 0x10: "RGB565"}
FMT_CODES = {"JPEG": 0x01, "PNG": 0x02, "RGB565": 0x10}


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
        self._lyrics_key = None
        self._log_lines = 0

        root.title("对端模拟器 · ESP32 / New Protocol V1.1")
        root.geometry("1200x900")
        root.minsize(980, 760)

        self._build_widgets()
        root.after(200, self._tick)
        root.after(300, self.on_start)      # 开窗即监听：这是调试工具，少点一次是一次


    # ------------------------------------------------------------------ 界面

    def _build_widgets(self):
        pad = {"padx": 8, "pady": 6}

        # --- 顶栏：监听地址 + 启停 ---
        top = ttk.Frame(self.root)
        top.grid(row=0, column=0, sticky="ew", **pad)

        # 传输方式：TCP（上位机填「传输=TCP」+ IP/端口）或 COM（上位机填「传输=COM」+ 配对的那一端）
        ttk.Label(top, text="传输:", font=FONT).pack(side="left")
        self.var_transport = tk.StringVar(value="TCP")
        cbo_tr = ttk.Combobox(top, textvariable=self.var_transport, width=5, state="readonly",
                              values=["TCP", "COM"], font=FONT)
        cbo_tr.pack(side="left", padx=4)
        cbo_tr.bind("<<ComboboxSelected>>", self.on_transport_changed)

        ttk.Label(top, text="地址:", font=FONT).pack(side="left")
        self.var_ip = tk.StringVar(value=self.args.ip)
        self.ent_ip = ttk.Entry(top, textvariable=self.var_ip, width=11, font=FONT)
        self.ent_ip.pack(side="left", padx=4)
        ttk.Label(top, text="端口:", font=FONT).pack(side="left")
        self.var_port = tk.StringVar(value=str(self.args.port))
        self.ent_port = ttk.Entry(top, textvariable=self.var_port, width=6, font=FONT)
        self.ent_port.pack(side="left", padx=4)

        ttk.Label(top, text="串口:", font=FONT).pack(side="left", padx=(8, 0))
        self.var_com = tk.StringVar(value=self.args.com)
        self.ent_com = ttk.Entry(top, textvariable=self.var_com, width=7, font=FONT)
        self.ent_com.pack(side="left", padx=4)
        ttk.Label(top, text="波特率:", font=FONT).pack(side="left")
        self.var_baud = tk.StringVar(value=str(self.args.baud))
        self.ent_baud = ttk.Entry(top, textvariable=self.var_baud, width=7, font=FONT)
        self.ent_baud.pack(side="left", padx=4)

        self.btn_start = ttk.Button(top, text="启动服务端", command=self.on_start)
        self.btn_start.pack(side="left", padx=6)
        self.btn_stop = ttk.Button(top, text="停止", command=self.on_stop, state="disabled")
        self.btn_stop.pack(side="left")

        self.lbl_phase = ttk.Label(top, text="未启动", font=FONT_BOLD, foreground="#555555")
        self.lbl_phase.pack(side="left", padx=12)

        self._apply_transport_state()        # 按当前传输方式把不适用的输入框灰掉

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

        # 第二行：资源请求状态机 + 本端自测延迟（"点按钮没反应"时，答案就在这两行里）
        self.lbl_res = ttk.Label(info, text="资源: -", font=FONT_MONO, foreground="#444444")
        self.lbl_res.grid(row=1, column=0, columnspan=2, sticky="w", padx=8, pady=(0, 4))
        self.lbl_lat = ttk.Label(info, text="延迟: 未测过", font=FONT_MONO, foreground="#444444")
        self.lbl_lat.grid(row=1, column=2, columnspan=2, sticky="w", padx=8, pady=(0, 4))

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

        # --- 歌词（放在 MEDIA 正下方：左边一列 MEDIA → LYRICS → TIMELINE）---
        lyr = ttk.LabelFrame(left, text=" LYRICS（歌词资源 → Legacy 帧 → 行） ")
        lyr.pack(fill="both", expand=True, pady=(6, 0))
        self.txt_lyrics = scrolledtext.ScrolledText(lyr, height=10, font=FONT_MONO, wrap="none")
        self.txt_lyrics.pack(fill="both", expand=True, padx=8, pady=8)

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

        # --- 手动按钮 ---
        ctl = ttk.Frame(self.root)
        ctl.grid(row=3, column=0, sticky="ew", padx=8, pady=4)
        ttk.Label(ctl, text="手动（都走真 C 代码入队）:", font=FONT).pack(side="left")
        for text, code in (("PLAY_PAUSE", 1), ("NEXT", 2), ("PREV", 3)):
            ttk.Button(ctl, text=text, command=lambda c=code: self.on_control(c)).pack(side="left", padx=4)
        ttk.Button(ctl, text="请求歌词", command=lambda: self.on_cmd("req_lyrics")).pack(side="left", padx=12)
        ttk.Button(ctl, text="请求封面", command=lambda: self.on_cmd("req_cover")).pack(side="left", padx=4)

        # 封面请求格式（只影响下一次 ALBUMCOVER REQUEST）：跟"请求封面"放一起更顺手
        ttk.Label(ctl, text="格式:", font=FONT).pack(side="left", padx=(10, 2))
        self.var_fmt = tk.StringVar(value=FMT_NAMES.get(self.args.cover_format, "JPEG"))
        cbo_fmt = ttk.Combobox(ctl, textvariable=self.var_fmt, width=7, state="readonly",
                               values=list(FMT_CODES.keys()), font=FONT)
        cbo_fmt.pack(side="left")
        cbo_fmt.bind("<<ComboboxSelected>>", self.on_format_changed)

        ttk.Button(ctl, text="测延迟", command=self.on_latency).pack(side="left", padx=(16, 4))
        ttk.Label(ctl, text="（本端发起 30 样本）", font=FONT, foreground="#888888").pack(side="left")

        # --- 事件日志（放大：排查"点了没反应"主要看这里）---
        logf = ttk.LabelFrame(self.root, text=" 事件日志 ")
        logf.grid(row=4, column=0, sticky="nsew", padx=8, pady=(0, 8))
        self.root.rowconfigure(4, weight=4)
        self.txt_log = scrolledtext.ScrolledText(logf, height=14, font=FONT_MONO, background="#1E1E1E",
                                                 foreground="#CCCCCC", insertbackground="#CCCCCC")
        self.txt_log.pack(fill="both", expand=True, padx=8, pady=8)

    # ------------------------------------------------------------------ 动作

    def _fmt_code(self):
        return FMT_CODES.get(self.var_fmt.get(), 0x01)

    def _apply_transport_state(self):
        """按当前传输方式把不适用的输入框灰掉（构造期只调这个，不写日志）"""
        is_com = (self.var_transport.get().upper() == "COM")
        for w in (self.ent_ip, self.ent_port):
            w.config(state="disabled" if is_com else "normal")
        for w in (self.ent_com, self.ent_baud):
            w.config(state="normal" if is_com else "disabled")

    def on_transport_changed(self, _evt=None):
        """TCP / COM 切换（真正生效在下次「启动服务端」）"""
        self._apply_transport_state()
        if self.server is None:
            self._append_log("传输 → %s（上位机的「传输」要选成一样的）" % self.var_transport.get())

    def on_format_changed(self, _evt=None):
        """改封面请求格式：只影响下一次 ALBUMCOVER REQUEST，立刻生效（不用重启服务端）"""
        fmt = self._fmt_code()
        if self.server is not None:
            self.server.cover_format = fmt
        self._append_log("封面请求格式 → %s（0x%02X）" % (self.var_fmt.get(), fmt))

    def on_start(self):
        transport = self.var_transport.get().lower()
        try:
            port = int(self.var_port.get())
            baud = int(self.var_baud.get())
        except ValueError:
            self._append_log("端口 / 波特率不合法")
            return
        if (baud <= 0) or (transport == "com" and not self.var_com.get().strip()):
            self._append_log("串口号 / 波特率不合法")
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
            cover_format=self._fmt_code(),
            transport=transport,
            com_port=self.var_com.get().strip(),
            baud=baud,
            latency_samples=self.args.latency_samples,
            latency_interval_ms=self.args.latency_interval_ms,
            quiet=True,                      # GUI 里不逐帧刷控制台
            cli=self.args.trace,             # --trace 时把事件同时打到控制台（留日志用）
            serve_forever=True)              # 断线后继续监听，方便反复联调
        self.server.start()

        self.btn_start.config(state="disabled")
        self.btn_stop.config(state="normal")
        if transport == "com":
            self._append_log("已启动服务端（串口 %s@%d，内核 np_ref.dll）；"
                             "上位机请把「传输」设为 COM，端口填配对的那一端"
                             % (self.var_com.get().strip(), baud))
        else:
            self._append_log("已启动服务端（TCP %s:%d，内核 np_ref.dll —— 与真固件同一份 C 代码）；"
                             "封面请求格式 = %s"
                             % (self.var_ip.get().strip() or "127.0.0.1", port, self.var_fmt.get()))

    def on_stop(self):
        if self.server is not None:
            self.server.stop()
        self.btn_start.config(state="normal")
        self.btn_stop.config(state="disabled")
        self._append_log("已停止服务端")

    def _session_active(self):
        """(是否 SESSION ACTIVE, 状态名)；GUI 里凡是"点了没反应"都要在这里给一句明确的话"""
        if self.server is None:
            return False, "未启动"
        st = self.server.snapshot()
        return (st["session_state"] == 4), st["session_name"]

    def on_control(self, code):
        if self.server is None:
            self._append_log("服务端未启动")
            return
        active, name = self._session_active()
        if not active:
            self._append_log("⚠ 会话还没建立（当前 %s）→ 命令先排队，SESSION ACTIVE 后执行；"
                             "超过 3 秒作废" % name)
        self.server.post("control", code)

    def on_cmd(self, cmd):
        if self.server is None:
            self._append_log("服务端未启动")
            return
        active, name = self._session_active()
        if not active:
            self._append_log("⚠ 会话还没建立（当前 %s）→ 请求先排队，SESSION ACTIVE 后发送；"
                             "超过 3 秒作废" % name)
        self.server.post(cmd)

    def on_latency(self, samples=30):
        """本端发起一轮延迟测量（规范 §14：两端都能发起；同一时刻系统里只有一轮）"""
        if self.server is None:
            self._append_log("服务端未启动")
            return
        active, name = self._session_active()
        if not active:
            self._append_log("⚠ 延迟测量需要 SESSION ACTIVE（当前 %s）—— 等连上再点" % name)
        self.server.post("latency", samples)

    # ------------------------------------------------------------------ 刷新

    @staticmethod
    def _upd(widget, key, **kw):
        """值没变就**不碰控件**：避免每 200ms 无谓重绘（会打断选择/滚动，看起来像卡住）"""
        if getattr(widget, "_np_key", None) == key:
            return False
        widget._np_key = key
        widget.configure(**kw)
        return True

    def _tick(self):
        if self.server is not None:
            self._render(self.server.snapshot())
        self.root.after(200, self._tick)

    def _render(self, st):
        # 顶栏：阶段
        self._upd(self.lbl_phase, (st["phase"], st["session_state"]),
                  text=st["phase"],
                  foreground="#0A7D28" if st["session_state"] == 4 else "#B36B00")

        # 会话与统计
        self._upd(self.lbl_session, (st["session_name"], st["session_state"]),
                  text="会话: %s" % st["session_name"],
                  foreground="#0A7D28" if st["session_state"] == 4 else "#444444")
        self._upd(self.lbl_sid, st["session_id"],
                  text="SESSION_ID: 0x%08X" % st["session_id"])
        self._upd(self.lbl_stats,
                  (st["frames_rx"], st["crc_errors"], st["resyncs"], st["tx_dropped"], st["ack_sent"]),
                  text="帧=%d  crc错=%d  重同步=%d  tx丢弃=%d  ACK发出=%d"
                       % (st["frames_rx"], st["crc_errors"], st["resyncs"], st["tx_dropped"],
                          st["ack_sent"]))
        self._upd(self.lbl_extra,
                  (st["latency_responded"], st["control_sent"], st["peer"]),
                  text="延迟应答=%d  回控发出=%d  对端=%s"
                       % (st["latency_responded"], st["control_sent"], st["peer"] or "-"))

        # 资源请求状态机 + 本端自测延迟（"点了没反应"的答案就在这两行）
        res = st.get("resource") or {}
        res_text = "资源: 在途=%s  排队=%s  完成=%s  上次ACK=%s" % (
            res.get("inflight") or "-",
            ",".join(res.get("queue") or []) or "-",
            "  ".join("%s:%s" % (k, v) for k, v in sorted((res.get("done") or {}).items())) or "-",
            res.get("last_ack") or "-")
        self._upd(self.lbl_res, res_text, text=res_text)

        lat = st.get("latency")
        if lat:
            stats = lat.get("stats")
            lat_text = "延迟(本端发起): %s（收 %d / 发 %d%s）" % (
                core.format_lat(stats) if stats else "无样本",
                lat.get("recv", 0), lat.get("sent", 0),
                "" if lat.get("running") else "，" + (lat.get("note") or "已结束"))
        else:
            lat_text = "延迟(本端发起): 未测过（点「测延迟」；对端发起时本端只应答，见左侧计数）"
        self._upd(self.lbl_lat, lat_text, text=lat_text)

        # 媒体
        media = st["media"]
        if media:
            k = (media["title"], media["artist"], media["album"])
            self._upd(self.lbl_title, k, text=media["title"] or "(空标题)")
            self._upd(self.lbl_artist, k, text=media["artist"] or "-")
            self._upd(self.lbl_album, k, text=media["album"] or "-")

        # 时间轴（进度条该动就动，但只在数值变化时 set）
        tl = st["timeline"]
        if tl:
            total = max(tl["total_ms"], 1)
            self._upd(self.pb, (tl["cur_ms"], total),
                      maximum=total, value=min(tl["cur_ms"], total))
            self._upd(self.lbl_time, (tl["cur_ms"], tl["total_ms"], tl["playing"]),
                      text="%s / %s    播放中: %s"
                           % (fmt_ms(tl["cur_ms"]), fmt_ms(tl["total_ms"]),
                              "是" if tl["playing"] else "否"))
            self._upd(self.lbl_tick, (tl["host_tick"], tl["local_tick"], tl["delivered"]),
                      text="HOST_TICK=%d  LOCAL_TICK=%d   已投递 %d 帧"
                           % (tl["host_tick"], tl["local_tick"], tl["delivered"]))

        # 歌词：**只在资源换了才重画**（以前靠"读控件首行"比较，首行还带字节数 → 永远不相等 → 每 200ms 重画，抢焦点）
        lyrics = st["lyrics"]
        if lyrics:
            key = (lyrics["path"], lyrics["size"], len(lyrics["lines"]))
            if key != self._lyrics_key:
                self._lyrics_key = key
                self._fill_lyrics(lyrics)

        # 封面
        cover = st["cover"]
        if cover:
            self._upd(self.lbl_cover,
                      (cover["path"], cover["format"], cover["w"], cover["h"], cover["size"]),
                      text="格式=%s  0x%02X  %dx%d  %d 字节  → %s"
                           % (FMT_NAMES.get(cover["format"], "?"), cover["format"],
                              cover["w"], cover["h"], cover["size"], cover["path"]))
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

    def _fill_lyrics(self, lyrics):
        self.txt_lyrics.delete("1.0", "end")
        self.txt_lyrics.insert("end", "// %s（%d 字节，%d 行）\n\n"
                               % (lyrics["path"], lyrics["size"], len(lyrics["lines"])))
        for ln in lyrics["lines"]:
            mark = {"line": "  ", "trans": "    ↳", "word": "  ~"}[ln["kind"]]
            tail = "（逐字 %d 词）" % len(ln["words"]) if ln["kind"] == "word" else ""
            self.txt_lyrics.insert("end", "%s[%s] %s%s\n"
                                   % (mark, fmt_ms(ln["start_ms"]), ln["text"], tail))
        self._append_log("歌词区已渲染：%d 行（只在资源更新时重画）" % len(lyrics["lines"]))

    def _show_cover(self, cover):
        fmt, w, h, data = cover["format"], cover["w"], cover["h"], cover["data"]

        if (cover.get("size", 0) == 0) or (fmt == 0x00):   # §11：空资源 = 对端明确说"没有封面" → 清空
            self._photo = None
            self._pil_photo = None
            self.canvas.delete("all")
            self.canvas.create_text(245, 235, text="（对端无封面：已清空封面区）",
                                    fill="#999999", font=FONT)
            self._append_log("封面区已清空（对端回空资源 FORMAT=0x00，§11）")
            return

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
        if getattr(self, "txt_log", None) is None:      # 构造期（日志控件还没建好）→ 只打控制台
            print("[gui] %s" % text, flush=True)
            return
        self.txt_log.insert("end", text + "\n")
        if getattr(self.args, "trace", False):
            print("[gui] %s" % text, flush=True)      # --trace：GUI 侧消息也进控制台日志
        lines = int(self.txt_log.index("end-1c").split(".")[0])
        if lines > 600:
            self.txt_log.delete("1.0", "150.0")
        self.txt_log.see("end")


# ---------------------------------------------------------------- 入口

def parse_args():
    ap = argparse.ArgumentParser(description="New Protocol V1.1 ESP32 模拟器（图形版，内核 = 真 C 代码）")
    ap.add_argument("--transport", choices=("tcp", "com"), default="tcp",
                    help="链路：tcp（默认）或 com（串口；配合 --com/--baud）")
    ap.add_argument("--ip", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9100)
    ap.add_argument("--com", default="COM23",
                    help="串口号（默认 COM23；要与上位机的 COM 端口**配对**）")
    ap.add_argument("--baud", type=int, default=115200, help="串口波特率（与上位机 config.new.json 一致）")
    ap.add_argument("--dll", default=core.DEFAULT_DLL)
    ap.add_argument("--caps", default="lyrics,cover,jpeg,png,rgb565")
    ap.add_argument("--max-edge", type=int, default=240)
    ap.add_argument("--max-resource", type=int, default=262144)
    ap.add_argument("--tick-ms", type=int, default=10)
    ap.add_argument("--art-dir", default="art_recv")
    ap.add_argument("--cover-format", type=lambda s: int(s, 0), default=0x01,
                    help="请求封面时指定的 FORMAT：0x01=JPEG（默认，比 RGB565 小一个数量级）"
                         "0x02=PNG 0x10=RGB565")
    ap.add_argument("--trace", action="store_true",
                    help="把事件同时打印到控制台（GUI 出问题时留日志用）")
    ap.add_argument("--latency-samples", type=int, default=0,
                    help="会话建立后自动主动测一轮延迟（本端发起、对端应答）；0 = 只在你点「测延迟」时测")
    ap.add_argument("--latency-interval-ms", type=int, default=100,
                    help="延迟测量两次 LATENCY_REQUEST 的间隔（默认 100ms）")
    ap.add_argument("--headless", action="store_true", help="不开窗口，只跑服务端（脚本化验证用）")
    ap.add_argument("--seconds", type=float, default=0.0, help="headless 模式跑 N 秒后打印摘要并退出")
    return ap.parse_args()

def headless_run(dll, args, caps):
    """无窗口自检：跑服务端，结束时打印解码摘要（给自动化验证用）"""
    server = core.MockServer(dll, ip=args.ip, port=args.port, caps=caps,
                             max_edge=args.max_edge, max_resource=args.max_resource,
                             tick_ms=args.tick_ms, art_dir=args.art_dir,
                             cover_format=args.cover_format,
                             latency_samples=args.latency_samples,
                             latency_interval_ms=args.latency_interval_ms,
                             transport=args.transport, com_port=args.com, baud=args.baud,
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

    res = st.get("resource") or {}
    print("[gui] 资源请求：在途=%s 排队=%s 完成=%s 上次ACK=%s"
          % (res.get("inflight") or "-", ",".join(res.get("queue") or []) or "-",
             "  ".join("%s:%s" % (k, v) for k, v in sorted((res.get("done") or {}).items())) or "-",
             res.get("last_ack") or "-"))

    lat = st.get("latency")
    if lat:
        print("[gui] 延迟（本端发起）：%s（收 %d / 发 %d，%s）"
              % (core.format_lat(lat.get("stats")), lat.get("recv", 0), lat.get("sent", 0),
                 lat.get("note") or "进行中"))
    print("[gui] 对端发起延迟测量的应答次数：%d" % st["latency_responded"])

    server.stop()
    return 0


def main():
    args = parse_args()

    core.setup_utf8_console()

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
