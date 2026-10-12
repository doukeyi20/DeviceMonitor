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
/// 趋势弹窗的数据源：固定所选记录的设备/点位编号，沿用父页面查过的条件和最大记录编号。
/// 查询保存过的读数，最多画最近 1000 条；不连接设备、不启动采集，真正画线由 HistoryTrendChart 完成。
/// </summary>
public sealed partial class HistoryTrendViewModel : ObservableObject
{
    public const int MaximumPoints = 1000;
    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;
    private readonly HistoryRecord _selectedRecord;
    private readonly HistoryFilter _filter;
    private readonly long _snapshotId;
    private bool _isShuttingDown;
    private IReadOnlyList<HistoryRecord> _records = Array.Empty<HistoryRecord>();
    private long _totalRecords;

    public string SeriesTitle => $"{_selectedRecord.DeviceName} · {_selectedRecord.PointName}";
    public string IdentityText => $"设备 #{_selectedRecord.DeviceId} / 点位 #{_selectedRecord.PointId}；沿用历史页面本次查询条件与数据批次。";
    public IReadOnlyList<HistoryRecord> Records => _records;
    public long TotalRecords => _totalRecords;
    public bool IsTruncated => _totalRecords > _records.Count;
    // 统计只针对图中这些读数，不能冒充全部历史的最大/最小/平均值。
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
    /// HistoryViewModel.CreateTrendViewModel 创建这里，继续传同一个 HistoryStore。
    /// record 决定设备和点位编号；filter、snapshotId 固定父页面已经查过的范围。
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
    /// HistoryTrendWindow.Loaded 调用此命令。后台查完整匹配范围的最近 1000 条，不是复制当前表格一页。
    /// 回到界面线程后换掉 Records，图表收到新数据就重画；查询失败不伪造数值。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadTrend))]
    private async Task LoadTrendAsync()
    {
        if (!CanLoadTrend()) return;
        IsLoading = true;
        PageMessage = "正在读取已保存的点位历史，不会向设备发送请求…";
        try
        {
            HistoryTrendData result = await Task.Run(() => _historyStore.GetPointTrend(_currentUser,
                _selectedRecord.DeviceId, _selectedRecord.PointId, _filter, _snapshotId, MaximumPoints));
            if (_isShuttingDown) return; // 关窗后不再把迟到的查询结果填回界面。
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
    /// 加载趋势命令的条件：窗口没退出，也没有另一份趋势查询在进行。
    /// </summary>
    private bool CanLoadTrend() => !_isShuttingDown && !IsLoading;

    /// <summary>
    /// 自动入口：生成属性 IsLoading 改变后调用，让加载命令重新判断能否再次执行。
    /// </summary>
    partial void OnIsLoadingChanged(bool value) => LoadTrendCommand.NotifyCanExecuteChanged();

    /// <summary>窗口关闭或主窗口退出时调用：不再发新查询，等已经开始的那次查完。</summary>
    public async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        LoadTrendCommand.NotifyCanExecuteChanged();
        await (LoadTrendCommand.ExecutionTask ?? Task.CompletedTask);
    }
}
