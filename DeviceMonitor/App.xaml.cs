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
    /// 程序从这里开始：准备日志和数据库，把三个 Store 交给登录窗口，再显示窗口。
    /// 先看 OnStartup；异常记录和 OnExit 是出错、退出时才走的分支。
    /// </summary>
    public partial class App : Application
    {

        /// <summary>
        /// WPF 启动时自动调用。按顺序准备日志、数据库、UserStore/DeviceStore/HistoryStore，再创建并显示 LoginWindow。
        /// 只是初始化和显示登录页，还没有登录，也没有开始采集。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 后面是程序启动时要做的事。

            try
            {
                // 调用Applog的方法给传入路径字符串，得到写日志的对象。
                AppLog.Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeviceMonitor", "Logs"));

                // 内部调用 FileLog.Write 写日志文件，记录程序启动。
                AppLog.Info("程序启动");

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

                // 历史也放在同一个库里。这个对象后面会一路传到每台设备的采集任务。
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
        /// 界面线程有未处理异常时，WPF 调用这里记录调用位置；不假装错误已经解决。
        /// </summary>
        private void RecordDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            AppLog.Error("界面线程未处理异常", exception: e.Exception);

        /// <summary>
        /// 程序遇到未处理异常时，由 .NET 通知这里记日志，不在这里重新启动采集。
        /// </summary>
        private void RecordDomainError(object sender, UnhandledExceptionEventArgs e) =>
            AppLog.Error("程序未处理异常", exception: e.ExceptionObject as Exception);

        /// <summary>
        /// 后台 Task 的异常一直没有被观察时，由 .NET 通知这里留下日志。
        /// </summary>
        private void RecordTaskError(object? sender, UnobservedTaskExceptionEventArgs e) =>
            AppLog.Error("后台任务未观察异常", exception: e.Exception);

        /// <summary>
        /// 程序真正退出时由 WPF 调用：记退出日志，解除启动时登记的异常处理方法。
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
