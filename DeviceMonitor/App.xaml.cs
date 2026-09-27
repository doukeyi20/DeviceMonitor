using DeviceMonitor.Core.Persistence;
using System.Configuration;
using System.Data;
using System.Windows;
using DeviceMonitor.Views;

namespace DeviceMonitor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // 程序运行时，先运行 OnStartup 方法
        protected override void OnStartup(StartupEventArgs e)
        { 
            base.OnStartup(e);

            try
            {
                // 启动时先确保数据库文件存在。
                DatabaseInitializer initializer = new DatabaseInitializer();
                
                initializer.Initialize();

                // 数据库准备好后，把数据库路径交给数据库操作类。
                UserStore userStore = new UserStore(initializer.DatabasePath);

                DeviceStore deviceStore = new DeviceStore(initializer.DatabasePath);

                // 先显示登录窗口，而不是直接进入业务主窗口。
                LoginWindow loginWindow = new LoginWindow(userStore, deviceStore);
                this.MainWindow = loginWindow;
                // 进入登录窗口，可以看登录窗口的代码了

                loginWindow.Show();
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
