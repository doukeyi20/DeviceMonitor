using DeviceMonitor.Core.Persistence;
using System.Configuration;
using System.Data;
using System.Windows;

namespace DeviceMonitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 启动时先确保数据库文件存在。
                DatabaseInitializer initializer = new DatabaseInitializer();
                initializer.Initialize();

                // 数据库准备好之后，再创建并显示主窗口。
                MainWindow mainWindow = new MainWindow();
                this.MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                // 启动失败就提示原因，不继续显示无法正常工作的界面。
                MessageBox.Show(
                    $"程序启动失败：{ex.Message}",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }
    }

}
