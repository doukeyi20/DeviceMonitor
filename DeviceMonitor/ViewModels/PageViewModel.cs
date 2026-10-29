// 功能页 ViewModel 基类。
using System;
using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceMonitor.ViewModels
{
    public abstract class PageViewModel : ObservableObject

    {
        public string Title { get; }

        protected PageViewModel(string title)
        {
            Title = title;
        }
    }
}
