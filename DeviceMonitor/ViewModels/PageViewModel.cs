using System;
using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 主窗口导航页面的数据源基类，保存页面 Title；各功能页继承它。
    /// 它不是界面控件，ContentControl 会通过 DataTemplate 为实际子类找到对应 View。
    /// </summary>
    public abstract class PageViewModel : ObservableObject

    {
        public string Title { get; }

        protected PageViewModel(string title)
        {
            Title = title;
        }

    }
}
