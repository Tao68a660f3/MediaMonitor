using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MediaMonitor.Tools;

namespace MediaMonitor
{
    /// <summary>
    /// 封面验证窗口（临时调试用）：只放一张"压缩到 600×600"的图，
    /// 用于确认 ① SMTC 能抓到封面 ② ArtworkProcessor 的调整尺寸功能可用。
    ///
    /// <para>本窗口只读、不落盘、不下发任何数据。联调完成后可整体删除本文件，
    /// 或把 <c>MainWindow.ShowArtworkPreviewOnUpdate</c> 置 false 关掉入口。</para>
    /// </summary>
    public partial class ArtworkPreviewWindow : Window
    {
        /// <summary>验证用的目标分辨率（边长）</summary>
        private const int PreviewSize = 600;

        /// <param name="raw">SMTC 封面原始字节；null/空表示本曲没有封面。</param>
        public ArtworkPreviewWindow(byte[]? raw)
        {
            InitializeComponent();
            ShowArtwork(raw);
        }

        private void ShowArtwork(byte[]? raw)
        {
            if (raw == null || raw.Length == 0)
            {
                TxtArtworkInfo.Text = "本曲无封面（SMTC 未提供缩略图，raw = null）";
                return;
            }

            // 抓取成功与否，先用原图尺寸打底（压缩失败时也要能看出抓到了多少字节）
            bool hasSrcSize = ArtworkProcessor.TryGetPixelSize(raw, out int srcW, out int srcH);
            string srcSize = hasSrcSize ? $"{srcW}×{srcH}" : "未知";

            var processed = ArtworkProcessor.ProcessToJpeg(raw, PreviewSize, PreviewSize);
            if (processed == null)
            {
                TxtArtworkInfo.Text = $"封面已抓到 {raw.Length / 1024.0:F1} KB / {srcSize}，但解码或缩放失败（原因见 VS 输出窗口）";
                return;
            }

            ImgPreview.Source = CreateBitmap(processed.Data);

            string upscaleNote = hasSrcSize && (srcW < PreviewSize || srcH < PreviewSize)
                ? "（原图小于目标，本次为放大）" : "";
            double ratio = processed.Data.Length * 100.0 / raw.Length;

            TxtArtworkInfo.Text =
                $"原始  : {srcSize} / {raw.Length / 1024.0:F1} KB\n" +
                $"压缩后: {processed.Width}×{processed.Height} JPEG q{ArtworkProcessor.DefaultQuality} / " +
                $"{processed.Data.Length / 1024.0:F1} KB ({ratio:F0}%) {upscaleNote}";
        }

        /// <summary>把内存里的图片字节变成可显示的位图（OnLoad 立即解码 → 可安全释放流）</summary>
        private static BitmapImage CreateBitmap(byte[] data)
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(data, writable: false))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
    }
}
