// 查询指定点位的历史趋势数据。
using System;
using DeviceMonitor;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

public sealed partial class HistoryTrendViewModel : ObservableObject
{
    public const int MaximumPoints = 1000;

    private readonly UserAccount _currentUser;
    // 趋势查询沿用历史页的条件和编号范围，不向设备重新采集。
    private readonly HistoryStore _historyStore;
    private readonly HistoryRecord _selectedRecord;
    private readonly HistoryFilter _filter;
    private readonly long _snapshotId;
    private bool _isClosed;
    private IReadOnlyList<HistoryRecord> _records = Array.Empty<HistoryRecord>();
    private long _totalRecords;

    public string SeriesTitle => $"{_selectedRecord.DeviceName} · {_selectedRecord.PointName}" +
        (string.IsNullOrEmpty(_selectedRecord.Unit) ? "" : $"（{_selectedRecord.Unit}）");
    public string IdentityText => $"设备 #{_selectedRecord.DeviceId} / 点位 #{_selectedRecord.PointId}";
    public IReadOnlyList<HistoryRecord> Records => _records;
    public long TotalRecords => _totalRecords;
    public bool IsTruncated => _totalRecords > _records.Count;

    public string MinimumValueText => Records.Count == 0 ? "—" : NumericValue.Format(Records.Min(item => item.Value), _selectedRecord.Unit);
    public string MaximumValueText => Records.Count == 0 ? "—" : NumericValue.Format(Records.Max(item => item.Value), _selectedRecord.Unit);
    public string AverageValueText => Records.Count == 0 ? "—" : NumericValue.Format(Records.Average(item => item.Value), _selectedRecord.Unit);
    public string RangeText => Records.Count == 0 ? "尚无绘图数据。"
        : $"图中 {Records.Count} / 匹配 {TotalRecords} 条；时间 {Records[0].ReadAtLocal:yyyy-MM-dd HH:mm:ss.fff} — {Records[^1].ReadAtLocal:yyyy-MM-dd HH:mm:ss.fff}";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _pageMessage = string.Empty;

    public HistoryTrendViewModel(UserAccount currentUser, HistoryStore historyStore,
    HistoryRecord record, HistoryFilter filter, long snapshotId)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _selectedRecord = record ?? throw new ArgumentNullException(nameof(record));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        _snapshotId = snapshotId;
        if (!_currentUser.IsEnabled || _currentUser.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有已启用的管理员可以查看历史趋势。");
    }

    /// <summary>查这个点最近的历史读数，更新 Records，通知图表重画。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadTrend))]
    private async Task LoadTrendAsync()
    {
        if (!CanLoadTrend())
            return;

        IsLoading = true;
        PageMessage = "正在查询历史…";
        try
        {
            HistoryTrendData result = await Task.Run(() => _historyStore.GetPointTrend(_currentUser,
                _selectedRecord.DeviceId, _selectedRecord.PointId, _filter, _snapshotId, MaximumPoints, _selectedRecord.Unit));
            if (_isClosed)
                return;

            _records = result.Records;
            _totalRecords = result.TotalRecords;
            OnPropertyChanged(nameof(Records));
            OnPropertyChanged(nameof(TotalRecords));
            OnPropertyChanged(nameof(IsTruncated));
            OnPropertyChanged(nameof(MinimumValueText));
            OnPropertyChanged(nameof(MaximumValueText));
            OnPropertyChanged(nameof(AverageValueText));
            OnPropertyChanged(nameof(RangeText));
            PageMessage = Records.Count == 0 ? "该点位在本次查询范围内没有已保存的读数。"
                : IsTruncated ? $"匹配 {TotalRecords} 条，显示最新 {MaximumPoints} 条。"
                : $"已绘制 {Records.Count} 条读数。";
        }
        catch (Exception ex)
        {
            AppLog.Error("历史趋势查询失败", exception: ex);
            if (!_isClosed)
                PageMessage = $"趋势查询失败：{ex.Message}。没有修改历史数据。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>没在退出或查询时，才允许加载趋势。</summary>
    private bool CanLoadTrend() => !_isClosed && !IsLoading;

    /// <summary>更新加载命令能否执行。</summary>
    partial void OnIsLoadingChanged(bool value) => LoadTrendCommand.NotifyCanExecuteChanged();

    /// <summary>关窗后不再更新这份显示；已开始的只读查询自行结束并释放上下文。</summary>
    public void StopUpdating()
    {
        _isClosed = true;
        LoadTrendCommand.NotifyCanExecuteChanged();
    }
}
