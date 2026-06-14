using System;
using System.Drawing;      // 需要用到图标
using System.Windows.Forms; // 这里随便引用，不会影响主项目
using System.Diagnostics;

namespace MediaMonitor.Tray
{
    public class TrayManager
    {
        private NotifyIcon _icon;

        public void Init(Action onShow, Action onExit)
        {
            _icon = new NotifyIcon();

            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            _icon.Icon = Icon.ExtractAssociatedIcon(exePath);

            _icon.Visible = true;
            _icon.Text = "MediaMonitor";

            // 双击托盘图标
            _icon.MouseDoubleClick += (s, e) => onShow?.Invoke();

            // 右键菜单
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示界面", null, (s, e) => onShow?.Invoke());
            menu.Items.Add("-"); // 分隔线
            menu.Items.Add("退出程序", null, (s, e) => onExit?.Invoke());
            _icon.ContextMenuStrip = menu;
        }

        /// <summary>
        /// 更新托盘图标的提示文字（显示在鼠标悬停时）
        /// </summary>
        public void UpdateTooltip(string text)
        {
            if (_icon != null)
            {
                // NotifyIcon.Text 最大 127 个字符，超出会截断
                _icon.Text = text.Length > 127 ? text[..127] : text;
            }
        }

        public void Dispose()
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
        }
    }
}