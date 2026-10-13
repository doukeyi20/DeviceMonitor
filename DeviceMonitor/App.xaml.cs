using DeviceMonitor.Core.Persistence;
using System.Windows;
using DeviceMonitor.Views;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using DeviceMonitor.Core.Diagnostics;

namespace DeviceMonitor
{

    /// <summary>
    /// 程序入口：准备日志和数据库，再创建并显示登录窗口。
    /// </summary>
    public partial class App : Application
    {

        /// <summary>
        /// 程序启动时，由 WPF 自动调用。
        /// WPF 启动时自动调用；完成初始化后显示登录页，不自动采集。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 指定日志目录，AppLog 内部创建真正写文件的 FileLog 对象。
                AppLog.Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeviceMonitor", "Logs"));

                // 写入一条“程序启动”日志。
                AppLog.Info("程序启动");

                // 未处理的界面、程序和后台任务异常，都尽量留下日志。
                DispatcherUnhandledException += RecordDispatcherError;
                AppDomain.CurrentDomain.UnhandledException += RecordDomainError;
                TaskScheduler.UnobservedTaskException += RecordTaskError;

                // 创建初始化对象，确定数据库路径。
                DatabaseInitializer initializer = new DatabaseInitializer();

                // 准备数据库和表
                initializer.Initialize();

                // 创建数据库操作对象，后面沿用这些对象。
                UserStore userStore = new UserStore(initializer.DatabasePath);

                DeviceStore deviceStore = new DeviceStore(initializer.DatabasePath);

                // 历史与配置共用一个数据库，这个对象后面会传给采集任务。
                HistoryStore historyStore = new HistoryStore(initializer.DatabasePath);

                // 创建登录窗口，把数据库操作对象交给它。
                LoginWindow loginWindow = new LoginWindow(userStore, deviceStore, historyStore);


                // 指定当前主窗口
                this.MainWindow = loginWindow;
                
                // 进入登录窗口
                loginWindow.Show();

                AppLog.Info("数据库已准备好，登录窗口已显示");
            }
            catch (Exception ex)
            {
                AppLog.Error("程序启动失败", exception: ex);
                // 启动失败就提示原因，不继续显示无法正常工作的界面。
                MessageBox.Show(
                    $"程序启动失败：{ex.Message}",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }

        /// <summary>
        /// 界面线程出现未处理异常时，记录日志；不吞掉异常。
        /// </summary>
        private void RecordDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            AppLog.Error("界面线程未处理异常", exception: e.Exception);

        /// <summary>
        /// 程序出现未处理异常时，记录日志。
        /// </summary>
        private void RecordDomainError(object sender, UnhandledExceptionEventArgs e) =>
            AppLog.Error("程序未处理异常", exception: e.ExceptionObject as Exception);

        /// <summary>
        /// 后台 Task 的异常没有被调用方处理时，收到通知并记日志。
        /// </summary>
        private void RecordTaskError(object? sender, UnobservedTaskExceptionEventArgs e) =>
            AppLog.Error("后台任务未观察异常", exception: e.Exception);

        /// <summary>
        /// 程序退出时自动调用，写退出日志并解除异常事件订阅。
        /// </summary>
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
