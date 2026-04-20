using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MediaMonitor.Tools
{
    public class MediaKeyInvoker
    {
        // 使用最经典的 keybd_event，避开结构体对齐的 87 错误
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly Lazy<MediaKeyInvoker> _instance = new Lazy<MediaKeyInvoker>(() => new MediaKeyInvoker());
        public static MediaKeyInvoker Instance => _instance.Value;

        private readonly ConcurrentQueue<byte> _cmdQueue = new ConcurrentQueue<byte>();
        private bool _isProcessing = false;
        private readonly object _lock = new object();

        private MediaKeyInvoker()
        {
        }

        public void EnqueueCommand(byte cmd)
        {
            _cmdQueue.Enqueue(cmd);
            // 响铃证明逻辑触发了
            System.Media.SystemSounds.Beep.Play();
            _ = ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
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
                    // 针对 com0com 的内核特性，保留一个小延迟
                    await Task.Delay(20);

                    byte vk = GetVk(cmd);
                    if (vk != 0)
                    {
                        // 直接执行，不经过 Dispatcher 减少链路干扰
                        ExecuteKey(vk);
                        System.Diagnostics.Debug.WriteLine($"[Invoker] 已触发按键: {vk:X2}");
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

        private byte GetVk(byte cmd)
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

        private void ExecuteKey(byte vk)
        {
            // 模拟按下和抬起
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}