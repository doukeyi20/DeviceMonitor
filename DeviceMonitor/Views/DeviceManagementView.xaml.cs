using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DeviceMonitor.ViewModels;

namespace DeviceMonitor.Views
{
    /// <summary>
    /// DeviceManagementView.xaml 的交互逻辑
    /// </summary>
    public partial class DeviceManagementView : UserControl
    {
        public DeviceManagementView()
        {
            InitializeComponent();
        }

        private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DeviceManagementViewModel viewModel)
            {
                MessageBox.Show("设备管理页面尚未准备好。");
                return;
            }

            // 弹窗是独立窗口，不会自动继承当前页面的 DataContext，
            // 因此明确把同一个 ViewModel 对象传进去。
            AddDeviceWindow dialog = new AddDeviceWindow(viewModel);

            Window? owner = Window.GetWindow(this);
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }
    }
}
