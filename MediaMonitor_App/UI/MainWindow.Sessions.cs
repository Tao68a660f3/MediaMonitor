using MediaMonitor.UI;
using System.Windows;
using System.Windows.Controls;

namespace MediaMonitor
{
    public partial class MainWindow
    {
        private SessionListController? _sessionList;

        /// <summary>媒体源列表：只创建一次，后续由 SessionListController 原地 diff。</summary>
        private void InitializeSessionList()
        {
            if (App.Smtc == null)
                return;

            _sessionList = new SessionListController(App.Smtc, ComboSessions, Dispatcher);
            ChkHideClosedSessions.IsChecked = _sessionList.HideClosed;
            _sessionList.Refresh();

        }

        /// <summary>供 SMTC 事件 / 模式切换 / 配置加载调用的薄入口。</summary>
        private void RefreshSessionList() => _sessionList?.Refresh();

        private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => _sessionList?.OnSelectionChanged();

        private void ChkHideClosedSessions_Click(object sender, RoutedEventArgs e)
        {
            if (_isInternalChange)
                return;

            _sessionList?.SetHideClosed(ChkHideClosedSessions.IsChecked == true);
        }
    }
}
