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
            if (_serialPort.IsOpen && data != null) _serialPort.Write(data, 0, data.Length);
        }

        private byte[] GetEncodedBytes(string text)
        {
            Encoding enc = SelectedEncoding == EncodingType.UTF8 ? Encoding.UTF8 : Encoding.GetEncoding("GB2312");
            return enc.GetBytes(text ?? "");
        }

        public byte[] BuildPacket(byte type, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 4];
            frame[0] = 0xAA;
            frame[1] = type;
            frame[2] = (byte)payload.Length;
            Array.Copy(payload, 0, frame, 3, payload.Length);
            byte crc = 0;
            for (int i = 0; i < payload.Length + 3; i++) crc ^= frame[i];
            frame[frame.Length - 1] = crc;
            return frame;
        }

        public byte[] BuildSync(bool isPlaying, uint currentMs, uint totalMs)
        {
            byte[] p = new byte[9];
            p[0] = (byte)(isPlaying ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(currentMs), 0, p, 1, 4);
            Array.Copy(BitConverter.GetBytes(totalMs), 0, p, 5, 4);
            return BuildPacket(0x11, p);
        }

        public byte[] BuildLyricWithIndex(byte type, ushort row, string text)
        {
            byte[] b = GetEncodedBytes(text);
            byte[] p = new byte[2 + b.Length];
            Array.Copy(BitConverter.GetBytes(row), 0, p, 0, 2);
            Array.Copy(b, 0, p, 2, b.Length);
            return BuildPacket(type, p);
        }

        public byte[] BuildWordByWord(ushort row, List<WordInfo> words, TimeSpan lineStartTime)
        {
            List<byte> p = new List<byte>();
            p.AddRange(BitConverter.GetBytes(row));
            p.Add((byte)words.Count);
            foreach (var w in words)
            {
                ushort offset = (ushort)Math.Max(0, (w.Time - lineStartTime).TotalMilliseconds);
                byte[] wordBytes = GetEncodedBytes(w.Word);
                p.AddRange(BitConverter.GetBytes(offset));
                p.Add((byte)wordBytes.Length);
                p.AddRange(wordBytes);
            }
            return BuildPacket(0x14, p.ToArray());
        }

        public byte[] BuildMetadata(string title, string artist, string album)
        {
            var b1 = GetEncodedBytes(title);
            var b2 = GetEncodedBytes(artist);
            var b3 = GetEncodedBytes(album);
            byte[] p = new byte[3 + b1.Length + b2.Length + b3.Length];
            p[0] = (byte)b1.Length; Array.Copy(b1, 0, p, 1, b1.Length);
            int cur = 1 + b1.Length;
            p[cur] = (byte)b2.Length; Array.Copy(b2, 0, p, cur + 1, b2.Length);
            cur += 1 + b2.Length;
            p[cur] = (byte)b3.Length; Array.Copy(b3, 0, p, cur + 1, b3.Length);
            return BuildPacket(0x10, p);
        }
    }
}