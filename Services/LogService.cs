using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaMonitor.Services
{
    public class LogService
    {
        private readonly RichTextBox _outputBox;
        private const int MAX_BLOCK_COUNT = 100;

        public LogService(RichTextBox outputBox)
        {
            _outputBox = outputBox;
        }

        /// <summary>
        /// 原样搬迁：带分色的高级协议日志记录
        /// </summary>
        public void LogProtocol(byte[] data, Encoding enc)
        {
            if (data == null || data.Length < 2)
                return;
            if (data[1] == 0x11)
                return; // 原样保留：忽略进度同步包日志

            _outputBox.Dispatcher.Invoke(() =>
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };

                // 1. 十六进制预览部分 (灰色)
                string hex = BitConverter.ToString(data).Replace("-", " ");
                p.Inlines.Add(new Run($"{hex}\n") { Foreground = Brushes.DimGray, FontSize = 10 });

                byte cmd = data[1];
                Run tag = new Run { Foreground = Brushes.White };
                string detail = "";

                // 2. 分色解析逻辑 (原样搬迁)
                if (cmd == 0x10)
                {
                    tag.Text = " [元数据] ";
                    tag.Background = Brushes.DarkBlue;
                    detail = DecodeMeta(data, enc);
                }
                else if (cmd == 0x12 || cmd == 0x13)
                {
                    tag.Text = cmd == 0x12 ? " [普通行] " : " [翻译行] ";
                    tag.Background = cmd == 0x12 ? Brushes.DarkGreen : Brushes.DarkSlateBlue;
                    detail = DecodeStandard(data, enc);
                }
                else if (cmd == 0x14)
                {
                    tag.Text = " [逐字行] ";
                    tag.Background = Brushes.DarkRed;
                    detail = DecodeWordByWord(data, enc);
                }
                else if (cmd == 0x20)
                {
                    tag.Text = " [时间同步] ";
                    tag.Background = Brushes.Teal;
                    detail = DecodeTimeSync(data);
                }

                p.Inlines.Add(tag);
                p.Inlines.Add(new Run(" " + detail) { Foreground = Brushes.White });
                AppendBlock(p);
            });
        }

        /// <summary>
        /// 原样搬迁：普通文本日志记录
        /// </summary>
        public void LogInfo(string msg, Brush color)
        {
            _outputBox.Dispatcher.Invoke(() =>
            {
                var p = new Paragraph(new Run(msg) { Foreground = color });
                AppendBlock(p);
            });
        }

        /// <summary>
        /// 核心缓冲区管理：自动清理超过100行的记录
        /// </summary>
        private void AppendBlock(Block block)
        {
            if (_outputBox.Document.Blocks.Count > MAX_BLOCK_COUNT)
            {
                _outputBox.Document.Blocks.Clear();
                _outputBox.Document.Blocks.Add(new Paragraph(new Run("--- 缓冲区已自动清空 ---") { Foreground = Brushes.Gray }));
            }

            _outputBox.Document.Blocks.Add(block);
            _outputBox.ScrollToEnd();
        }

        #region 数据解析子功能 (原样搬迁自 MainWindow)

        private string DecodeMeta(byte[] data, Encoding enc)
        {
            try
            {
                int ptr = 3;
                List<string> res = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    int len = data[ptr];
                    res.Add(enc.GetString(data, ptr + 1, len));
                    ptr += (1 + len);
                }
                return string.Join(" | ", res);
            }
            catch { return "解析失败"; }
        }

        private string DecodeStandard(byte[] data, Encoding enc)
        {
            short idx = BitConverter.ToInt16(data, 3);
            uint time = BitConverter.ToUInt32(data, 5);
            string txt = enc.GetString(data, 9, data.Length - 10);
            return $"({idx:D3}) [{time}ms] {txt}";
        }

        private string DecodeWordByWord(byte[] data, Encoding enc)
        {
            short idx = BitConverter.ToInt16(data, 3);
            uint time = BitConverter.ToUInt32(data, 5);
            StringBuilder sb = new StringBuilder($"({idx:D3}) [{time}ms] ");
            int ptr = 10;
            for (int i = 0; i < data[9]; i++)
            {
                ushort off = BitConverter.ToUInt16(data, ptr);
                byte len = data[ptr + 2];
                sb.Append($"<{off}ms>{enc.GetString(data, ptr + 3, len)}");
                ptr += (3 + len);
            }
            return sb.ToString();
        }

        private string DecodeTimeSync(byte[] data)
        {
            if (data.Length < 10)
                return "数据长度不足";
            try
            {
                int yy = data[3], mm = data[4], dd = data[5], h = data[6], m = data[7], s = data[8], w = data[9];
                string[] weeks = { "日", "一", "二", "三", "四", "五", "六", "日" };
                string wStr = (w >= 1 && w <= 7) ? weeks[w] : w.ToString();
                return $"20{yy:D2}-{mm:D2}-{dd:D2} {h:D2}:{m:D2}:{s:D2} (周{wStr})";
            }
            catch { return "解析失败"; }
        }

        #endregion
    }
}