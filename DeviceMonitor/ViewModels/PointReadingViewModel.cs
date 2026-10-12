using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 一份任务中某个点的最新显示结果：Point 是配置，ValueText/QualityText/ReadTimeText 是运行时显示。
/// DeviceSamplingViewModel 读取前、成功、失败分别调用 BeginRead/ShowResult/ShowFailure；本类不写数据库。
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
    /// 由 DeviceSamplingViewModel.UpdateRows 为每个采集点创建一行。
    /// 保存传来的 PointInfo 配置；读数、状态和时间另放在本对象的通知属性中。
    /// </summary>
    public PointReadingViewModel(PointInfo point) => Point = point;

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在读取这一行之前调用。
    /// 清掉上一轮的值和时间，把状态改成“读取中”，通知下方表格更新。
    /// </summary>
    public void BeginRead()
    {
        ValueText = "—";
        ReadTimeText = "—";
        QualityText = "读取中…";
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在这行读取成功后调用，传入 ModbusPointReader 返回的结果。
    /// 把值、读取时间和成功状态写到通知属性中；这里只更新显示，不写数据库。
    /// </summary>
    public void ShowResult(PointReadResult result)
    {
        ValueText = result.Value.ToString(CultureInfo.InvariantCulture);
        ReadTimeText = result.ReadAt.ToLocalTime().ToString("HH:mm:ss.fff");
        QualityText = "成功";
    }

    /// <summary>
    /// 由 DeviceSamplingViewModel.RunAsync 在这行读取失败或取消时调用。
    /// 清掉旧值和时间，显示失败原因，避免把上次成功的读数当作本次有效值。
    /// </summary>
    public void ShowFailure(string message)
    {
        // 本次失败就不把上次成功的值冒充为当前有效值。
        ValueText = "—";
        ReadTimeText = "—";
        QualityText = message;
    }

    // 配置没有改变，就保留原来的行和读数，避免每轮都重建表格。
    /// <summary>
    /// 由 DeviceSamplingViewModel.UpdateRows 调用，把当前行的点位配置与数据库刚查到的配置比较。
    /// 返回 true 就保留这行；有变化才重建读数行。它不是比较两次读数。
    /// </summary>
    public bool HasSameConfiguration(PointInfo point) =>
        Point.Id == point.Id && Point.Name == point.Name
        && Point.RegisterType == point.RegisterType
        && Point.Address == point.Address && Point.DataType == point.DataType
        // 改限值后也要换成新 PointInfo，否则报警判断仍拿着上轮的旧限值。
        && Point.LowerLimit == point.LowerLimit && Point.UpperLimit == point.UpperLimit;
}
