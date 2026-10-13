using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels
{

    /// <summary>
    /// 记住本次要删除的设备、点位和确认结果，弹框后不再重新取选择。
    /// </summary>
    public sealed class PointDeleteRequest
    {
        public DeviceInfo Device { get; }
        public PointInfo Point { get; }
        public bool Confirmed { get; set; } // 默认 false，后台确认框选“是”才改为 true。

        // 固定本次删除目标，并检查点位确实属于这台设备。
        public PointDeleteRequest(DeviceInfo device, PointInfo point)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            Point = point ?? throw new ArgumentNullException(nameof(point));
            if (point.DeviceId != device.Id)
                throw new ArgumentException("这个采集点不属于当前设备。", nameof(point));
        }
    }
}
