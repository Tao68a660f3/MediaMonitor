using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaMonitor.Tools
{
    public class MediaKeyInvoker : IDisposable
    {
        // --- Win32 API 结构体定义 ---
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
        }

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // --- 队列与线程控制 ---
        private static readonly Lazy<MediaKeyInvoker> _instance = new Lazy<MediaKeyInvoker>(() => new MediaKeyInvoker());
        public static MediaKeyInvoker Instance => _instance.Value;

        private readonly ConcurrentQueue<byte> _cmdQueue = new ConcurrentQueue<byte>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed = false;

        private MediaKeyInvoker()
        {
            // 核心：启动一个完全独立的 LongRunning 线程，脱离串口事件上下文
            Task.Factory.StartNew(
                async () => await ProcessQueueAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        /// <summary>
        /// 生产者：将指令放入队列
        /// </summary>
        public void EnqueueCommand(byte cmd)
        {
            _cmdQueue.Enqueue(cmd);
            // 响铃表示指令已成功进入独立线程队列
            System.Media.SystemSounds.Beep.Play();
        }

        /// <summary>
        /// 消费者：在独立线程中循环监听并执行
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_cmdQueue.TryDequeue(out byte cmd))
                {
                    // 关键：物理延迟，确保串口 I/O 状态已释放
                    await Task.Delay(30, token);

                    ushort vk = 0;
                    switch (cmd)
                    {
                        case 0xA1:
                            vk = 0xB0;
                            break; // Media Next
                        case 0xA2:
                            vk = 0xB1;
                            break; // Media Prev
                        case 0xA3:
                            vk = 0xB3;
                            break; // Media Play/Pause
                        case 0xA4:
                            vk = 0xAD;
                            break; // Volume Mute
                        case 0xA5:
                            vk = 0x27;
                            break; // Right Arrow (Fast Forward)
                        case 0xA6:
                            vk = 0x25;
                            break; // Left Arrow (Rewind)
                    }

                    if (vk != 0)
                    {
                        SendKey(vk);
                        System.Diagnostics.Debug.WriteLine($"[Invoker] 独立线程已执行指令: {cmd:X2} -> VK: {vk:X2}");
                    }
                }
                else
                {
                    // 无指令时进入低功耗休眠
                    await Task.Delay(30, token);
                }
            }
        }

        private void SendKey(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];

            // 构造 Key Down
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[0].ki.dwFlags = KEYEVENTF_EXTENDEDKEY;

            // 构造 Key Up
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].ki.wVk = vk;
            inputs[1].ki.dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP;

            uint result = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));

            //if (result == 0)
            //{
            //    int error = Marshal.GetLastError();
            //    System.Diagnostics.Debug.WriteLine($"[SendInput] 失败! Win32错误码: {error}");
            //}
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                _cts.Dispose();
                _disposed = true;
            }
        }
    }
}