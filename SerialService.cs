using System.IO.Ports;
using System.Text;

namespace MediaMonitor
{
    public enum EncodingType { UTF8, GB2312 }

    public class SerialService
    {
        private SerialPort _port = new SerialPort();
        public bool IsOpen => _port.IsOpen;
        public EncodingType SelectedEncoding { get; set; } = EncodingType.UTF8;

        public string[] GetPortNames() => SerialPort.GetPortNames();

        public void Connect(string portName, int baudRate)
        {
            if (_port.IsOpen) _port.Close();
            _port.PortName = portName;
            _port.BaudRate = baudRate;
            _port.Open();
        }

        public void Disconnect() => _port.Close();

        public Encoding CurrentEncoding =>
            SelectedEncoding == EncodingType.GB2312 ? Encoding.GetEncoding("GB2312") : Encoding.UTF8;

        public byte[] GetEncodedBytes(string text) => CurrentEncoding.GetBytes(text);

        // 基础打包逻辑：AA [Cmd] [Len] [Payload] [Check]
        public byte[] BuildPacket(byte cmd, byte[] payload)
        {
            List<byte> frame = new List<byte> { 0xAA, cmd, (byte)payload.Length };
            frame.AddRange(payload);
            byte check = 0;
            foreach (var b in payload) check ^= b;
            frame.Add(check);
            return frame.ToArray();
        }

        // 统一 Header 构建：Index(2B) + StartTime(4B)
        private List<byte> BuildHeader(short absIdx, uint startTimeMs)
        {
            var header = new List<byte>();
            header.AddRange(BitConverter.GetBytes(absIdx));      // Int16, 2B
            header.AddRange(BitConverter.GetBytes(startTimeMs)); // UInt32, 4B
            return header;
        }

        // 0x20: 对时包
        public byte[] BuildTimeSync()
        {
            var now = DateTime.Now;
            // 计算星期：C# DayOfWeek 是枚举（周日=0, 周一=1...），
            // 需根据你单片机 DS1302 驱动的要求转换（通常 DS1302 周一至周日是 1-7）
            byte week = (byte)(now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek);

            byte[] payload = new byte[] {
                (byte)(now.Year % 100), // 年 (如 24)
                (byte)now.Month,         // 月
                (byte)now.Day,           // 日
                (byte)now.Hour,          // 时
                (byte)now.Minute,        // 分
                (byte)now.Second,        // 秒
                week                     // 周
            };

            // 假设你的包格式是: [Header][CMD][LEN][Payload][CheckSum]
            // 或者是你 DecodeMeta 里那种 ptr 风格。按你之前的逻辑封装：
            return BuildPacket(0x20, payload);
        }

        // 0x12: 原文包
        public byte[] BuildLyricLine(short absIdx, TimeSpan startTime, string content)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.AddRange(GetEncodedBytes(content));
            return BuildPacket(0x12, p.ToArray());
        }

        // 0x13: 翻译包
        public byte[] BuildTranslationLine(short absIdx, TimeSpan startTime, string translation)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.AddRange(GetEncodedBytes(translation));
            return BuildPacket(0x13, p.ToArray());
        }

        // 0x14: 逐字包
        public byte[] BuildWordByWord(short absIdx, TimeSpan startTime, List<WordInfo> words)
        {
            var p = BuildHeader(absIdx, (uint)startTime.TotalMilliseconds);
            p.Add((byte)words.Count); // 词数 (1B)
            foreach (var w in words)
            {
                ushort offset = (ushort)Math.Max(0, (w.Time - startTime).TotalMilliseconds);
                byte[] wordBytes = GetEncodedBytes(w.Word);
                p.AddRange(BitConverter.GetBytes(offset)); // 偏移 (2B)
                p.Add((byte)wordBytes.Length);             // 长度 (1B)
                p.AddRange(wordBytes);                     // 文本
            }
            return BuildPacket(0x14, p.ToArray());
        }

        // 0x11: 同步包 (播放状态)
        public byte[] BuildSync(bool isPlaying, uint currentMs, uint totalMs)
        {
            List<byte> p = new List<byte> { (byte)(isPlaying ? 1 : 0) };
            p.AddRange(BitConverter.GetBytes(currentMs));
            p.AddRange(BitConverter.GetBytes(totalMs));
            return BuildPacket(0x11, p.ToArray());
        }

        // 0x10: 媒体元数据
        public byte[] BuildMetadata(string title, string artist, string album)
        {
            List<byte> p = new List<byte>();
            byte[] t = GetEncodedBytes(title); p.Add((byte)t.Length); p.AddRange(t);
            byte[] r = GetEncodedBytes(artist); p.Add((byte)r.Length); p.AddRange(r);
            byte[] b = GetEncodedBytes(album); p.Add((byte)b.Length); p.AddRange(b);
            return BuildPacket(0x10, p.ToArray());
        }

        public void SendRaw(byte[] data) { if (_port.IsOpen) _port.Write(data, 0, data.Length); }
    }
}