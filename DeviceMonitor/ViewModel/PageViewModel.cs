// 功能页 ViewModel 基类。
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceMonitor.ViewModel
{
    public abstract class PageViewModel : ObservableObject
    {
        public string Title { get; }

        protected PageViewModel(string title)
        {
            Title = title;
        }

        /// <summary>退出时由主窗口统一调用；各页面等待自己的操作结束并解除订阅。</summary>
        public abstract Task StopOperationsAsync();
    }
}
