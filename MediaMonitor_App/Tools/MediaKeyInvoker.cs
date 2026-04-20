using System;
using System.Runtime.InteropServices;

namespace MediaMonitor.Tools
{
    /// <summary>
    /// 媒体按键模拟工具类：通过 Win32 API 模拟系统全局多媒体按键动作
    /// </summary>
    public static class MediaKeyInvoker
    {
        // 导入 Windows 系统 API 用于模拟键盘事件
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        // 键盘事件常量定义
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // 虚拟键码 (Virtual-Key Codes) 定义
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0; // 下一曲
        private const byte VK_MEDIA_PREV_TRACK = 0xB1; // 上一曲
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3; // 播放/暂停
        private const byte VK_LEFT = 0x25;              // 左方向键 (用于快退)
        private const byte VK_RIGHT = 0x27;             // 右方向键 (用于快进)

        /// <summary>
        /// 根据单片机上传的指令字执行对应的多媒体操作
        /// </summary>
        /// <param name="cmd">上行协议中的 Cmd 字节</param>
        public static void Execute(byte cmd)
        {
            switch (cmd)
            {
                case 0xA1: // 下一曲
                    SimulateKeyPress(VK_MEDIA_NEXT_TRACK);
                    break;

                case 0xA2: // 上一曲
                    SimulateKeyPress(VK_MEDIA_PREV_TRACK);
                    break;

                case 0xA3: // 播放/暂停
                    SimulateKeyPress(VK_MEDIA_PLAY_PAUSE);
                    break;

                case 0xA5: // 快进 (+5s)：模拟按下右方向键
                    SimulateKeyPress(VK_RIGHT);
                    break;

                case 0xA6: // 快退 (-5s)：模拟按下左方向键
                    SimulateKeyPress(VK_LEFT);
                    break;
            }
        }

        /// <summary>
        /// 模拟一次完整的按键动作（按下 + 弹起）
        /// </summary>
        private static void SimulateKeyPress(byte keyCode)
        {
            // 按下按键
            keybd_event(keyCode, 0, 0, 0);
            // 释放按键
            keybd_event(keyCode, 0, KEYEVENTF_KEYUP, 0);
        }
    }
}