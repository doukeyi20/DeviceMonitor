using System;
using DeviceMonitor.Core.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 点位历史趋势窗口的数据源，按所选点位查询已保存的读数，最多显示最近 1000 条。
/// 不读取设备；数据准备好后，由 HistoryTrendChart 画线。
/// </summary>
public sealed partial class HistoryTrendViewModel : ObservableObject
{
    public const int MaximumPoints = 1000;
    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;
    private readonly HistoryRecord _selectedRecord; // 用这条记录确定要看哪个设备、哪个点。
    private readonly HistoryFilter _filter; // 沿用历史页已查询的条件。
    private readonly long _snapshotId; // 沿用历史页的最大记录编号，排除后来新增的数据。
    private bool _isShuttingDown;
    private IReadOnlyList<HistoryRecord> _records = Array.Empty<HistoryRecord>();
    private long _totalRecords;

    public string SeriesTitle => $"{_selectedRecord.DeviceName} · {_selectedRecord.PointName}";
    public string IdentityText => $"设备 #{_selectedRecord.DeviceId} / 点位 #{_selectedRecord.PointId}；沿用历史页面本次查询条件与数据批次。";
    public IReadOnlyList<HistoryRecord> Records => _records; // 图表绑定的数据，每条历史读数画成一个点。
    public long TotalRecords => _totalRecords;
    public bool IsTruncated => _totalRecords > _records.Count;
    // 这些统计只算图中显示的记录，不是全部历史的统计。
    public string MinimumValueText => Records.Count == 0 ? "—" : Records.Min(item => item.Value).ToString(CultureInfo.CurrentCulture);
    public string MaximumValueText => Records.Count == 0 ? "—" : Records.Max(item => item.Value).ToString(CultureInfo.CurrentCulture);
    public string AverageValueText => Records.Count == 0 ? "—" : Records.Average(item => (double)item.Value).ToString("0.##", CultureInfo.CurrentCulture);
    public string RangeText => Records.Count == 0 ? "尚无绘图数据。"
        : $"图中 {Records.Count} / 匹配 {TotalRecords} 条；时间 {Records[0].ReadAtLocal:yyyy-MM-dd HH:mm:ss.fff} — {Records[^1].ReadAtLocal:yyyy-MM-dd HH:mm:ss.fff}";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _pageMessage = "打开窗口后查询该点位的历史读数。";

    /// <summary>
    /// 保存所选点位及历史页的查询范围，打开窗口后再查数据。
    /// </summary>
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

    /// <summary>
    /// 由 HistoryTrendWindow_Loaded 执行 LoadTrendCommand 时调用。
    /// 查这个点最近的历史读数，更新 Records，通知图表重画。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadTrend))]
    private async Task LoadTrendAsync()
    {
        if (!CanLoadTrend()) return;
        IsLoading = true;
        PageMessage = "正在读取已保存的点位历史，不会向设备发送请求…";
        try
        {
            // 后台按设备编号、点位编号和历史页范围查询，不只取表格当前页。
            HistoryTrendData result = await Task.Run(() => _historyStore.GetPointTrend(_currentUser,
                _selectedRecord.DeviceId, _selectedRecord.PointId, _filter, _snapshotId, MaximumPoints));
            if (_isShuttingDown) return; // 关窗后不再把迟到的查询结果填回界面。

            // 保存绘图数据和匹配总数，再刷新图表及统计文字。
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
                : IsTruncated ? $"匹配 {TotalRecords} 条，仅绘制其中最新的 {MaximumPoints} 条。可回历史页面缩短时间范围后再看。"
                : $"已绘制本次匹配的全部 {Records.Count} 条读数。要看新采集数据，请回历史页面重新查询。";
        }
        catch (Exception ex)
        {
            AppLog.Error("历史趋势查询失败", exception: ex);
            if (!_isShuttingDown) PageMessage = $"趋势查询失败：{ex.Message}。没有修改历史数据。";
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// 由 LoadTrendCommand 判断按钮能不能点时调用。
    /// 没在退出或查询时，才允许加载趋势。
    /// </summary>
    private bool CanLoadTrend() => !_isShuttingDown && !IsLoading;

    /// <summary>
    /// IsLoading 改变后自动调用，更新加载命令能否执行。
    /// </summary>
    partial void OnIsLoadingChanged(bool value) => LoadTrendCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// 由 HistoryTrendWindow.OnClosing 或 HistoryViewModel.StopOperationsAsync 调用。
    /// 退出时禁止新查询，再等已开始的查询完成。
    /// </summary>
    public async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        LoadTrendCommand.NotifyCanExecuteChanged();
        await (LoadTrendCommand.ExecutionTask ?? Task.CompletedTask);
    }
}
