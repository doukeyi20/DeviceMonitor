using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 监控右侧读数表的一行：补上设备名称，Reading 指向采集任务里的原 PointReadingViewModel。
/// 原读数改变就转发通知；不复制另一份采集数据，不再次连接设备。
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

    // MonitoringViewModel 汇总勾选设备的读数时创建。Reading 就是任务表里原来的行。
    public MonitoringReadingViewModel(DeviceInfo device, PointReadingViewModel reading)
    {
        ArgumentNullException.ThrowIfNull(device);
        Reading = reading ?? throw new ArgumentNullException(nameof(reading));
        DeviceId = device.Id;
        DeviceName = device.Name;
        Reading.PropertyChanged += Reading_PropertyChanged;
    }

    // 原读数被采集任务改了，就通知本表更新相应列，不在这里重新读取设备。
    private void Reading_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(QualityText));
            OnPropertyChanged(nameof(ReadTimeText));
        }
        else if (e.PropertyName is nameof(PointReadingViewModel.ValueText)
            or nameof(PointReadingViewModel.QualityText) or nameof(PointReadingViewModel.ReadTimeText))
            OnPropertyChanged(e.PropertyName);
    }

    // 取消勾选、刷新列表或关窗后不再展示这行，解除订阅，别让旧行一直收到更新。
    public void Dispose() => Reading.PropertyChanged -= Reading_PropertyChanged;
}
