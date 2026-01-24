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
            _serialPort.DataBits = 8;
            _serialPort.StopBits = StopBits.One;
            _serialPort.Parity = Parity.None;
            _serialPort.Open();
        }

        public void Disconnect() => _serialPort.Close();

        private byte[] GetEncodedBytes(string text)
        {
            Encoding encoding = SelectedEncoding == EncodingType.UTF8 ? Encoding.UTF8 : Encoding.GetEncoding("GB2312");
            return encoding.GetBytes(text);
        }

        public byte[] BuildPacket(byte type, byte[] payload)
        {
            int len = payload.Length;
            byte[] packet = new byte[len + 4];
            packet[0] = 0xAA;
            packet[1] = type;
            packet[2] = (byte)len;
            Array.Copy(payload, 0, packet, 3, len);

            int checksum = 0;
            for (int i = 0; i < packet.Length - 1; i++) checksum += packet[i];
            packet[packet.Length - 1] = (byte)(checksum & 0xFF);
            return packet;
        }

        public byte[] BuildMetadata(string title, string artist)
        {
            var tBytes = GetEncodedBytes(title);
            var aBytes = GetEncodedBytes(artist);
            var payload = tBytes.Concat(new byte[] { 0x00 }).Concat(aBytes).ToArray();
            return BuildPacket(0x10, payload);
        }

        public byte[] BuildProgress(bool isPlaying, uint currentMs, uint totalMs)
        {
            byte[] payload = new byte[9];
            payload[0] = (byte)(isPlaying ? 0x01 : 0x00);
            Array.Copy(BitConverter.GetBytes(currentMs), 0, payload, 1, 4);
            Array.Copy(BitConverter.GetBytes(totalMs), 0, payload, 5, 4);
            return BuildPacket(0x11, payload);
        }

        public byte[] BuildLyricWithIndex(byte type, ushort index, string text)
        {
            var textBytes = GetEncodedBytes(text);
            var payload = new byte[2 + textBytes.Length];
            Array.Copy(BitConverter.GetBytes(index), 0, payload, 0, 2);
            Array.Copy(textBytes, 0, payload, 2, textBytes.Length);
            return BuildPacket(type, payload);
        }

        public byte[] BuildWordByWord(ushort index, List<WordInfo> words, TimeSpan lineStartTime)
        {
            List<byte> payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(index));
            payload.Add((byte)words.Count);
            foreach (var w in words)
            {
                ushort offset = (ushort)(w.Time - lineStartTime).TotalMilliseconds;
                var wordBytes = GetEncodedBytes(w.Word);
                payload.AddRange(BitConverter.GetBytes(offset));
                payload.Add((byte)wordBytes.Length);
                payload.AddRange(wordBytes);
            }
            return BuildPacket(0x14, payload.ToArray());
        }

        public void SendRaw(byte[] data)
        {
            if (_serialPort.IsOpen) _serialPort.Write(data, 0, data.Length);
        }
    }
}