using MediaMonitor.Core;
using MediaMonitor.Services;
using System;
using System.Windows;
using System.Windows.Threading;

namespace MediaMonitor
{
    public partial class MainWindow
    {
        private DispatcherTimer? _uiTimer;

        /// <summary>封面变化时是否自动弹窗（默认关闭，使用按钮按需查看）。</summary>
        private static readonly bool ShowArtworkPreviewOnUpdate = false;

        private void StartUiTimer()
        {
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += UIUpdate_Tick;
            _uiTimer.Start();
        }

        private static string FormatTime(TimeSpan ts)
            => ts.TotalHours >= 1
                ? ts.ToString(@"hh\:mm\:ss")
                : ts.ToString(@"mm\:ss");

        private void UIUpdate_Tick(object? sender, EventArgs e)
        {
            if (App.Smtc == null || App.Lyrics == null)
                return;

            // 更新播放信息
            TxtTitle.Text = App.Smtc.CurrentTitle ?? "未在播放";
            TxtArtist.Text = App.Smtc.CurrentArtist ?? "未知艺术家";
            TxtAlbum.Text = App.Smtc.CurrentAlbum ?? "未知唱片集";

            // 更新进度条
            var prog = App.Smtc.GetCurrentProgress();
            if (prog != null)
            {
                PbProgress.Maximum = prog.Duration.TotalSeconds;
                PbProgress.Value = prog.Position.TotalSeconds;
                //TxtTime.Text = $"{prog.Position:mm\\:ss} / {prog.Duration:mm\\:ss}";
                TxtTime.Text = $"{FormatTime(prog.Position)} / {FormatTime(prog.Duration)}";
            }
            else
            {
                // SMTC 丢失（播放器退出/未选中会话）：进度控件是被动刷新的，不主动复位就会停在最后一帧
                PbProgress.Maximum = 1;
                PbProgress.Value = 0;
                TxtTime.Text = "--:-- / --:--";
            }

            // 更新歌词状态：区分"真的加载到 lrc"与"未找到歌词的歌曲信息占位"
            int lrcCount = App.Lyrics.Lines?.Count ?? 0;
            string lrcPath = App.Lyrics.CurrentLyricPath ?? "";
            if (App.Lyrics.IsPlaceholder)
                TxtLrcStatus.Text = "未找到歌词（已用歌曲信息占位）";
            else
                TxtLrcStatus.Text = lrcCount > 0 ? $"已加载 {lrcPath}, {lrcCount} 行" : "未找到歌词";

            // 同步托盘提示：歌曲信息 + 主程序标题（含版本号）；歌曲信息与占位歌词共用一份拼接实现
            string song = App.Smtc.CurrentTitle ?? "未在播放";
            string artist = App.Smtc.CurrentArtist ?? "";
            _tray.UpdateTooltip($"{LyricService.ComposeSongInfo(song, artist)} | {this.Title}");

            // 新协议面板的只读状态（会话 / 对端能力 / 延迟）：100ms 一次，跨线程由这里统一消化
            UpdateNewStatusUI();

            // 会话列表对账（1s 一次）：由 SessionListController 统一处理
            _sessionList?.OnUiTick();
        }

        private void OnMasterLyricChanged(int index, LyricLine line)
        {
            // 因为 PackageMaster 通常在后台线程，更新 UI 必须回到主线程
            Dispatcher.Invoke(() =>
            {
                if (line == null || line.IsEmpty)
                {
                    TxtLyricDisplay.Text = "  ";
                    return;
                }

                // 1. 获取当前配置（用于判断是否显示翻译）
                var cfg = App.ConfigSvc?.Current;
                bool canShowTranslation = cfg != null && cfg.TransOccupies && !string.IsNullOrEmpty(line.Translation);

                // 2. 拼接显示文本
                // 逻辑：原文 + (如果有翻译且开启了占行则换行加翻译)
                string displayContent = line.Content;
                if (canShowTranslation)
                {
                    displayContent += "\n" + line.Translation;
                }

                // 3. 刷到界面预览框
                TxtLyricDisplay.Text = displayContent;

                // 4. (可选) 如果你想在 UI 上标记当前是第几行，可以顺便用 index 坐点什么
                // Debug.WriteLine($"Current Line Index: {index}");
            });
        }

        /// <summary>
        /// 手动显示封面预览（供新界面的「Show Artwork」按钮调用）：
        /// 取当前 SMTC 封面原始字节 → 压缩到 600×600 弹窗显示；无封面时会显示"本曲无封面"。
        /// </summary>
        public void ShowArtworkPreview() => ShowArtworkWindow(App.Smtc?.CurrentThumbnail);

        /// <summary>
        /// 封面变化回调：仅在 <c>ShowArtworkPreviewOnUpdate</c> 为 true 时自动弹窗
        /// （当前默认关闭，改用界面上按需触发 <see cref="ShowArtworkPreview"/>）。
        /// </summary>
        private void OnArtworkUpdated(byte[]? raw)
        {
            if (!ShowArtworkPreviewOnUpdate)
                return;

            ShowArtworkWindow(raw);
        }

        /// <summary>
        /// 弹窗显示封面 —— 自动回调与手动按钮共用的核心实现。
        /// 回调可能来自 SMTC 事件的线程池线程，建窗口必须回到 UI 线程；
        /// </summary>
        private void ShowArtworkWindow(byte[]? raw)
        {
            // 程序正在退出时 Dispatcher 可能已经关停，直接放弃
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.BeginInvoke(() =>
            {
                // 单实例：快速切歌时先关掉上一个，避免窗口堆积
                _artPreview?.Close();

                var win = new ArtworkPreviewWindow(raw) { Owner = this };
                // 用户手动点 X 关掉后把引用置空，否则下一次切歌会去 Close() 一个已关闭的窗口
                win.Closed += (_, __) =>
                {
                    if (ReferenceEquals(_artPreview, win))
                        _artPreview = null;
                };

                _artPreview = win;
                win.Show();   // 非模态：不阻塞后台帧循环，也不抢播放器焦点
            });
        }

        /// <summary>查看当前 SMTC 封面（与协议模式无关：两种模式下都能用）</summary>
        private void BtnShowArtwork_Click(object sender, RoutedEventArgs e) => ShowArtworkPreview();

    }
}
