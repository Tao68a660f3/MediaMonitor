using MediaMonitor.Core;
using MediaMonitor.Services;
using System;
using System.Collections.Generic;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaMonitor.UI
{
    /// <summary>
    /// 媒体源下拉框的完整状态管理。
    /// MainWindow 只负责把 ComboBox 和 SMTC 服务交给它，
    /// 列表 diff、稳定 key、用户/自动选择、已关闭会话过滤都在这里。
    /// </summary>
    internal sealed class SessionListController : IDisposable
    {
        private readonly SmtcService _smtc;
        private readonly ComboBox _combo;
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<SessionListEntry> _items = new();

        private bool _suppressSelection;
        private bool _autoSelecting;
        private string? _userSelectedKey;
        private string? _internalSelectedKey;
        private int _reconcileTicks;
        private int _refreshQueued;
        private bool _disposed;

        public SessionListController(SmtcService smtc, ComboBox combo, Dispatcher dispatcher)
        {
            _smtc = smtc ?? throw new ArgumentNullException(nameof(smtc));
            _combo = combo ?? throw new ArgumentNullException(nameof(combo));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            // ItemsSource 只绑定一次，之后原地 diff（重建会清空选择 -> SelectSession(null)）
            _combo.ItemsSource = _items;
            _smtc.SessionsListChanged += Refresh;
        }

        /// <summary>当前模式配置里的“隐藏已关闭会话”。</summary>
        public bool HideClosed => GetHideClosed();

        /// <summary>由 SMTC 事件 / 1s UI 对账 / 模式切换触发；线程安全。</summary>
        public void Refresh()
        {
            if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;

            if (_dispatcher.CheckAccess())
            {
                Rebuild();
                return;
            }

            if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
                return;                         // 已有一次排队，合并掉

            _dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_disposed &&
                    !_dispatcher.HasShutdownStarted &&
                    !_dispatcher.HasShutdownFinished)
                {
                    Rebuild();
                }
            }));
        }

        /// <summary>100ms UI 定时器调用：每 10 次做一次系统会话对账并刷新列表。</summary>
        public void OnUiTick()
        {
            if (_disposed)
                return;

            if (++_reconcileTicks < 10)
                return;

            _reconcileTicks = 0;
            _smtc.ReconcileSessions();
            Refresh();
        }

        /// <summary>用户操作下拉框（XAML SelectionChanged 的薄入口）。</summary>
        public void OnSelectionChanged()
        {
            if (_suppressSelection)
                return;

            var entry = _combo.SelectedItem as SessionListEntry;

            if (entry == null)
            {
                if (!_autoSelecting)
                {
                    _userSelectedKey = null;
                    _smtc.SelectSession(null);
                }
                return;
            }

            if (_autoSelecting)
            {
                _internalSelectedKey = entry.Key;
                Debug.WriteLine($"[媒体源] 自动选择: {entry.Key}");
            }
            else
            {
                _userSelectedKey = entry.Key;
                Debug.WriteLine($"[媒体源] 用户选择: {entry.Key}");
            }

            _smtc.SelectSession(entry.Session);
        }

        /// <summary>复选框：两份配置同时写同一个值，保证切模式后表现一致。</summary>
        public void SetHideClosed(bool value)
        {
            bool changed = false;

            if ((App.NewConfigSvc != null) && (App.NewConfigSvc.Current.HideClosedSessions != value))
            {
                App.NewConfigSvc.Current.HideClosedSessions = value;
                App.NewConfigSvc.Save();
                changed = true;
            }

            if ((App.ConfigSvc != null) && (App.ConfigSvc.Current.HideClosedSessions != value))
            {
                App.ConfigSvc.Current.HideClosedSessions = value;
                App.ConfigSvc.Save();
                changed = true;
            }

            if (changed)
                Refresh();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _smtc.SessionsListChanged -= Refresh;
        }

        private void Rebuild()
        {
            bool hideClosed = GetHideClosed();
            var current = _smtc.CurrentSession;
            var sessions = _smtc.GetSessions();

            var desired = new List<SessionListEntry>(sessions.Count);
            var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var session in sessions)
            {
                bool isCurrent = (current != null) && Equals(session, current);
                var status = _smtc.GetSessionPlaybackStatus(session);

                if (hideClosed && !isCurrent && (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed))
                    continue;

                string aumid = string.IsNullOrEmpty(session.SourceAppUserModelId)
                    ? "(未知来源)"
                    : session.SourceAppUserModelId;

                keyCounts.TryGetValue(aumid, out int count);
                keyCounts[aumid] = count + 1;

                // 稳定 key：AUMID（同 AUMID 多会话加 #2/#3）。
                // 播放器切歌重建会话时 AUMID 不变 -> key 不变 -> 下拉框选择不会被清掉。
                string key = (count == 0) ? aumid : $"{aumid}#{count + 1}";
                string label = (count == 0) ? aumid : $"{aumid} #{count + 1}";
                if (status is { } st)
                    label += $"（{StatusText(st)}）";

                desired.Add(new SessionListEntry(session, key, label));
            }

            var previousSelected = _combo.SelectedItem as SessionListEntry;
            _suppressSelection = true;
            try
            {
                // 删除已经不存在的 key
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (!desired.Any(d => string.Equals(d.Key, _items[i].Key, StringComparison.Ordinal)))
                        _items.RemoveAt(i);
                }

                // 按 desired 顺序插入/移动；同 key 只更新会话对象与文本，不替换 entry
                for (int i = 0; i < desired.Count; i++)
                {
                    int existing = IndexOfKey(desired[i].Key);
                    if (existing < 0)
                    {
                        _items.Insert(Math.Min(i, _items.Count), desired[i]);
                        continue;
                    }

                    if (existing != i)
                        _items.Move(existing, i);

                    _items[i].SetSession(desired[i].Session);
                    _items[i].Display = desired[i].Display;
                }

                // 选择优先级：用户选择 > 上次程序选择 > 当前选中项
                SessionListEntry? target = null;
                if (!string.IsNullOrEmpty(_userSelectedKey))
                    target = FindByKey(_userSelectedKey);
                if (target == null && !string.IsNullOrEmpty(_internalSelectedKey))
                    target = FindByKey(_internalSelectedKey);
                if (target == null && previousSelected != null)
                    target = FindByKey(previousSelected.Key);

                if (target != null)
                {
                    _combo.SelectedItem = target;
                    _internalSelectedKey = target.Key;

                    if ((previousSelected == null) ||
                        !string.Equals(previousSelected.Key, target.Key, StringComparison.Ordinal))
                    {
                        Debug.WriteLine($"[媒体源] 程序恢复选择: {target.Key}");
                    }
                }
            }
            finally
            {
                _suppressSelection = false;
            }

            // 原本没有选择才自动选第一个（记为内部选择，不覆盖用户选择）
            if ((_combo.SelectedItem == null) && (_items.Count > 0))
            {
                _autoSelecting = true;
                try
                {
                    _combo.SelectedIndex = 0;
                }
                finally
                {
                    _autoSelecting = false;
                }
            }

            // 会话对象可能被播放器重建：让 SmtcService 指向 UI 当前选中的新对象。
            if ((_combo.SelectedItem is SessionListEntry selected) &&
                !ReferenceEquals(_smtc.CurrentSession, selected.Session))
            {
                _internalSelectedKey = selected.Key;
                _smtc.SelectSession(selected.Session);
            }
        }

        private int IndexOfKey(string key)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Key, key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private SessionListEntry? FindByKey(string key)
        {
            foreach (var entry in _items)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                    return entry;
            }
            return null;
        }

        private static bool GetHideClosed()
        {
            if ((App.Mode == ProtocolMode.New) && (App.NewConfigSvc != null))
                return App.NewConfigSvc.Current.HideClosedSessions;
            if ((App.Mode == ProtocolMode.Legacy) && (App.ConfigSvc != null))
                return App.ConfigSvc.Current.HideClosedSessions;

            return App.NewConfigSvc?.Current?.HideClosedSessions
                ?? App.ConfigSvc?.Current?.HideClosedSessions
                ?? true;
        }

        private static string StatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
            => status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => "已关闭",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => "已打开",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "切换中",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "已停止",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "播放中",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "已暂停",
                _ => status.ToString()
            };
    }

    /// <summary>
    /// 媒体源下拉框条目：会话 + 显示文本（AUMID + 播放状态）。
    /// <see cref="Display"/> 是可变可通知属性：状态变化时原地刷新，不重建列表（保持选择不变）。
    /// </summary>
    public sealed class SessionListEntry : INotifyPropertyChanged
    {
        private string _display;

        public SessionListEntry(
            GlobalSystemMediaTransportControlsSession session,
            string key,
            string display)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Key = key;
            _display = display;
        }

        /// <summary>稳定标识（AUMID[#n]）：播放器切歌重建会话对象时保持不变。</summary>
        public string Key { get; }

        /// <summary>当前实际指向的 WinRT 会话（重建会话时原地替换）。</summary>
        public GlobalSystemMediaTransportControlsSession Session { get; private set; }

        /// <summary>播放器重建会话时更新指向，不改变列表项对象（保持选中不变）。</summary>
        public void SetSession(GlobalSystemMediaTransportControlsSession session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public override string ToString() => _display;

        public string Display
        {
            get => _display;
            set
            {
                if (string.Equals(_display, value, StringComparison.Ordinal))
                    return;

                _display = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
