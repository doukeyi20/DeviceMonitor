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
    /// AcquisitionView.xaml 的交互逻辑
    /// </summary>
    public partial class AcquisitionView : UserControl
    {
        public AcquisitionView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 进入页面时自动加载，返回页面时也重新读取最新启用配置。
        /// 这里只触发 ViewModel 的刷新命令，不在后台重复编写查询逻辑。
        /// </summary>
        private async void AcquisitionView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is AcquisitionViewModel viewModel &&
                viewModel.RefreshDevicesCommand.CanExecute(null))
            {
                // 正在刷新时命令不可执行，避免自动加载与手动刷新重叠。
                await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }
    }
}
