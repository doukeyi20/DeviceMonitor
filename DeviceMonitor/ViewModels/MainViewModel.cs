using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceMonitor.ViewModels
{
    /// <summary>
    /// 主界面的数据对象
    /// </summary>
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

        // 当前登录的用户信息
        public UserAccount CurrentUser { get; }

        // 用于显示用户信息
        public string CurrentUserRoleName =>
    CurrentUser.Role == UserRole.Administrator
        ? "管理员"
        : "操作员";

        public MainViewModel(UserAccount currentUser, UserStore userStore)
        {
            CurrentUser = currentUser
                ?? throw new ArgumentNullException(nameof(currentUser));
            ArgumentNullException.ThrowIfNull(userStore);

            var monitoringPage = new MonitoringViewModel();

            // 通过用户角色，决定左侧导航栏显示哪些页面。
            if (CurrentUser.Role == UserRole.Administrator)
            {
                Pages = new PageViewModel[]
                {
            monitoringPage,
            new DeviceManagementViewModel(),
            new UserManagementViewModel(CurrentUser, userStore)
                };
            }
            else
            {
                Pages = new PageViewModel[]
                {
            monitoringPage
                };
            }

            // 两种角色进入系统时，都默认显示实时监控。
            _currentPage = monitoringPage;
        }

    }
}
