using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MediaMonitor.Tools
{
    public class MediaKeyInvoker
    {
        // --- Win32 API 定义保持不变 ---
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type; public KEYBDINPUT ki;
        }

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // --- 单例定义 ---
        private static readonly Lazy<MediaKeyInvoker> _instance = new Lazy<MediaKeyInvoker>(() => new MediaKeyInvoker());
        public static MediaKeyInvoker Instance => _instance.Value;

        private readonly ConcurrentQueue<byte> _cmdQueue = new ConcurrentQueue<byte>();
        private bool _isProcessing = false;
        private readonly object _lock = new object();

        private MediaKeyInvoker()
        {
        }

        /// <summary>
        /// 生产者：将指令放入队列并触发处理
        /// </summary>
        public void EnqueueCommand(byte cmd)
        {
            _cmdQueue.Enqueue(cmd);
            System.Media.SystemSounds.Beep.Play(); // 响铃证明方法被调用了

            // 触发处理逻辑
            _ = ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
            // 确保只有一个处理流程在运行
            lock (_lock)
            {
                if (_isProcessing)
                    return;
                _isProcessing = true;
            }

            try
            {
                while (_cmdQueue.TryDequeue(out byte cmd))
                {
                    // 给系统极短的缓冲，避免 com0com 内核锁死
                    await Task.Delay(10);

                    ushort vk = GetVk(cmd);
                    if (vk != 0)
                    {
                        // 强制切换到 UI 线程执行 SendInput，提高成功率
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            SendKey(vk);
                            System.Diagnostics.Debug.WriteLine($"[Invoker] 执行指令: {cmd:X2}");
                        });
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isProcessing = false;
                }
            }
        }

        private ushort GetVk(byte cmd)
        {
            return cmd switch
            {
                0xA1 => 0xB0, // Next
                0xA2 => 0xB1, // Prev
                0xA3 => 0xB3, // Play/Pause
                0xA4 => 0xAD, // Mute
                0xA5 => 0x27, // Right
                0xA6 => 0x25, // Left
                _ => 0
            };
        }

        private void SendKey(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[0].ki.dwFlags = KEYEVENTF_EXTENDEDKEY;

            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].ki.wVk = vk;
            inputs[1].ki.dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP;

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}