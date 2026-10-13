using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 监控右侧的一行读数：在原读数行上补上设备名称。
/// Reading 指向任务里的原 PointReadingViewModel，数值改变就通知这张表更新。
/// </summary>
public sealed class MonitoringReadingViewModel : ObservableObject, IDisposable
{
    public long DeviceId { get; }
    public string DeviceName { get; }
    public PointReadingViewModel Reading { get; }
    public string PointName => Reading.Point.Name;
    public ushort Address => Reading.Point.Address;
    public string ValueText => Reading.ValueText;
    public string QualityText => Reading.QualityText;
    public string ReadTimeText => Reading.ReadTimeText;

    // 汇总时创建显示行，并接上原读数的变化通知。
    public MonitoringReadingViewModel(DeviceInfo device, PointReadingViewModel reading)
    {
        ArgumentNullException.ThrowIfNull(device);
        // 保留原读数行；上面的 ValueText 等属性直接从它取值。
        Reading = reading ?? throw new ArgumentNullException(nameof(reading));
        // 汇总表有多台设备，所以要带上设备编号和名称。
        DeviceId = device.Id;
        DeviceName = device.Name;
        // 原行变化时，也要通知汇总表重新显示。
        Reading.PropertyChanged += Reading_PropertyChanged;
    }

    // 把原读数的变化通知转给汇总表，不重新读取设备。
    // 由原读数行 PointReadingViewModel 的 PropertyChanged 事件调用。
    private void Reading_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            // 没指定属性名，就刷新三个显示列。
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(ReadTimeText));
        }
        else if (e.PropertyName is nameof(PointReadingViewModel.ValueText)
            or nameof(PointReadingViewModel.QualityText) or nameof(PointReadingViewModel.ReadTimeText))
            // 两边属性同名，直接转发这个属性的通知。
            OnPropertyChanged(e.PropertyName);
    }

    // 不再显示这行时，解除它对原读数的订阅。
    // 由 MonitoringViewModel 移除或清空读数显示行时调用。
    public void Dispose() => Reading.PropertyChanged -= Reading_PropertyChanged;
}
