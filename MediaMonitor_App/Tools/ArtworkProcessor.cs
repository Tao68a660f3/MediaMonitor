using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaMonitor.Tools
{
    /// <summary>
    /// 压缩后的封面图。刻意不带任何协议语义（没有指令号、没有格式字节、没有分片信息）：
    /// 目的是让"抓取 → 压缩"与"怎么发给硬件"彻底解耦，后续新协议定稿后直接用这里的字节即可。
    /// </summary>
    public sealed record ArtworkImage(byte[] Data, int Width, int Height);

    /// <summary>
    /// SMTC 封面原始字节 → 指定分辨率的重编码。
    ///
    /// <para><b>尺寸策略</b>：等比"填满"目标框（<c>scale = max(w/srcW, h/srcH)</c>），再居中裁剪，
    /// 因此输出尺寸与目标<b>严格一致</b>（要 600×600 就给 600×600），且不会拉伸变形。
    /// 原图比目标小则本次为放大（是否提示由调用方决定）。</para>
    ///
    /// <para><b>依赖</b>：只用 WPF 自带的 <c>System.Windows.Media.Imaging</c>（PresentationCore），
    /// 不引入任何 NuGet 包；可在后台线程调用（产出的 <see cref="BitmapSource"/> 内部已 Freeze）。</para>
    /// </summary>
    public static class ArtworkProcessor
    {
        /// <summary>JPEG 默认质量（1~100）</summary>
        public const int DefaultQuality = 85;

        /// <summary>只解析图片头拿原始尺寸（不做像素解码）。图片损坏/格式不支持时返回 false。</summary>
        public static bool TryGetPixelSize(byte[]? raw, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (raw == null || raw.Length == 0)
                return false;

            try
            {
                using var ms = new MemoryStream(raw, writable: false);
                // DelayCreation：只读头部元数据，不解码像素（大图也不吃内存）
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[封面] 读取尺寸失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>缩放并重编码为 JPEG（有损，体积小，适合带宽受限的链路）。失败返回 null。</summary>
        public static ArtworkImage? ProcessToJpeg(byte[]? raw, int width, int height, int quality = DefaultQuality)
            => Encode(Prepare(raw, width, height),
                      () => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) });

        /// <summary>缩放并重编码为 PNG（无损，体积大；给将来的 1-bit / 调色板屏留路）。失败返回 null。</summary>
        public static ArtworkImage? ProcessToPng(byte[]? raw, int width, int height)
            => Encode(Prepare(raw, width, height), () => new PngBitmapEncoder());

        /// <summary>
        /// 缩放并转成**未压缩 RGB565**（规范 §11.1 的 `RAW_RGB565`：2 B/像素、低字节在前）。
        /// 输出长度为 `width * height * 2`，可直接 blit 到常见 SPI 屏 / LVGL 缓冲。
        /// </summary>
        public static ArtworkImage? ProcessToRgb565(byte[]? raw, int width, int height)
        {
            BitmapSource? src = Prepare(raw, width, height);
            if (src == null)
            {
                return null;
            }

            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr565, null, 0);
                conv.Freeze();

                int stride = conv.PixelWidth * 2;
                byte[] pixels = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(pixels, stride, 0);

                return new ArtworkImage(pixels, conv.PixelWidth, conv.PixelHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[封面] 转 RGB565 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>把位图编码成图片字节</summary>
        private static ArtworkImage? Encode(BitmapSource? source, Func<BitmapEncoder> encoderFactory)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                BitmapEncoder encoder = encoderFactory();
                encoder.Frames.Add(BitmapFrame.Create(source));

                using var outMs = new MemoryStream();
                encoder.Save(outMs);

                return new ArtworkImage(outMs.ToArray(), source.PixelWidth, source.PixelHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[封面] 重编码失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>解码 + 等比填满 + 居中裁剪，得到"严格 width×height"的冻结位图</summary>
        private static BitmapSource? Prepare(byte[]? raw, int width, int height)
        {
            if (raw == null || raw.Length == 0) return null;
            if (width <= 0 || height <= 0) return null;
            if (!TryGetPixelSize(raw, out int srcW, out int srcH)) return null;

            try
            {
                // 1) 等比填满：两边都 >= 目标，多出来的部分下面居中裁掉。
                //    解码阶段直接把 DecodePixel* 设成"缩放后的尺寸"（两个维度同时给，宽高比与源一致），
                //    这样只做一次 WIC 缩放，不做二次采样。
                double scale = Math.Max(width / (double)srcW, height / (double)srcH);
                int scaledW = Math.Max(width, (int)Math.Round(srcW * scale));
                int scaledH = Math.Max(height, (int)Math.Round(srcH * scale));

                var decoded = new BitmapImage();
                using (var src = new MemoryStream(raw, writable: false))
                {
                    decoded.BeginInit();
                    decoded.CacheOption = BitmapCacheOption.OnLoad;   // 立刻解码，src 可随即释放
                    decoded.DecodePixelWidth = scaledW;
                    decoded.DecodePixelHeight = scaledH;
                    decoded.StreamSource = src;
                    decoded.EndInit();
                }
                decoded.Freeze();

                // 2) 居中裁剪到严格目标尺寸（缩放取整可能差 1px，做边界钳位，避免越界）
                if (decoded.PixelWidth == width && decoded.PixelHeight == height)
                {
                    return decoded;
                }

                int x = Math.Max(0, (decoded.PixelWidth - width) / 2);
                int y = Math.Max(0, (decoded.PixelHeight - height) / 2);
                int cw = Math.Min(width, decoded.PixelWidth - x);
                int ch = Math.Min(height, decoded.PixelHeight - y);

                var cropped = new CroppedBitmap(decoded, new Int32Rect(x, y, cw, ch));
                cropped.Freeze();
                return cropped;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[封面] 解码/缩放失败: {ex.Message}");
                return null;
            }
        }
    }
}
