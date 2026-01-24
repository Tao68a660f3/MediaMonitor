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
            int checksum = frame[0] + frame[1] + frame[2];
            foreach (byte b in payload) checksum += b;
            frame[frame.Length - 1] = (byte)(checksum & 0xFF);
            return frame;
        }

        public byte[] BuildMetadata(string t, string a, string al)
        {
            var b1 = GetEncodedBytes(t); var b2 = GetEncodedBytes(a); var b3 = GetEncodedBytes(al);
            var p = b1.Concat(new byte[] { 0 }).Concat(b2).Concat(new byte[] { 0 }).Concat(b3).ToArray();
            return BuildPacket(0x10, p);
        }

        public byte[] BuildSync(bool p, uint c, uint t)
        {
            byte[] pay = new byte[9]; pay[0] = (byte)(p ? 1 : 0);
            Array.Copy(BitConverter.GetBytes(c), 0, pay, 1, 4);
            Array.Copy(BitConverter.GetBytes(t), 0, pay, 5, 4);
            return BuildPacket(0x11, pay);
        }

        public byte[] BuildLyricWithIndex(byte type, ushort idx, string text)
        {
            byte[] b = GetEncodedBytes(text);
            byte[] p = new byte[2 + b.Length];
            Array.Copy(BitConverter.GetBytes(idx), 0, p, 0, 2);
            Array.Copy(b, 0, p, 2, b.Length);
            return BuildPacket(type, p);
        }

        public byte[] BuildWordByWord(ushort idx, List<WordInfo> words, TimeSpan start)
        {
            List<byte> p = new List<byte>();
            p.AddRange(BitConverter.GetBytes(idx));
            p.Add((byte)words.Count);
            foreach (var w in words)
            {
                ushort offset = (ushort)Math.Max(0, (w.Time - start).TotalMilliseconds);
                byte[] wb = GetEncodedBytes(w.Word);
                p.AddRange(BitConverter.GetBytes(offset));
                p.Add((byte)wb.Length); p.AddRange(wb);
            }
            return BuildPacket(0x14, p.ToArray());
        }
        public void SendRaw(byte[] d) { if (_serialPort.IsOpen) _serialPort.Write(d, 0, d.Length); }
    }
}