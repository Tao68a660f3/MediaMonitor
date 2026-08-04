using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MediaMonitor.Services;

namespace MediaMonitor.Tools
{
    public class MediaKeyInvoker
    {
        // 建议不要使用 SuperCom 串口调试软件来调试本程序。

        // ========== Deprecated 备用路径：keybd_event 键盘注入 ==========
        // 该路径依赖前台窗口焦点与权限（UIPI），在管理员窗口/全屏应用下可能失效。
        // 现仅用于 SMTC 没有现成 API 的指令：Mute(0xA4)、快进(0xA5)、快退(0xA6)。
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
            //响铃证明逻辑触发了
            //System.Media.SystemSounds.Beep.Play();
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
                    // 针对串口调试软件可能干扰时序的特性，保留一个小延迟
                    await Task.Delay(20);

                    switch (cmd)
                    {
                        // ===== 主用路径：SMTC 直控（不依赖前台窗口/权限，最可靠） =====
                        case 0xA1:
                            _ = App.Smtc?.NextAsync();
                            System.Diagnostics.Debug.WriteLine("[Invoker] 已触发 SMTC 下一曲");
                            break;
                        case 0xA2:
                            _ = App.Smtc?.PrevAsync();
                            System.Diagnostics.Debug.WriteLine("[Invoker] 已触发 SMTC 上一曲");
                            break;
                        case 0xA3:
                            _ = App.Smtc?.PlayPauseAsync();
                            System.Diagnostics.Debug.WriteLine("[Invoker] 已触发 SMTC 播放/暂停");
                            break;

                        // ===== 备用路径：keybd_event 键盘注入（SMTC 无现成 API 的指令） =====
                        default:
                            byte vk = GetVk(cmd);
                            if (vk != 0)
                            {
                                // 直接执行，不经过 Dispatcher 减少链路干扰
                                ExecuteKey(vk);
                                System.Diagnostics.Debug.WriteLine($"[Invoker] 已触发按键: {vk:X2}");
                            }
                            break;
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