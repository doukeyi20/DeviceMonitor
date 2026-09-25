using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.ViewModels
{
    public class MainViewModel:ObservableObject
    {
        public string WindowTitle { get; } = "设备监测与数据追溯系统";

        // 提供给左侧导航栏的页面列表
        public IReadOnlyList<PageViewModel> Pages { get; }

        // 当前选中的页面 CurrentPage
        private PageViewModel _currentPage;
       
        public PageViewModel CurrentPage
        {
            get => _currentPage;
            set 
            { 
                if(value == null)
                {
                    return;
                }

                // 修改字段并通知；WPF绑定收到通知后重新读取属性，更新界面。
                SetProperty(ref _currentPage, value);
            }
        }

        public MainViewModel()
        {
            var monitoringPage = new MonitoringViewModel();
            var deviceManagementPage = new DeviceManagementViewModel();
            // 创建页面列表
            Pages = new PageViewModel[]
                {
                    monitoringPage,
                    deviceManagementPage
                };

            // 启动时默认显示实时监控。
            _currentPage = monitoringPage;
        }

    }
}
