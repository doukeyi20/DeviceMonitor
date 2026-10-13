using System;
using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 各功能页数据源的共同父类，保存页面标题，不是界面控件。
    /// </summary>
    public abstract class PageViewModel : ObservableObject

    {
        public string Title { get; }

        // 各页面创建时传入自己的标题，左侧导航用它显示名称。
        protected PageViewModel(string title)
        {
            Title = title;
        }

    }
}
