using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Sampling;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 监控表格中的一行：一个点的最新读数和当前报警放在一起。
/// Reading 仍是采集任务里的原读数行，这里不重复采集或保存记录。
/// </summary>
public sealed class MonitoringReadingViewModel : ObservableObject, IDisposable
{
    private readonly AlarmTracker? _alarms;
    private readonly DeviceSamplingViewModel? _sampling;
    private AlarmRecord? _alarm; // 当前报警，或停止前尚未恢复的报警；恢复后清掉。
    private string? _readFailure; // 重新读取时保留失败提示，真正读成功后才清掉。

    public long DeviceId { get; }
    public string DeviceName { get; private set; }
    public PointReadingViewModel Reading { get; }
    public string PointName => Reading.Point.Name;
    public string DisplayName => $"{DeviceName} / {PointName}"; // 波形选择框带上设备名，防止同名点混淆。
    public ushort Address => Reading.Point.Address;
    public string ValueText => Reading.ValueText;
    public string QualityText => Reading.QualityText;
    // 监控表只显示到秒；完整读取时间仍保留在原结果和历史记录里。
    public string ReadTimeText => Reading.LatestResult is { } result
        ? result.ReadAt.ToLocalTime().ToString("HH:mm:ss") : Reading.ReadTimeText;
    public bool IsAlert => _alarm is not null || _readFailure is not null;
    public string StatusText
    {
        get
        {
            // 停止不代表数值恢复正常，仍保留最后一次报警说明。
            if (_sampling is { IsRunning: false }) return IsAlert ? "已停止 · 不再监测" : "已停止";
            // 循环已结束但保存还在收尾时，也不能显示“正常”。
            if (_sampling?.StatusText.StartsWith("正在停止", StringComparison.Ordinal) == true
                || _sampling?.StatusText.StartsWith("已停止", StringComparison.Ordinal) == true
                || _sampling?.StatusText.StartsWith("采集已结束", StringComparison.Ordinal) == true) return "正在停止";
            if (_alarm is not null)
                return _alarm.State == AlarmState.NotMonitoring ? "不再监测" : _alarm.KindText;
            if (_readFailure is not null) return "读取失败";
            return Reading.QualityText == "成功" ? "正常" : Reading.QualityText;
        }
    }
    // 表里只放简短状态；鼠标悬停再查看具体原因。
    public string DetailsText => $"{DisplayName}；地址 {Address}。{StatusText}。"
        + (_alarm is null ? string.Empty : $" {_alarm.Message}")
        + (_readFailure is null ? string.Empty : $" {_readFailure}");

    /// <summary>
    /// 由 MonitoringViewModel 汇总勾选设备的读数时创建。
    /// 接上原读数、报警和任务状态的通知，让表格随实际结果更新。
    /// </summary>
    public MonitoringReadingViewModel(DeviceInfo device, PointReadingViewModel reading,
        AlarmTracker? alarms = null, DeviceSamplingViewModel? sampling = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        Reading = reading ?? throw new ArgumentNullException(nameof(reading));
        DeviceId = device.Id;
        DeviceName = device.Name;
        _alarms = alarms;
        _sampling = sampling;

        Reading.PropertyChanged += Reading_PropertyChanged;
        if (_alarms is not null)
            ((INotifyCollectionChanged)_alarms.Records).CollectionChanged += Alarms_CollectionChanged;
        if (_sampling is not null) _sampling.PropertyChanged += Sampling_PropertyChanged;
        UpdateReadFailure();
        RefreshAlarm();
    }

    // 由 MonitoringViewModel 刷新设备列表时调用；改名后更新文字，不换原读数或清空波形。
    public void UpdateDevice(DeviceInfo device)
    {
        if (DeviceName == device.Name) return;
        DeviceName = device.Name;
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DetailsText));
    }

    // 由原 PointReadingViewModel 的 PropertyChanged 调用；只转发显示，不发请求。
    private void Reading_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PointReadingViewModel.ValueText))
            OnPropertyChanged(nameof(ValueText));
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PointReadingViewModel.ReadTimeText))
            OnPropertyChanged(nameof(ReadTimeText));
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PointReadingViewModel.QualityText))
        {
            UpdateReadFailure();
            OnPropertyChanged(nameof(QualityText));
            NotifyState();
        }
    }

    // 原行开始下一次读取时不清掉旧失败，只有成功或主动取消才清掉。
    private void UpdateReadFailure()
    {
        string quality = Reading.QualityText;
        if (quality == "成功" || quality.Contains("取消", StringComparison.Ordinal)) _readFailure = null;
        else if (quality != "尚未读取" && !quality.StartsWith("读取中", StringComparison.Ordinal)) _readFailure = quality;
    }

    // 由报警集合变化调用。新建、持续、恢复和停止监测都会经过这里。
    private void Alarms_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlarm();

    // 构造行和报警集合变化时调用；从活动字典取当前报警，再更新简短状态。
    private void RefreshAlarm()
    {
        if (_alarms is null) return;
        AlarmRecord? current = _alarms.GetCurrentAlarm(DeviceId, Reading.Point.Id);
        if (current is not null) _alarm = current;
        else if (_alarm is not null)
        {
            // 看报警的最终状态：恢复就清红，停止监测仍不是恢复。
            AlarmRecord? ended = _alarms.Records.FirstOrDefault(item => item.EventId == _alarm.EventId);
            _alarm = ended?.State == AlarmState.NotMonitoring ? ended
                : ended is null && _alarm.State == AlarmState.NotMonitoring ? _alarm : null;
        }
        NotifyState();
    }

    // 由采集任务通知调用；停止后显示“已停止”，不把最后一次成功当成正在正常采集。
    private void Sampling_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceSamplingViewModel.IsRunning) or nameof(DeviceSamplingViewModel.StatusText))
            NotifyState();
    }

    // 由读数、报警和任务状态的更新方法共用，通知表格刷新文字和红色提示。
    private void NotifyState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsAlert));
        OnPropertyChanged(nameof(DetailsText));
    }

    // 由 MonitoringViewModel 移除显示行或退出时调用；不停止采集任务。
    public void Dispose()
    {
        Reading.PropertyChanged -= Reading_PropertyChanged;
        if (_alarms is not null)
            ((INotifyCollectionChanged)_alarms.Records).CollectionChanged -= Alarms_CollectionChanged;
        if (_sampling is not null) _sampling.PropertyChanged -= Sampling_PropertyChanged;
    }
}
