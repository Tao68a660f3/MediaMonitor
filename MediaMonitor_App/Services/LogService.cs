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
            // 即使 App.Master 或 Config 为空，也会安全返回 false，不会炸掉
            bool AdvMode = App.Master?.Config?.IsAdvancedMode ?? false;

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

                if (AdvMode)
                {
                    // 2. 分色解析逻辑 (原样搬迁)
                    if (cmd == 0x10)
                    {
                        tag.Text = " [元数据] ";
                        tag.Background = Brushes.DarkBlue;
                        detail = DecodeMeta(data, enc);
                    }
                    else if (cmd == 0x15)
                    {
                        tag.Text = " [增强原文行] ";
                        tag.Background = Brushes.DarkGreen;
                        detail = DecodeEnhanced(data, enc);
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

                    if (string.IsNullOrEmpty(detail))
                    {
                        // 如果上面所有的 if (cmd == 0xXX) 都没有匹配成功
                        try
                        {
                            // 直接尝试将整个包按当前编码转为字符串
                            detail = "[非协议编码数据]";
                            tag.Text = " [?] ";
                            tag.Background = Brushes.Gray;
                        }
                        catch
                        {
                            detail = "[非法编码数据]";
                        }
                    }
                }
                else
                {
                    try
                    {
                        // 直接尝试将整个包按当前编码转为字符串
                        detail = enc.GetString(data);
                        tag.Text = " [纯文本] ";
                        tag.Background = Brushes.Gray;
                    }
                    catch
                    {
                        detail = "[非法编码数据]";
                    }
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
                int ptr = 4; // AA Cmd LenH LenL
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
            try
            {
                if (data.Length < 11)
                    return "数据长度不足";
                short idx = BitConverter.ToInt16(data, 4);      // 4: Index(2B)
                uint time = BitConverter.ToUInt32(data, 6);     // 6: StartTime(4B)
                string txt = enc.GetString(data, 10, data.Length - 11); // 10: 文本
                return $"({idx:D3}) [{time}ms] {txt}";
            }
            catch { return "解析失败"; }
        }

        private string DecodeEnhanced(byte[] data, Encoding enc)
        {
            try
            {
                if (data.Length < 15)
                    return "数据长度不足";
                short idx = BitConverter.ToInt16(data, 4);      // 4: Index(2B)
                uint start = BitConverter.ToUInt32(data, 6);    // 6: StartTime(4B)
                uint end = BitConverter.ToUInt32(data, 10);     // 10: EndTime(4B)
                string txt = enc.GetString(data, 14, data.Length - 15); // 14: 文本
                return $"({idx:D3}) [{start}~{end}ms] {txt}";
            }
            catch { return "解析失败"; }
        }

        private string DecodeWordByWord(byte[] data, Encoding enc)
        {
            try
            {
                if (data.Length < 11)
                    return "数据长度不足";
                short idx = BitConverter.ToInt16(data, 4);       // 4: Index(2B)
                uint time = BitConverter.ToUInt32(data, 6);      // 6: StartTime(4B)
                StringBuilder sb = new StringBuilder($"({idx:D3}) [{time}ms] ");
                int wordCount = data[10];                        // 10: WordCount(1B)
                int ptr = 11;                                    // 11: 第一个词的 Offset(2B)，跳过 WordCount
                for (int i = 0; i < wordCount; i++)
                {
                    // 边界保护：至少需要 Offset(2B) + Len(1B)
                    if (ptr + 3 > data.Length)
                    {
                        sb.Append("...[截断]");
                        break;
                    }
                    ushort off = BitConverter.ToUInt16(data, ptr);
                    byte len = data[ptr + 2];
                    // 边界保护：文本长度不能越过帧尾
                    if (ptr + 3 + len > data.Length)
                    {
                        sb.Append("...[文本截断]");
                        break;
                    }
                    sb.Append($"<{off}ms>{enc.GetString(data, ptr + 3, len)}");
                    ptr += (3 + len);
                }
                return sb.ToString();
            }
            catch { return "解析失败"; }
        }

        private string DecodeTimeSync(byte[] data)
        {
            if (data.Length < 11)
                return "数据长度不足";
            try
            {
                int yy = data[4], mm = data[5], dd = data[6], h = data[7], m = data[8], s = data[9], w = data[10];
                string[] weeks = { "日", "一", "二", "三", "四", "五", "六", "日" };
                string wStr = (w >= 1 && w <= 7) ? weeks[w] : w.ToString();
                return $"20{yy:D2}-{mm:D2}-{dd:D2} {h:D2}:{m:D2}:{s:D2} (周{wStr})";
            }
            catch { return "解析失败"; }
        }

        #endregion
    }
}