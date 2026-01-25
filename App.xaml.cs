using System;
using System.Windows;

namespace MediaMonitor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 捕获所有线程的未处理异常
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show($"致命错误: {args.ExceptionObject}", "程序崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // 捕获 UI 线程的未处理异常
            this.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"UI 线程错误: {args.Exception.Message}", "同步异常", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true; // 尝试让程序继续运行
            };
        }
    }
}