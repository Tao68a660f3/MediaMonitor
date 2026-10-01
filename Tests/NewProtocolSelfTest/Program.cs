using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaMonitor.Core;
using MediaMonitor.Protocol.New.Codec;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Protocol.New.Transport;
using MediaMonitor.Services;
using MediaMonitor.Tools;
using NewProtocolSelfTest;

/// <summary>
/// New Protocol V1.1 编解码层自测（实施计划 §4.2）。
/// 与 C 侧 ref_c/tests/np_selftest.c 使用同一批附录 A 向量：
/// 两侧都 PASS 且编码结果逐字节相同，才说明"大端 / CRC 参数 / CRC 覆盖范围"三件事没写错。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        if (ok) { _pass++; } else { _fail++; }
    }

    private static (List<NpFrame> Frames, NpStreamParser Parser) Run(byte[] data, int chunk = 0)
    {
        var parser = new NpStreamParser();
        var frames = new List<NpFrame>();
        parser.FrameReceived += f => frames.Add(f);

        if (chunk <= 0)
        {
            parser.Feed(data);
        }
        else
        {
            for (int off = 0; off < data.Length; off += chunk)
            {
                parser.Feed(data.AsSpan(off, Math.Min(chunk, data.Length - off)));
            }
        }
        return (frames, parser);
    }

    private static bool Matches(NpFrame f, NpVector v)
    {
        return f.Version == v.Version &&
               f.Flags == v.Flags &&
               f.Type == v.Type &&
               f.Code == v.Code &&
               f.HeaderLen == NpConstants.HeaderLenFixed &&
               f.PayloadLen == v.PayloadLen &&
               f.Sequence == v.Sequence &&
               f.SessionId == v.SessionId &&
               f.RequestId == v.RequestId &&
               f.Payload.Span.SequenceEqual(v.Payload);
    }

    private static byte[] BogusHeader(byte headerLen, uint payloadLen)
    {
        byte[] b = new byte[NpConstants.HeaderLenFixed];
        b[0] = NpConstants.Magic0;
        b[1] = NpConstants.Magic1;
        NpWriter.WriteU16(b.AsSpan(2), NpConstants.Version11);
        b[NpConstants.OffHeaderLen] = headerLen;
        NpWriter.WriteU32(b.AsSpan(NpConstants.OffPayloadLen), payloadLen);
        return b;
    }

    private static NpFrame MakeFrame(byte type, byte code, byte[] payload, uint sequence, uint sessionId, uint requestId, byte flags = 0)
    {
        return new NpFrame
        {
            Version = NpConstants.Version11,
            Flags = flags,
            Type = type,
            Code = code,
            HeaderLen = NpConstants.HeaderLenFixed,
            PayloadLen = (uint)payload.Length,
            Sequence = sequence,
            SessionId = sessionId,
            RequestId = requestId,
            Payload = payload
        };
    }

    /* ---------------- R1：CRC check value（规范 §4.1 / §12.1） ---------------- */
    private static void TestCrc()
    {
        byte[] s9 = Encoding.ASCII.GetBytes("123456789");

        Console.WriteLine("R1 CRC check values");
        Check("CRC16-CCITT-FALSE(\"123456789\") == 0x29B1", Crc16CcittFalse.Compute(s9) == 0x29B1);
        Check("CRC32(\"123456789\") == 0xCBF43926", Crc32Ieee.Compute(s9) == 0xCBF43926);
        Check("CRC32(\"\") == 0x00000000", Crc32Ieee.Compute(ReadOnlySpan<byte>.Empty) == 0x00000000);
        Check("CRC32(\"a\") == 0xE8B7BE43", Crc32Ieee.Compute(Encoding.ASCII.GetBytes("a")) == 0xE8B7BE43);

        uint st = Crc32Ieee.Init;
        st = Crc32Ieee.Update(st, s9.AsSpan(0, 4));
        st = Crc32Ieee.Update(st, s9.AsSpan(4, 5));
        Check("CRC32 分块续算 == 一次性计算", Crc32Ieee.Final(st) == 0xCBF43926);

        ushort c16 = Crc16CcittFalse.Update(Crc16CcittFalse.Init, s9.AsSpan(0, 4));
        c16 = Crc16CcittFalse.Update(c16, s9.AsSpan(4, 5));
        Check("CRC16 分块续算 == 一次性计算", c16 == 0x29B1);
    }

    /* ---------------- R2：解码附录 A 五条帧 ---------------- */
    private static void TestDecode()
    {
        Console.WriteLine("R2 解码附录 A 的五条帧");

        foreach (NpVector v in Vectors.All)
        {
            var (frames, parser) = Run(v.Bytes);
            Check($"{v.Name}: 解出 1 帧", frames.Count == 1);
            if (frames.Count == 1)
            {
                Check($"{v.Name}: 字段全部匹配（含 payload）", Matches(frames[0], v));
            }
            Check($"{v.Name}: framesOk=1 且 crcErrors=0", parser.Stats.FramesOk == 1 && parser.Stats.CrcErrors == 0);
        }
    }

    /* ---------------- R3：编码回环（与规范 hexdump 逐字节比对） ---------------- */
    private static void TestEncode()
    {
        Console.WriteLine("R3 编码回环（与规范 hexdump 逐字节比对）");

        byte[] dest = new byte[NpConstants.MaxFrameLen];
        foreach (NpVector v in Vectors.All)
        {
            NpFrame f = MakeFrame(v.Type, v.Code, v.Payload, v.Sequence, v.SessionId, v.RequestId, v.Flags);

            int n = NpEncoder.Encode(f, dest);
            string hex = Vectors.ToHex(dest.AsSpan(0, Math.Max(n, 0)));

            Check($"{v.Name}: 编码长度 == {v.Bytes.Length}", n == v.Bytes.Length);
            Check($"{v.Name}: 编码结果逐字节一致", n == v.Bytes.Length && dest.AsSpan(0, n).SequenceEqual(v.Bytes));

            Console.WriteLine($"        {v.Name,-14}{hex}");
        }
    }

    /* ---------------- R4：分片喂入（半包 / 任意切片） ---------------- */
    private static void TestChunked()
    {
        Console.WriteLine("R4 分片喂入（同一帧按 1/3/7 字节切片）");

        foreach (int chunk in new[] { 1, 3, 7 })
        {
            foreach (NpVector v in Vectors.All)
            {
                var (frames, _) = Run(v.Bytes, chunk);
                Check($"chunk={chunk} {v.Name}: 解出 1 帧且字段匹配",
                      frames.Count == 1 && Matches(frames[0], v));
            }
        }
    }

    /* ---------------- R5：误码与重同步（规范 §18.3） ---------------- */
    private static void TestResync()
    {
        Console.WriteLine("R5 误码与重同步");

        // (a) 前置垃圾字节（不含 MAGIC）
        {
            byte[] junk = { 0x00, 0x11, 0x22, 0xA5, 0x5A, 0x5A, 0x33 };
            var parser = new NpStreamParser();
            var frames = new List<NpFrame>();
            parser.FrameReceived += f => frames.Add(f);
            parser.Feed(junk);
            parser.Feed(Vectors.All[2].Bytes);      // TIMELINE
            Check("前置垃圾字节后仍能解出 TIMELINE", frames.Count == 1 && Matches(frames[0], Vectors.All[2]));
            Check("且发生了重同步计数", parser.Stats.Resyncs > 0);
        }

        // (b) 假 MAGIC + 非法 HEADER_LEN，随后是真帧
        {
            byte[] fake = { 0x5A, 0xA5, 0x01, 0x01, 0x00, 0x01, 0x01, 0x00 };
            var parser = new NpStreamParser();
            var frames = new List<NpFrame>();
            parser.FrameReceived += f => frames.Add(f);
            parser.Feed(fake);
            parser.Feed(Vectors.All[3].Bytes);      // MEDIA
            Check("假 MAGIC + 非法 HEADER_LEN 后仍能解出 MEDIA", frames.Count == 1 && Matches(frames[0], Vectors.All[3]));
            Check("非法头被计入 droppedBadLen", parser.Stats.DroppedBadLen > 0);
        }

        // (c) payload 内部含 5A A5 —— 证明"帧内不搜索 MAGIC"
        {
            byte[] pay = { 0x5A, 0xA5, 0x5A, 0xA5, 0xAA, 0x15, 0x00, 0x00 };
            byte[] frame = NpEncoder.EncodeToArray(MakeFrame(NpType.Lyrics, NpResCode.Data, pay, 9, 0x2A, 0))!;
            Check("payload 含 5A A5 的帧能正常编码", frame.Length == NpConstants.HeaderLenFixed + 8 + 2);

            var (frames, _) = Run(frame);
            Check("payload 含 5A A5 的帧能正常解码",
                  frames.Count == 1 && frames[0].PayloadLen == 8 && frames[0].Payload.Span.SequenceEqual(pay));
        }

        // (d) CRC 被篡改：丢帧但能恢复
        {
            byte[] bad = (byte[])Vectors.All[2].Bytes.Clone();
            bad[30] ^= 0xFF;

            var parser = new NpStreamParser();
            var frames = new List<NpFrame>();
            parser.FrameReceived += f => frames.Add(f);

            parser.Feed(bad);
            Check("CRC 不符的帧不被接受", frames.Count == 0);
            Check("CRC 错误被计数", parser.Stats.CrcErrors > 0);

            parser.Feed(Vectors.All[2].Bytes);
            Check("紧随其后的正确帧能解出", frames.Count == 1 && Matches(frames[0], Vectors.All[2]));
        }

        // (e) 非法 HEADER_LEN（0 / 超上限）
        {
            var parser = new NpStreamParser();
            var frames = new List<NpFrame>();
            parser.FrameReceived += f => frames.Add(f);

            parser.Feed(BogusHeader(0x00, 4));
            parser.Feed(BogusHeader(0xFF, 4));
            parser.Feed(Vectors.All[4].Bytes);      // ACK
            Check("非法 HEADER_LEN 被丢弃且随后帧正常", frames.Count == 1 && Matches(frames[0], Vectors.All[4]));
        }

        // (f) 非法 PAYLOAD_LEN（超 MaxPayloadLen）
        {
            var parser = new NpStreamParser();
            var frames = new List<NpFrame>();
            parser.FrameReceived += f => frames.Add(f);

            parser.Feed(BogusHeader((byte)NpConstants.HeaderLenFixed, 0x0000FFFF));
            parser.Feed(Vectors.All[0].Bytes);      // HELLO
            Check("非法 PAYLOAD_LEN 被丢弃且随后帧正常", frames.Count == 1 && Matches(frames[0], Vectors.All[0]));
        }

        // (g) 编码器拒绝非法入参（不产生半帧）
        {
            byte[] dest = new byte[NpConstants.MaxFrameLen];
            byte[] pay = { 1, 2, 3 };

            NpFrame bad1 = MakeFrame(0x01, 0x01, pay, 0, 0, 0);
            bad1 = new NpFrame
            {
                Version = bad1.Version, Flags = bad1.Flags, Type = bad1.Type, Code = bad1.Code,
                HeaderLen = 0x00, PayloadLen = bad1.PayloadLen, Sequence = 0, SessionId = 0, RequestId = 0,
                Payload = pay
            };
            Check("编码器拒绝 HEADER_LEN != 24", NpEncoder.Encode(bad1, dest) == 0);

            NpFrame bad2 = MakeFrame(0x01, 0x01, Array.Empty<byte>(), 0, 0, 0);
            bad2 = new NpFrame
            {
                Version = bad2.Version, Flags = bad2.Flags, Type = bad2.Type, Code = bad2.Code,
                HeaderLen = NpConstants.HeaderLenFixed, PayloadLen = 8, Sequence = 0, SessionId = 0, RequestId = 0,
                Payload = ReadOnlyMemory<byte>.Empty
            };
            Check("编码器拒绝 Payload 长度不足", NpEncoder.Encode(bad2, dest) == 0);

            NpFrame ok = MakeFrame(0x01, 0x01, Array.Empty<byte>(), 0, 0, 0);
            Check("编码器拒绝输出缓冲不足", NpEncoder.Encode(ok, dest.AsSpan(0, 10)) == 0);
        }
    }

    /* ---------------- R6：粘包 —— 多帧一次喂入 ---------------- */
    private static void TestSticky()
    {
        Console.WriteLine("R6 粘包（五条帧一次性喂入）");

        var all = new List<byte>();
        foreach (NpVector v in Vectors.All)
        {
            all.AddRange(v.Bytes);
        }

        var (frames, parser) = Run(all.ToArray());

        Check("一次喂入解出 5 帧", frames.Count == 5);
        bool ok = frames.Count == Vectors.All.Length;
        for (int i = 0; ok && i < frames.Count; i++)
        {
            ok = Matches(frames[i], Vectors.All[i]);
        }
        Check("5 帧顺序与内容全部正确", ok);
        Check("解析器无报错统计", parser.Stats.CrcErrors == 0 && parser.Stats.DroppedBadLen == 0);
    }

    /* ---------------- 端到端握手（PC ↔ Python mock，对端跑真 C 代码） ---------------- */

    private static async Task<int> TcpHandshakeDemo(string hostPort)
    {
        string[] parts = hostPort.Split(':');
        string host = parts[0];
        int port = (parts.Length > 1) ? int.Parse(parts[1]) : 9100;

        Console.WriteLine($"[demo] 连接 {host}:{port} ...");

        /* ---- P4：准备资源数据源（歌词池走真实的 LyricService 解析）---- */
        string lrcDir = Path.Combine(Path.GetTempPath(), "np_lrc_test");
        Directory.CreateDirectory(lrcDir);
        File.WriteAllText(Path.Combine(lrcDir, "测试歌手 - 测试歌曲.lrc"),
            "[offset:-200]\n[00:00.50]第一行歌词\n[00:03.00]第二行歌词\n[00:06.00]第三行歌词\n",
            new UTF8Encoding(false));

        var lyrics = new LyricService { LyricFolder = lrcDir };
        lyrics.LoadAndParse("测试歌曲", "测试歌手");
        Console.WriteLine($"[demo] 歌词解析：{lyrics.Lines.Count} 行（文件 offset 标签={lyrics.CurrentOffsetMs}ms）");

        byte[] coverJpeg = MakeTestCoverJpeg(256, forceTestPattern: false);
        Console.WriteLine($"[demo] 合成测试封面：{coverJpeg.Length} 字节");

        using var transport = new TcpTransport(host, port);
        using var scheduler = new NewSendScheduler(transport, frameIntervalMs: 5);
        scheduler.Start();
        using var session = new SessionManager(scheduler);
        var parser = new NpStreamParser();
        var control = new ControlReceiver(session,
            cmd => Console.WriteLine($"[demo] 收到 CONTROL → 执行 Legacy 媒体键 0x{cmd:X2}"));
        var media = new MediaPublisher(session);
        using var pump = new TimelinePump(session, new FakeTimelineSource(), intervalMs: 500);
        using var latency = new LatencyManager(session) { WindowSize = 30, PingIntervalMs = 100 };

        // 资源发送：歌词用整份歌词池，封面按对端请求的尺寸/格式现做
        var resources = new ResourceSender(session, (type, w, h, fmt, q) =>
        {
            if (type == NpType.Lyrics)
            {
                byte[] data = LyricResourceBuilder.Build(lyrics, TimeSpan.FromSeconds(100), out int frames);
                Console.WriteLine($"[demo] 生成歌词资源：{data.Length} 字节 / {frames} 帧（Legacy 帧字节流）");
                return new NpResourceData(Crc32Ieee.Compute(data), data, 0, 0, 0);
            }

            NpResourceData? art = ArtworkResourceBuilder.Build(coverJpeg, w, h, fmt, q, out string? err);
            if (art == null)
            {
                Console.WriteLine($"[demo] 封面构造失败：{err}");
            }
            else
            {
                Console.WriteLine($"[demo] 生成封面资源：{art.Data.Length} 字节 FORMAT=0x{art.Format:X2} {art.Width}×{art.Height}");
            }
            return art;
        }, chunkSize: 1024);

        transport.DataReceived += data => parser.Feed(data.Span);
        parser.FrameReceived += f =>
        {
            resources.OnFrame(f);                        // REQUEST / ACK（必须在会话层之前）
            if (session.OnFrame(f))
            {
                return;                                  // SYSTEM：会话层已处理
            }
            latency.OnFrame(f);                          // LATENCY_REQUEST / RESPONSE / END
            if (control.OnFrame(f))
            {
                return;                                  // CONTROL：已执行并回 ACK
            }
            Console.WriteLine($"[demo] 业务帧 T=0x{f.Type:X2} C=0x{f.Code:X2} len={f.PayloadLen}");
        };
        session.StateChanged += st =>
            Console.WriteLine($"[demo] 状态 -> {st}" + (st == ProtocolState.Active ? $"  SessionId=0x{session.SessionId:X8}" : string.Empty));
        transport.Disconnected += r => Console.WriteLine($"[demo] 链路断开: {r}");

        if (!await transport.ConnectAsync(3000))
        {
            Console.WriteLine("[demo] TCP 连接失败（mock 没在跑？）");
            return 4;
        }

        Console.WriteLine("[demo] TCP 已连接，发起 HELLO");
        session.LinkUp();

        // ---- 阶段 1：握手 ----
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            session.Tick();
            await Task.Delay(10);
            if (session.State == ProtocolState.Active)
            {
                await scheduler.WaitDrainedAsync(500);
                break;
            }
        }

        bool handshakeOk = (session.State == ProtocolState.Active) && (session.SessionId != 0);
        Console.WriteLine(handshakeOk
            ? $"[demo] === 握手成功，SessionId=0x{session.SessionId:X8} ==="
            : "[demo] === 握手失败 ===");

        if (!handshakeOk)
        {
            return 5;
        }

        // ---- 阶段 2（P3）：会话建立后立即推元数据 + 启动时间轴 ----
        // 顺序很重要：先 PublishNow() 补发首帧，再 Start() 进入常规节拍，
        // 否则 Start() 的首次触发会与 PublishNow() 撞在一起（重复一帧）。
        media.Publish("测试歌曲", "测试歌手", "测试专辑");
        pump.PublishNow();
        pump.Start();
        Console.WriteLine("[demo] 已推送 MEDIA + 启动 TIMELINE；等待对端（mock）拉取歌词与封面...");

        await Task.Delay(12000);

        // 切歌：元数据更新 + 时间轴立即补发；若还有资源在途则 ABORT（规范 §9.7 / N-13）
        resources.AbortCurrent("切歌：资源作废");
        media.Publish("第二首歌", "另一个歌手", "另一张专辑");
        await Task.Delay(2500);

        pump.Stop();
        await scheduler.WaitDrainedAsync(1500);

        // ---- 阶段 3（P5）：延迟测量（PC 发起，mock 作为应答方）----
        Console.WriteLine("[demo] 开始延迟测量（30 样本 / 100ms 间隔）...");
        latency.StatsUpdated += s =>
        {
            if ((s.SampleCount % 10) == 0)
            {
                Console.WriteLine($"        样本 {s.SampleCount}: {s}");
            }
        };
        latency.Start();
        var swLat = Stopwatch.StartNew();
        while (latency.IsRunning && swLat.ElapsedMilliseconds < 8000)
        {
            await Task.Delay(50);
        }
        latency.Stop();
        await scheduler.WaitDrainedAsync(500);
        Console.WriteLine($"[demo] P5 统计: {latency.LastStats} 结束原因='{latency.LastEndReason}'");
        Console.WriteLine($"[demo] P5 计数: REQUEST={latency.StatRequestsSent} RESPONSE={latency.StatResponsesRecv}" +
                          $" 应答对端={latency.StatResponded} 超时={latency.StatTimeouts} 被抢占={latency.StatPreempted}");

        Console.WriteLine($"[demo] 结果: State={session.State} SessionId=0x{session.SessionId:X8} 对端能力=0x{(session.RemoteCaps?.Caps ?? 0):X8}");
        Console.WriteLine($"[demo] P3 统计: MEDIA={media.StatMediaTx} TIMELINE={pump.StatTimelineTx}(立即={pump.StatImmediateTx})" +
                          $" CONTROL={control.StatControlRx}(最后 0x{control.LastLegacyCmd:X2})");
        Console.WriteLine($"[demo] P4 统计: REQUEST={resources.StatRequests} 传输={resources.StatTransfers} 字节={resources.StatBytes}" +
                          $" NOT_READY={resources.StatNotReady} REJECTED={resources.StatRejected}" +
                          $" ABORT={resources.StatAborted} END_ACK失败={resources.StatAckFail}");
        Console.WriteLine($"[demo] 解析统计: 帧={parser.Stats.FramesOk} crc错={parser.Stats.CrcErrors} 发送帧={scheduler.SentFrames}");

        bool ok = session.IsActive &&
                  media.StatMediaTx >= 2 &&          // 首推 + 切歌
                  pump.StatTimelineTx >= 10 &&       // 15 秒 × 500ms ≈ 30 帧
                  control.StatControlRx >= 1 &&      // 收到 mock 的按键回控
                  resources.StatTransfers >= 1 &&    // 至少传了一份资源（歌词/封面）
                  (latency.LastStats?.SampleCount ?? 0) >= 30;   // 延迟测量满窗

        Console.WriteLine(ok
            ? "[demo] === P3/P4/P5 验收通过（实时数据 + 资源传输 + 回控 + 延迟测量）==="
            : "[demo] === 验收未通过 ===");
        return ok ? 0 : 6;
    }

    /* ---------------- 合成测试图（不依赖外部素材） ---------------- */

    /// <summary>合成一张封面测试图（渐变 + 文字）；<paramref name="forceTestPattern"/> 时输出 2×2 纯色 PNG</summary>
    private static byte[] MakeTestCoverJpeg(int size, bool forceTestPattern)
    {
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            if (forceTestPattern)
            {
                // 2×2：红 / 绿 / 蓝 / 白（用于校验 RGB565 字节序）
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 1, 1));
                dc.DrawRectangle(Brushes.Lime, null, new Rect(1, 0, 1, 1));
                dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 1, 1, 1));
                dc.DrawRectangle(Brushes.White, null, new Rect(1, 1, 1, 1));
            }
            else
            {
                dc.DrawRectangle(new LinearGradientBrush(Colors.DarkOrange, Colors.MediumPurple, 45), null,
                                 new Rect(0, 0, size, size));

                var ft = new FormattedText("TEST", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           new Typeface("Segoe UI"), size * 0.22, Brushes.White, 96);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
        }

        rtb.Render(visual);

        BitmapEncoder enc = forceTestPattern
            ? new PngBitmapEncoder()                       // 纯色校验必须无损
            : new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>演示用的假时间轴源：1 秒走 1 秒，100 秒循环</summary>
    private sealed class FakeTimelineSource : ITimelineSource
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public TimelineSnapshot? GetSnapshot()
        {
            TimeSpan total = TimeSpan.FromSeconds(100);
            return new TimelineSnapshot(true, TimeSpan.FromSeconds(_sw.Elapsed.TotalSeconds % 100), total);
        }
    }

    /* ---------------- R9：封面 RGB565 出口（字节序 / 尺寸） ---------------- */

    private static void TestRgb565()
    {
        Console.WriteLine("R9 封面 RGB565 出口（字节序 / 尺寸）");

        byte[] png = MakeTestCoverJpeg(2, forceTestPattern: true);
        ArtworkImage? img = ArtworkProcessor.ProcessToRgb565(png, 2, 2);

        Check("R9 输出非 null 且尺寸 2×2", (img != null) && (img.Width == 2) && (img.Height == 2));
        if (img == null)
        {
            return;
        }

        Check("R9 字节数 = w×h×2 = 8", img.Data.Length == 8);

        string hex = string.Join(" ", img.Data.Select(b => b.ToString("X2")));
        Console.WriteLine($"        2×2 RGB565 字节: {hex}");

        // 期望（低字节在前、R 在高 5 位）：红 00 F8 / 绿 E0 07 / 蓝 1F 00 / 白 FF FF
        Check("R9 红 = 00 F8", (img.Data[0] == 0x00) && (img.Data[1] == 0xF8));
        Check("R9 绿 = E0 07", (img.Data[2] == 0xE0) && (img.Data[3] == 0x07));
        Check("R9 蓝 = 1F 00", (img.Data[4] == 0x1F) && (img.Data[5] == 0x00));
        Check("R9 白 = FF FF", (img.Data[6] == 0xFF) && (img.Data[7] == 0xFF));
    }

    /* ---------------- R10：时间轴同步偏移包装（SyncCurrentOffsetMs） ---------------- */

    /// <summary>固定快照的数据源（R10 用：不依赖 SMTC，也就没有时间漂移）</summary>
    private sealed class FixedTimelineSource : ITimelineSource
    {
        private readonly TimelineSnapshot? _snap;

        public FixedTimelineSource(TimelineSnapshot? snap) => _snap = snap;

        public TimelineSnapshot? GetSnapshot() => _snap;
    }

    private static void TestOffsetSource()
    {
        Console.WriteLine("R10 时间轴同步偏移（SyncCurrentOffsetMs）");

        var src = new FixedTimelineSource(new TimelineSnapshot(true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100)));

        var pass = new OffsetTimelineSource(src, 0).GetSnapshot();
        Check("偏移 0 → 位置不动", (pass is { } p0) && (Math.Abs(p0.Position.TotalMilliseconds - 10000) < 0.001));

        var lead = new OffsetTimelineSource(src, 250).GetSnapshot();
        Check("偏移 +250ms → 位置前移 250ms（提前发出）",
              (lead is { } p1) && (Math.Abs(p1.Position.TotalMilliseconds - 10250) < 0.001));
        Check("偏移不改变 IS_PLAYING / 总时长",
              (lead is { } p2) && p2.IsPlaying && (Math.Abs(p2.Duration.TotalMilliseconds - 100000) < 0.001));

        var negSrc = new FixedTimelineSource(new TimelineSnapshot(true, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(100)));
        var behind = new OffsetTimelineSource(negSrc, -500).GetSnapshot();
        Check("偏移 -500ms 且位置仅 100ms → 夹到 0（不出现负进度）",
              (behind is { } p3) && (p3.Position == TimeSpan.Zero));

        var none = new OffsetTimelineSource(new FixedTimelineSource(null), 100).GetSnapshot();
        Check("数据源为 null（无会话）→ 仍为 null（发包层据此不发时间轴）", none == null);
    }

    /* ---------------- R11：配置容错 + 双配置文件（P6） ---------------- */

    private static string ConfigPath(string fileName)
        => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

    private static void WriteConfig(string fileName, string json)
        => File.WriteAllText(ConfigPath(fileName), json, new UTF8Encoding(false));

    private static void TestConfig()
    {
        Console.WriteLine("R11 配置容错（config.json / config.new.json 各自独立）");

        const string newFile = "np_cfg_new_test.json";
        const string legacyFile = "np_cfg_legacy_test.json";

        try
        {
            /* (a) 文件不存在 → 全套默认值 */
            File.Delete(ConfigPath(newFile));
            var fresh = new ConfigService<NewProtocolConfig>(newFile).Current;
            Check("(a) 无文件 → New 默认（Com / COM3 / 115200 / 500ms）",
                  (fresh.TransportMode == NewTransportType.Com) && (fresh.ComPortName == "COM3") &&
                  (fresh.BaudRate == 115200) && (fresh.SyncIntervalMs == 500));

            /* (b) 单项非法 → 只回退该项；未知键忽略；枚举可用字符串写 */
            WriteConfig(newFile, """
                {
                  // 注释与尾随逗号都应被容忍
                  "TransportMode": "Tcp",
                  "TcpRemotePort": 9200,
                  "SyncIntervalMs": "abc",
                  "SyncCurrentOffsetMs": 33,
                  "LyricFolder": "M:\\Lyrics_New",
                  "NoSuchKey": 123,
                }
                """);

            var mixed = new ConfigService<NewProtocolConfig>(newFile).Current;
            Check("(b1) 枚举写成字符串 TransportMode=Tcp 生效", mixed.TransportMode == NewTransportType.Tcp);
            Check("(b2) 合法项 TcpRemotePort / LyricFolder 生效",
                  (mixed.TcpRemotePort == 9200) && (mixed.LyricFolder == "M:\\Lyrics_New"));
            Check("(b3) 非法项 SyncIntervalMs 只回退自己（500），不牵连同文件其他项",
                  (mixed.SyncIntervalMs == 500) && (mixed.SyncCurrentOffsetMs == 33));
            Check("(b4) 未知键被忽略且不影响解析（ComPortName 仍是默认 COM3）", mixed.ComPortName == "COM3");

            /* (c) 键名大小写不敏感 */
            WriteConfig(newFile, "{ \"comportname\": \"COM9\", \"syncintervalms\": 750 }");
            var ci = new ConfigService<NewProtocolConfig>(newFile).Current;
            Check("(c) 键名大小写不敏感", (ci.ComPortName == "COM9") && (ci.SyncIntervalMs == 750));

            /* (d) 结构损坏 → 整体回退默认（只有这一种情况会整份丢弃） */
            WriteConfig(newFile, "{ \"SyncIntervalMs\": 750,  ");
            var broken = new ConfigService<NewProtocolConfig>(newFile).Current;
            Check("(d) JSON 结构损坏 → 整体回退默认", broken.SyncIntervalMs == 500);

            /* (e) 写盘 → 重新加载一致（WindowBounds 这类运行期字段不会丢） */
            var svc = new ConfigService<NewProtocolConfig>(newFile);
            svc.Current.ComPortName = "COM7";
            svc.Current.SyncIntervalMs = 250;
            svc.Current.WindowBounds = "100,120,520,850";
            svc.Save();

            var reload = new ConfigService<NewProtocolConfig>(newFile).Current;
            Check("(e) 保存后重载一致（ComPortName / SyncIntervalMs / WindowBounds）",
                  (reload.ComPortName == "COM7") && (reload.SyncIntervalMs == 250) &&
                  (reload.WindowBounds == "100,120,520,850"));

            /* (f) Legacy 服务泛型化后行为不变 */
            File.Delete(ConfigPath(legacyFile));
            var legacy = new ConfigService(legacyFile);
            Check("(f1) 无文件 → Legacy 默认（Serial / 115200 / 高级模式）",
                  (legacy.Current.TransportMode == TransportType.Serial) &&
                  (legacy.Current.BaudRate == 115200) && legacy.Current.IsAdvancedMode);

            legacy.Current.SerialPortName = "COM11";
            legacy.Update(legacy.Current);
            var legacyReload = new ConfigService(legacyFile).Current;
            Check("(f2) Legacy 保存后重载一致", legacyReload.SerialPortName == "COM11");
            Check("(f3) 两份配置各自独立（互不串味）",
                  (legacyReload.SyncIntervalMs == 500) && (legacyReload.LyricFolder != "M:\\Lyrics_New"));
        }
        finally
        {
            File.Delete(ConfigPath(newFile));
            File.Delete(ConfigPath(legacyFile));
        }
    }

    /* ---------------- SMTC 真源探针：量发包节奏与"分因"（P6 抖动问题定位） ---------------- */

    /// <summary>
    /// 用**真实 SMTC**作为时间轴数据源，连到 mock 跑 N 秒，打印发包总数与分因统计。
    /// 用途：验证"数据源抖动"是否会把 500ms 节奏打成高频（护栏是否生效）。
    /// </summary>
    private static async Task<int> SmtcTimelineProbe(string hostPort, int seconds)
    {
        string[] parts = hostPort.Split(':');
        string host = parts[0];
        int port = (parts.Length > 1) ? int.Parse(parts[1]) : 9100;

        var smtc = new SmtcService();
        await smtc.InitializeAsync();
        await Task.Delay(2000);
        Console.WriteLine($"[probe] SMTC 就绪：title={smtc.CurrentTitle ?? "(无)"}  thumbnail={(smtc.CurrentThumbnail?.Length ?? 0)}B");
        using var transport = new TcpTransport(host, port);
        using var scheduler = new NewSendScheduler(transport, frameIntervalMs: 5);
        scheduler.Start();
        using var session = new SessionManager(scheduler);
        var parser = new NpStreamParser();
        using var pump = new TimelinePump(session,
                                          new OffsetTimelineSource(new SmtcTimelineSource(smtc), 10),
                                          intervalMs: 500);

        transport.DataReceived += d => parser.Feed(d.Span);
        parser.FrameReceived += f => session.OnFrame(f);

        // 与会话栈同样的消费姿势：**只在"进入 Active"那一次**做事
        //（会话层的通知可能在状态未变时也会来，不能当成"刚进入"）
        ProtocolState prevState = ProtocolState.Idle;
        session.StateChanged += st =>
        {
            bool entered = st != prevState;
            prevState = st;

            if (!entered || (st != ProtocolState.Active))
            {
                return;
            }

            session.SendRealtime(NpType.Media, NpMediaCode.Metadata,
                                 new MediaPublisher(session).BuildPayload(smtc.CurrentTitle, smtc.CurrentArtist, smtc.CurrentAlbum));
            pump.PublishNow();
            pump.Start();
        };

        if (!await transport.ConnectAsync(3000))
        {
            Console.WriteLine("[probe] TCP 连接失败（mock 没在跑？）");
            return 4;
        }

        session.LinkUp();

        var sw = Stopwatch.StartNew();
        long lastTx = 0;
        Console.WriteLine($"[probe] 运行 {seconds}s；每 5s 打印发包节奏（MinGapMs={pump.MinGapMs}）");

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            session.Tick();
            await Task.Delay(10);

            if ((sw.ElapsedMilliseconds / 5000) != (sw.ElapsedMilliseconds - 10) / 5000)
            {
                long tx = pump.StatTimelineTx;
                Console.WriteLine($"[probe] t={sw.Elapsed.TotalSeconds,5:0.0}s  TIMELINE={tx}（本段 {tx - lastTx}）" +
                                  $" due={pump.StatSentDue} playing变={pump.StatSentPlayingChange} seek={pump.StatSentSeek} 即时={pump.StatImmediateTx}");
                lastTx = tx;
            }
        }

        double rate = pump.StatTimelineTx / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Console.WriteLine($"[probe] 合计 {sw.Elapsed.TotalSeconds:0.0}s  TIMELINE={pump.StatTimelineTx}（{rate:0.00} 帧/秒）" +
                          $" 会话={session.State}");
        Console.WriteLine($"[probe] 发送面：调度器已发={scheduler.SentFrames} 丢弃={scheduler.DroppedFrames}" +
                          $"  HELLO={session.StatHelloTx}/收 {session.StatHelloAckRx}  ACK发={session.StatAckTx}/收={session.StatAckRx}" +
                          $"  重传超时={session.StatAckTimeout}  业务帧收={session.StatBusinessFrames}");
        Console.WriteLine($"[probe] 接收面：解析成功={parser.Stats.FramesOk} crc错={parser.Stats.CrcErrors}" +
                          $" 重同步={parser.Stats.Resyncs} 坏长度={parser.Stats.DroppedBadLen} 字节={parser.Stats.BytesIn}" +
                          $"  发送队列积压={scheduler.PendingCount}");
        Console.WriteLine("[probe] 判据：2 帧/秒左右为正常（500ms 节拍）；若远高于此说明抖动护栏/数据源仍需处理");

        return (rate < 6.0) ? 0 : 5;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 端到端握手模式：dotnet run -- --tcp 127.0.0.1:9100
        // （对端用 A_tools/mock_esp32_new.py，它跑的是 ref_c 编出来的真 C 代码）
        if ((args.Length >= 2) && (args[0] == "--tcp"))
        {
            return TcpHandshakeDemo(args[1]).GetAwaiter().GetResult();
        }

        // SMTC 真源探针：dotnet run -- --tcp-smtc 127.0.0.1:9100 20
        // （P6 定位"数据源抖动把 500ms 节拍打成高频"用；WinRT 调用放到线程池上避免 STA 死锁）
        if ((args.Length >= 2) && (args[0] == "--tcp-smtc"))
        {
            int secs = (args.Length >= 3) ? int.Parse(args[2]) : 20;
            return Task.Run(() => SmtcTimelineProbe(args[1], secs)).GetAwaiter().GetResult();
        }

        Console.WriteLine("=== New Protocol V1.1  C# 编解码层自测 ===");
        Console.WriteLine($"HeaderLenFixed={NpConstants.HeaderLenFixed}  MaxHeaderLen={NpConstants.MaxHeaderLen}  " +
                          $"MaxPayloadLen={NpConstants.MaxPayloadLen}  MaxFrameLen={NpConstants.MaxFrameLen}\n");

        TestCrc();
        Console.WriteLine();
        TestDecode();
        Console.WriteLine();
        TestEncode();
        Console.WriteLine();
        TestChunked();
        Console.WriteLine();
        TestResync();
        Console.WriteLine();
        TestSticky();
        Console.WriteLine();
        TestRgb565();
        Console.WriteLine();
        TestOffsetSource();
        Console.WriteLine();
        TestConfig();

        Console.WriteLine($"\n=== 通过 {_pass} 项，失败 {_fail} 项 ===");
        return _fail == 0 ? 0 : 1;
    }
}
