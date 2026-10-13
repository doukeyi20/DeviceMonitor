using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 一个采集点的显示行：Point 是点位配置，另外三个属性显示数值、状态和时间。
/// DeviceSamplingViewModel 负责读取，再调用这里的方法更新显示。
/// </summary>
public partial class PointReadingViewModel : ObservableObject
{
    public PointInfo Point { get; }

    [ObservableProperty]
    private string _valueText = "—";

    [ObservableProperty]
    private string _qualityText = "尚未读取";

    [ObservableProperty]
    private string _readTimeText = "—";

    /// <summary>
    /// 为一个采集点创建显示行，先保存它的配置。
    /// </summary>
    public PointReadingViewModel(PointInfo point) => Point = point;

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取这个点之前调用。
    /// 开始读这个点之前，清掉旧值并显示“读取中”。
    /// </summary>
    public void BeginRead()
    {
        // 修改这些属性会通知表格更新，本方法不发读取请求。
        ValueText = "—";
        ReadTimeText = "—";
        QualityText = "读取中…";
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取成功后调用。
    /// 读取成功后，把结果填到数值、时间和状态列；不在这里保存历史。
    /// </summary>
    public void ShowResult(PointReadResult result)
    {
        // 把读到的值转成表格显示的文字。
        ValueText = result.Value.ToString(CultureInfo.InvariantCulture);
        // 读取时间转成本地时间显示。
        ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss.fff");
        QualityText = "成功";
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取失败后调用。
    /// 读取失败或取消时，清掉旧值并显示原因。
    /// </summary>
    public void ShowFailure(string message)
    {
        // 清掉旧值，避免把上次成功的读数当成这次的结果。
        ValueText = "—";
        ReadTimeText = "—";
        // 把传来的原因显示在状态列。
        QualityText = message;
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.UpdateRows 比较新旧配置时调用。
    /// 比较新旧点位配置。相同就保留这行，不同就由 UpdateRows 重建。
    /// </summary>
    public bool HasSameConfiguration(PointInfo point) =>
        Point.Id == point.Id && Point.Name == point.Name
        && Point.RegisterType == point.RegisterType
        && Point.Address == point.Address && Point.DataType == point.DataType
        // 限值也是配置，变了就要使用新 PointInfo。
        && Point.LowerLimit == point.LowerLimit && Point.UpperLimit == point.UpperLimit;
}
