using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 删除采集点时临时携带设备、目标点和确认结果。
    /// 目标在点击时固定，确认框关闭后继续删同一条，不再根据后来选中行猜目标。
    /// </summary>
    public sealed class PointDeleteRequest
    {
        public DeviceInfo Device { get; }
        public PointInfo Point { get; }
        public bool Confirmed { get; set; }

        public PointDeleteRequest(DeviceInfo device, PointInfo point)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            Point = point ?? throw new ArgumentNullException(nameof(point));
            if (point.DeviceId != device.Id)
                throw new ArgumentException("这个采集点不属于当前设备。", nameof(point));
        }
    }
}
