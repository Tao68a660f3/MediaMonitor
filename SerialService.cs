using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace MediaMonitor
{
    public enum EncodingType { UTF8, GB2312 }

    public class SerialService
    {
        private SerialPort _serialPort = new SerialPort();

        // 必须公开，让 UI 层的日志解码能对齐编码格式
        public EncodingType SelectedEncoding { get; set; } = EncodingType.UTF8;
        public bool IsOpen => _serialPort.IsOpen;

        public string[] GetPortNames() => SerialPort.GetPortNames();

        public void Connect(string portName, int baudRate)
        {
            if (_serialPort.IsOpen) _serialPort.Close();
            _serialPort.PortName = portName;
            _serialPort.BaudRate = baudRate;
            _serialPort.Open();
        }

        public void Disconnect() => _serialPort.Close();

        public void SendRaw(byte[] data)
        {
            if (_serialPort.IsOpen) _serialPort.Write(data, 0, data.Length);
        }

        public byte[] GetEncodedBytes(string text)
        {
            Encoding enc = SelectedEncoding == EncodingType.UTF8 ? Encoding.UTF8 : Encoding.GetEncoding("GB2312");
            return enc.GetBytes(text ?? "");
        }

        public byte[] BuildPacket(byte type, byte[] payload)
        {
            int len = payload.Length;
            byte[] frame = new byte[len + 4];
            frame[0] = 0xAA;
            frame[1] = type;
            frame[2] = (byte)len;
            Array.Copy(payload, 0, frame, 3, len);

            // 简单的累加校验
            byte checksum = 0;
            for (int i = 0; i < frame.Length - 1; i++) checksum += frame[i];
            frame[frame.Length - 1] = checksum;

            return frame;
        }

        // 0x10 元数据：标题、艺术家、专辑
        public byte[] BuildMetadata(string title, string artist, string album)
        {
            byte[] b1 = GetEncodedBytes(title);
            byte[] b2 = GetEncodedBytes(artist);
            byte[] b3 = GetEncodedBytes(album);

            List<byte> payload = new List<byte>();
            payload.Add((byte)b1.Length); payload.AddRange(b1);
            payload.Add((byte)b2.Length); payload.AddRange(b2);
            payload.Add((byte)b3.Length); payload.AddRange(b3);

            return BuildPacket(0x10, payload.ToArray());
        }

        // 0x11 同步包
        public byte[] BuildSync(bool isPlaying, uint currentMs, uint totalMs)
        {
            byte[] p = new byte[9];
            p[0] = (byte)(isPlaying ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(currentMs), 0, p, 1, 4);
            Array.Copy(BitConverter.GetBytes(totalMs), 0, p, 5, 4);
            return BuildPacket(0x11, p);
        }

        // 0x12/0x13 歌词与翻译 (带行索引)
        public byte[] BuildLyricWithIndex(byte type, ushort row, string text)
        {
            byte[] b = GetEncodedBytes(text);
            byte[] p = new byte[2 + b.Length];
            Array.Copy(BitConverter.GetBytes(row), 0, p, 0, 2);
            Array.Copy(b, 0, p, 2, b.Length);
            return BuildPacket(type, p);
        }

        // 0x14 逐字数据
        public byte[] BuildWordByWord(ushort row, List<WordInfo> words, TimeSpan start)
        {
            List<byte> p = new List<byte>();
            p.AddRange(BitConverter.GetBytes(row));
            p.Add((byte)words.Count);
            foreach (var w in words)
            {
                ushort offset = (ushort)Math.Max(0, (w.Time - start).TotalMilliseconds);
                byte[] wb = GetEncodedBytes(w.Word);
                p.AddRange(BitConverter.GetBytes(offset));
                p.Add((byte)wb.Length);
                p.AddRange(wb);
            }
            return BuildPacket(0x14, p.ToArray());
        }
    }
}