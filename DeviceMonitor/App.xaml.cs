// 初始化日志、数据库和登录窗口，记录程序级异常。
using DeviceMonitor.Model;
using System.Windows;
using DeviceMonitor.View;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DeviceMonitor
{
    public partial class App : Application
    {
        /// <summary>程序启动时，初始化基础服务并显示登录窗口。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 获取当前用户的本地应用数据目录，再拼上项目和日志文件夹名称。。
                AppLog.Initialize(
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                    "DeviceMonitor", "Logs")
                    );

                AppLog.Info("程序启动");

                DispatcherUnhandledException += RecordDispatcherError;
                AppDomain.CurrentDomain.UnhandledException += RecordDomainError;
                TaskScheduler.UnobservedTaskException += RecordTaskError;

                // 数据库初始化器，负责创建数据库文件、应用迁移和建表
                DatabaseInitializer initializer = new DatabaseInitializer();

                // 启动时第一次应用待执行迁移，不重复建表或清空已有数据。
                initializer.Initialize();

                // 创建数据库操作入口
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
