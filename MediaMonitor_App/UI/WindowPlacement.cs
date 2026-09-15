using MediaMonitor.Core;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace MediaMonitor
{
    /// <summary>
    /// 窗口布局的持久化（落盘到 config.json 的 <see cref="PackageConfig.WindowBounds"/>）。
    ///
    /// - <see cref="Capture"/>：窗口关闭 / 最小化到托盘时，把窗口的"还原边界"写进配置；
    /// - <see cref="Apply"/>：启动时读回，**先校验后应用** —— 位置不合理（显示器被拔掉、
    ///   分辨率变小、被手改到屏幕外）就直接忽略，窗口仍按系统默认摆放。
    ///
    /// 解析与判定都是纯函数（<see cref="TryParseBounds"/> / <see cref="IsBoundsUsable"/> / <see cref="ClampSize"/>），
    /// 不依赖窗口实例，便于单独验证。
    ///
    /// 坐标系：<c>Window.Left/Top/Width/Height</c> 与 <c>SystemParameters.VirtualScreen*</c> 都是 DIP，单位一致；
    /// VirtualScreen* 已覆盖全部显示器（副屏在左侧/上方时为负坐标）。
    /// </summary>
    internal static class WindowPlacement
    {
        /// <summary>尺寸下限（XAML 未设 MinWidth/MinHeight，这里给一个"仍然可用"的下限）</summary>
        internal const double MinUsableWidth = 400;
        internal const double MinUsableHeight = 300;

        /// <summary>必须留在屏幕内的最小可见区域：低于它就认为"用户拖不回来"</summary>
        internal const double MinVisibleWidth = 80;
        internal const double MinVisibleHeight = 40;

        /// <summary>坐标绝对值上限：拦掉手改出来的离谱值（同时避免后续运算溢出）</summary>
        internal const int MaxAbsCoordinate = 100000;

        /// <summary>
        /// 启动时应用上次保存的窗口布局。没有记录 / 格式非法 / 位置不合理 → 什么都不做（保持系统默认）。
        /// </summary>
        public static void Apply(Window window, PackageConfig? cfg)
        {
            if (!TryParseBounds(cfg?.WindowBounds, out Rect saved))
                return;

            Rect screen = GetVirtualScreen();
            if (!IsBoundsUsable(saved, screen))
            {
                Debug.WriteLine($"[界面] 上次保存的窗口位置 {Format(saved)} 不在当前屏幕 {Format(screen)} 范围内" +
                                "（显示器/分辨率有变化？），已忽略");
                return;
            }

            Rect target = ClampSize(saved, screen);

            window.WindowStartupLocation = WindowStartupLocation.Manual;   // 显式声明，避免将来 XAML 改成 CenterScreen 后失效
            window.Left = target.Left;
            window.Top = target.Top;
            window.Width = target.Width;
            window.Height = target.Height;

            Debug.WriteLine($"[界面] 已恢复上次窗口位置 {Format(target)}");
        }

        /// <summary>
        /// 关闭 / 隐藏到托盘时捕获窗口布局。用 <see cref="Window.RestoreBounds"/> —— 最大化/最小化时
        /// 它依然是"正常状态"下的还原矩形，不会把最大化尺寸或最小化时的 -32000 坐标存进去。
        /// </summary>
        public static void Capture(Window window, PackageConfig? cfg)
        {
            if (cfg == null)
                return;

            Rect bounds = window.RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Width) || bounds.Width <= 0 ||
                double.IsNaN(bounds.Height) || bounds.Height <= 0)
            {
                Debug.WriteLine($"[界面] 窗口还原边界无效 {Format(bounds)}，本次不保存窗口布局");
                return;
            }

            cfg.WindowBounds = Format(bounds);
            Debug.WriteLine($"[界面] 已记录窗口布局 {cfg.WindowBounds}");
        }

        // === 纯函数：解析 / 判定 / 收敛 ===

        /// <summary>解析 "left,top,width,height"：必须 4 段整数、宽高为正、坐标不超上限，否则返回 false</summary>
        internal static bool TryParseBounds(string? text, out Rect bounds)
        {
            bounds = Rect.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split(',');
            if (parts.Length != 4)
                return false;

            var v = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.AllowLeadingSign,
                                  CultureInfo.InvariantCulture, out v[i]))
                    return false;

                if (Math.Abs(v[i]) > MaxAbsCoordinate)
                    return false;
            }

            if (v[2] <= 0 || v[3] <= 0)
                return false;

            bounds = new Rect(v[0], v[1], v[2], v[3]);
            return true;
        }

        /// <summary>
        /// 位置是否合理：① 标题栏不能落在屏幕上方之外（否则拖不回来）；② 横向/纵向留在屏幕内的
        /// 可见区域不小于 <see cref="MinVisibleWidth"/> × <see cref="MinVisibleHeight"/>。
        /// </summary>
        internal static bool IsBoundsUsable(Rect bounds, Rect screen)
        {
            if (bounds.IsEmpty || double.IsNaN(bounds.Width) || bounds.Width <= 0 ||
                double.IsNaN(bounds.Height) || bounds.Height <= 0)
                return false;

            if (bounds.Top < screen.Top)
                return false;

            double visibleWidth = Math.Min(bounds.Right, screen.Right) - Math.Max(bounds.Left, screen.Left);
            double visibleHeight = Math.Min(bounds.Bottom, screen.Bottom) - Math.Max(bounds.Top, screen.Top);

            return visibleWidth >= MinVisibleWidth && visibleHeight >= MinVisibleHeight;
        }

        /// <summary>把尺寸收敛到 [下限, 当前虚拟屏幕尺寸]：只动宽高，不动位置（保留用户偏好）</summary>
        internal static Rect ClampSize(Rect bounds, Rect screen)
        {
            double width = Math.Clamp(bounds.Width, MinUsableWidth, Math.Max(MinUsableWidth, screen.Width));
            double height = Math.Clamp(bounds.Height, MinUsableHeight, Math.Max(MinUsableHeight, screen.Height));
            return new Rect(bounds.Left, bounds.Top, width, height);
        }

        /// <summary>当前虚拟桌面（含全部显示器）的边界，DIP 单位</summary>
        internal static Rect GetVirtualScreen()
            => new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        /// <summary>序列化为 "left,top,width,height"（整数、InvariantCulture，避免区域设置干扰）</summary>
        internal static string Format(Rect r)
            => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                             (int)Math.Round(r.Left), (int)Math.Round(r.Top),
                             (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    }
}