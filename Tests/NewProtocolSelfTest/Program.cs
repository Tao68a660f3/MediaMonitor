using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MediaMonitor.Protocol.New.Codec;
using MediaMonitor.Protocol.New.Service;
using MediaMonitor.Protocol.New.Transport;
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

        using var transport = new TcpTransport(host, port);
        using var scheduler = new NewSendScheduler(transport, frameIntervalMs: 5);
        scheduler.Start();
        using var session = new SessionManager(scheduler);
        var parser = new NpStreamParser();

        transport.DataReceived += data => parser.Feed(data.Span);
        parser.FrameReceived += f => session.OnFrame(f);
        session.StateChanged += st =>
            Console.WriteLine($"[demo] 状态 -> {st}" + (st == ProtocolState.Active ? $"  SessionId=0x{session.SessionId:X8}" : string.Empty));
        session.BusinessFrame += f => Console.WriteLine($"[demo] 业务帧 T=0x{f.Type:X2} C=0x{f.Code:X2} len={f.PayloadLen}");
        transport.Disconnected += r => Console.WriteLine($"[demo] 链路断开: {r}");

        if (!await transport.ConnectAsync(3000))
        {
            Console.WriteLine("[demo] TCP 连接失败（mock 没在跑？）");
            return 4;
        }

        Console.WriteLine("[demo] TCP 已连接，发起 HELLO");
        session.LinkUp();

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

        bool ok = (session.State == ProtocolState.Active) && (session.SessionId != 0);

        Console.WriteLine($"[demo] 结果: State={session.State} SessionId=0x{session.SessionId:X8}" +
                          $" helloTx={session.StatHelloTx} helloRx={session.StatHelloRx} helloAckRx={session.StatHelloAckRx}" +
                          $" sessStartRx={session.StatSessionStartRx} ackTx={session.StatAckTx}" +
                          $" sentFrames={scheduler.SentFrames} 对端能力=0x{(session.RemoteCaps?.Caps ?? 0):X8}" +
                          $" 解析: 帧={parser.Stats.FramesOk} crc错={parser.Stats.CrcErrors} 重同步={parser.Stats.Resyncs}");
        Console.WriteLine(ok ? "[demo] === 握手成功（PC ↔ 假 ESP32）===" : "[demo] === 握手失败 ===");
        return ok ? 0 : 5;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 端到端握手模式：dotnet run -- --tcp 127.0.0.1:9100
        // （对端用 A_tools/mock_esp32_new.py，它跑的是 ref_c 编出来的真 C 代码）
        if ((args.Length >= 2) && (args[0] == "--tcp"))
        {
            return TcpHandshakeDemo(args[1]).GetAwaiter().GetResult();
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

        Console.WriteLine($"\n=== 通过 {_pass} 项，失败 {_fail} 项 ===");
        return _fail == 0 ? 0 : 1;
    }
}
