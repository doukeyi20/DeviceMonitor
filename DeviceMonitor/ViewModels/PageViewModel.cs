using System;
using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceMonitor.ViewModels
{

    // <summary>
    // PageViewModel 是各功能页面 ViewModel 的共同父类，提供标题和属性通知能力，并让主窗口能够统一管理不同页面。
    //<summary>
    public abstract class PageViewModel:ObservableObject
    
    {
        public string Title { get; }

        protected PageViewModel(string title)
        {
            Title = title;
        }

    }
}
