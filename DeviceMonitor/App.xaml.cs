// 初始化日志、数据库和登录窗口，记录程序级异常。
using DeviceMonitor.Core.Persistence;
using System.Windows;
using DeviceMonitor.Views;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using DeviceMonitor.Core;

namespace DeviceMonitor
{
    public partial class App : Application
    {
        /// <summary>初始化基础服务并显示登录窗口。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 先准备日志目录，后续初始化失败也能留下原因。
                AppLog.Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeviceMonitor", "Logs"));

                AppLog.Info("程序启动");

                DispatcherUnhandledException += RecordDispatcherError;
                AppDomain.CurrentDomain.UnhandledException += RecordDomainError;
                TaskScheduler.UnobservedTaskException += RecordTaskError;

                DatabaseInitializer initializer = new DatabaseInitializer();

                // 启动时应用待执行迁移，不重复建表或清空已有数据。
                initializer.Initialize();

                // 复用存储入口；各次数据库操作仍创建独立 DbContext。
                UserStore userStore = new UserStore(initializer.DatabasePath);

                DeviceStore deviceStore = new DeviceStore(initializer.DatabasePath);

                HistoryStore historyStore = new HistoryStore(initializer.DatabasePath);

                LoginWindow loginWindow = new LoginWindow(userStore, deviceStore, historyStore);

                this.MainWindow = loginWindow;

                loginWindow.Show();

                AppLog.Info("登录窗口已就绪");
            }
            catch (Exception ex)
            {
                AppLog.Error("程序启动失败", exception: ex);

                MessageBox.Show(
                    $"程序启动失败：{ex.Message}",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }

        /// <summary>界面线程出现未处理异常时，记录日志；不吞掉异常。</summary>
        private void RecordDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            AppLog.Error("界面线程未处理异常", exception: e.Exception);

        /// <summary>程序出现未处理异常时，记录日志。</summary>
        private void RecordDomainError(object sender, UnhandledExceptionEventArgs e) =>
            AppLog.Error("程序未处理异常", exception: e.ExceptionObject as Exception);

        /// <summary>后台 Task 的异常没有被调用方处理时，收到通知并记日志。</summary>
        private void RecordTaskError(object? sender, UnobservedTaskExceptionEventArgs e) =>
            AppLog.Error("后台任务未观察异常", exception: e.Exception);

        /// <summary>记录退出状态并解除全局异常订阅。</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Info("程序退出", $"ExitCode={e.ApplicationExitCode}");
            DispatcherUnhandledException -= RecordDispatcherError;
            AppDomain.CurrentDomain.UnhandledException -= RecordDomainError;
            TaskScheduler.UnobservedTaskException -= RecordTaskError;
            base.OnExit(e);
        }
    }
}
