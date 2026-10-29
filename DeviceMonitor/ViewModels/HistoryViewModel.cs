// 管理采集历史筛选、分页、导出和趋势查询。
using System.Globalization;
using System;
using DeviceMonitor.Core;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels;

/// <summary>读数历史页面的数据和命令：查询、翻页、导出，以及打开点位趋势。</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    public const int PageSize = 200;

    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;
    private readonly HistoryCsvExporter _csvExporter;
    private readonly DeviceFilterOption _allDevices = new(null, "全部设备");

    // 输入框和已应用条件分开，翻页、导出沿用上次成功查询。
    private HistoryFilter? _appliedFilter;

    // 固定查询编号上限，排除后续新记录，避免翻页漂移。
    private long _snapshotId;
    private long _totalRecords;
    private int _currentPage;
    private bool _isShuttingDown;
    private HistoryTrendViewModel? _openTrend;

    public ObservableCollection<DeviceFilterOption> DeviceOptions { get; } = new();
    public ObservableCollection<HistoryRecord> Records { get; } = new();

    public AlarmHistoryViewModel Alarms { get; }

    [ObservableProperty] private DeviceFilterOption? _selectedDevice;
    [ObservableProperty] private string _pointNameFilter = string.Empty;
    [ObservableProperty] private bool _useTimeFilter;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTimeText = "00:00:00";
    [ObservableProperty] private string _endTimeText = "23:59:59";
    [ObservableProperty] private bool _filtersChanged;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowTrendCommand))]
    private HistoryRecord? _selectedRecord;

    public int CurrentPage => _currentPage;

    public bool HasLoadedRecords => _appliedFilter is not null;
    public long TotalRecords => _totalRecords;
    public long TotalPages => _totalRecords == 0 ? 0 : (_totalRecords - 1) / PageSize + 1;
    public bool IsBusy => IsLoading || IsExporting || _isShuttingDown;
    public bool CanEditFilters => !IsBusy;
    public bool ShowEmptyMessage => HasLoadedRecords && !IsBusy && Records.Count == 0;
    public string RefreshButtonText => IsLoading ? "查询中…" : "查询 / 刷新";
    public string RecordSummary => $"本页 {Records.Count} 条 / 匹配 {_totalRecords} 条";
    public string PageDescription => $"第 {CurrentPage} / {TotalPages} 页 · 每页 {PageSize} 条";
    public string FilterStatusText => FiltersChanged
        ? "条件已改但尚未查询；翻页、导出和趋势仍使用上次查询结果。"
        : string.Empty;

    private string _pageMessage = string.Empty;
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    public event Func<string?>? ExportPathRequested;
    public event Action<HistoryTrendViewModel>? TrendRequested;

    public HistoryViewModel(UserAccount currentUser, HistoryStore historyStore, AlarmStore? alarmStore = null) : base("历史记录")
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的管理员可以进入历史记录。");
        _csvExporter = new HistoryCsvExporter(historyStore);
        Alarms = new AlarmHistoryViewModel(currentUser, alarmStore ?? new AlarmStore(historyStore.DatabasePath));
        DeviceOptions.Add(_allDevices);
        _selectedDevice = _allDevices;
    }

    /// <summary>按当前输入查询第一页，并把最新保存的记录纳入本次查询。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords())
            return;
        try
        {
            HistoryFilter filter = BuildFilter();

            await LoadPageAsync(filter, 1, null, refreshDeviceOptions: true);
        }
        catch (Exception ex)
        {
            FiltersChanged = true;
            PageMessage = $"查询条件无效：{ex.Message}";
        }
    }

    /// <summary>没有查询、导出或退出工作时，才允许重新查询。</summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>把筛选输入装成查询条件；启用时间筛选时，先转成数据库使用的 UTC 时间。</summary>
    private HistoryFilter BuildFilter()
    {
        if (!UseTimeFilter)
            return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter);

        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter, range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>后台查一页记录，成功后更新表格和页码，失败就保留旧结果。</summary>
    private async Task LoadPageAsync(HistoryFilter filter, int page, long? snapshotId, bool refreshDeviceOptions = false)
    {
        IsLoading = true;
        PageMessage = "正在查询历史记录…";
        try
        {
            var result = await Task.Run(() =>
            {
                var options = refreshDeviceOptions ? _historyStore.GetDeviceOptions(_currentUser) : null;

                var data = _historyStore.GetPage(_currentUser, filter, page, PageSize, snapshotId);

                return (Options: options, Data: data);
            });

            if (_isShuttingDown)
                return;

            if (result.Options is not null)
            {
                DeviceOptions.Clear();
                DeviceOptions.Add(_allDevices);
                foreach (DeviceFilterOption option in result.Options)
                    DeviceOptions.Add(option);
                SelectedDevice = DeviceOptions.FirstOrDefault(item => item.DeviceId == filter.DeviceId);
                if (SelectedDevice is null)
                {
                    SelectedDevice = new DeviceFilterOption(filter.DeviceId, "暂无历史的设备");
                    DeviceOptions.Add(SelectedDevice);
                }
            }

            long? selectedRecordId = SelectedRecord?.Id;
            Records.Clear();
            foreach (HistoryRecord record in result.Data.Records)
                Records.Add(record);

            SelectedRecord = Records.FirstOrDefault(record => record.Id == selectedRecordId)
                ?? Records.FirstOrDefault();

            _appliedFilter = filter;
            _snapshotId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;

            if (refreshDeviceOptions)
                FiltersChanged = false;
            PageMessage = _totalRecords == 0 ? "没有匹配的历史记录。"
                : $"查询成功，匹配 {_totalRecords} 条，当前显示第 {_currentPage} 页；最新读数在前。";
        }
        catch (Exception ex)
        {
            AppLog.Error("采集历史查询失败", exception: ex);
            PageMessage = $"查询失败：{ex.Message}。已保留上次结果，请检查后重试。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>恢复默认筛选条件，再查询第一页。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task ResetFiltersAsync()
    {
        if (!CanRefreshRecords())
            return;
        SelectedDevice = _allDevices;
        PointNameFilter = string.Empty;
        UseTimeFilter = false;
        StartDate = EndDate = DateTime.Today;
        StartTimeText = "00:00:00";
        EndTimeText = "23:59:59";
        await RefreshRecordsAsync();
    }

    /// <summary>按上次已查询的范围加载上一页。</summary>
    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => CanPreviousPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage - 1, _snapshotId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且不是第一页，才允许上一页。</summary>
    private bool CanPreviousPage() => !IsBusy && HasLoadedRecords && _currentPage > 1;

    /// <summary>按同一查询范围加载下一页，不混入后来新增的数据。</summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage + 1, _snapshotId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且后面还有页，才允许下一页。</summary>
    private bool CanNextPage() => !IsBusy && HasLoadedRecords && _currentPage < TotalPages;

    /// <summary>为选中记录的点位打开趋势窗口；趋势会单独查这个点的历史，不只画当前页。</summary>
    [RelayCommand(CanExecute = nameof(CanShowTrend))]
    private void ShowTrend()
    {
        if (!CanShowTrend())
            return;
        if (TrendRequested is null)
        {
            PageMessage = "页面尚未准备好，无法打开趋势窗口。";
            return;
        }
        try
        {
            _openTrend = CreateTrendViewModel(SelectedRecord!);
            ShowTrendCommand.NotifyCanExecuteChanged();

            TrendRequested.Invoke(_openTrend);
        }
        catch (Exception ex)
        {
            PageMessage = $"打开趋势失败：{ex.Message}";
        }
        finally
        {
            _openTrend = null;
            ShowTrendCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanShowTrend() => !IsBusy && HasLoadedRecords
        && _openTrend is null && SelectedRecord is not null && Records.Contains(SelectedRecord);

    /// <summary>把所选点位和本次查询范围交给趋势窗口的数据对象。</summary>
    public HistoryTrendViewModel CreateTrendViewModel(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (IsBusy || _appliedFilter is null || !Records.Contains(record))
            throw new InvalidOperationException("请先查询历史，再选中当前表格中的一条记录。");
        return new HistoryTrendViewModel(_currentUser, _historyStore, record, _appliedFilter, _snapshotId);
    }

    /// <summary>选择文件后，导出本次查询的全部结果，不只当前页。</summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords())
            return;

        HistoryFilter filter = _appliedFilter!;
        long snapshot = _snapshotId;
        long total = _totalRecords;
        bool exportFinished = false;
        IsExporting = true;
        try
        {
            if (ExportPathRequested is null)
                throw new InvalidOperationException("页面尚未准备好，无法选择导出文件。");

            string? path = ExportPathRequested.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                PageMessage = "已取消选择导出文件。";
                return;
            }

            var progress = new Progress<long>(count =>
            {
                if (!_isShuttingDown && !exportFinished)
                    PageMessage = $"正在导出：已写入 {count} / {total} 条，可取消。";
            });

            long written = await Task.Run(() => _csvExporter.Export(_currentUser, filter, snapshot,
                path, cancellationToken, progress), cancellationToken);
            if (!_isShuttingDown)
                PageMessage = $"已导出 {written} 条记录：{path}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_isShuttingDown)
                PageMessage = "导出已取消，原目标文件未被覆盖。";
        }
        catch (Exception ex)
        {
            AppLog.Error("采集历史导出失败", exception: ex);
            if (!_isShuttingDown)
                PageMessage = $"导出失败：{ex.Message}。原目标文件未被覆盖。";
        }
        finally
        {
            exportFinished = true;
            IsExporting = false;
        }
    }

    /// <summary>有上次成功查询的结果且当前不忙，才允许导出。</summary>
    private bool CanExportRecords() => !IsBusy && HasLoadedRecords && _totalRecords > 0;

    partial void OnSelectedDeviceChanged(DeviceFilterOption? value) => MarkFiltersChanged();

    partial void OnPointNameFilterChanged(string value) => MarkFiltersChanged();
    partial void OnUseTimeFilterChanged(bool value) => MarkFiltersChanged();
    partial void OnStartDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnEndDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnStartTimeTextChanged(string value) => MarkFiltersChanged();
    partial void OnEndTimeTextChanged(string value) => MarkFiltersChanged();

    /// <summary>输入改了只提醒重新查询，不立即查库，也不改变已查询范围。</summary>
    private void MarkFiltersChanged()
    {
        FiltersChanged = true;
    }

    partial void OnFiltersChangedChanged(bool value) => OnPropertyChanged(nameof(FilterStatusText));
    partial void OnIsLoadingChanged(bool value) => NotifyStateChanged();
    partial void OnIsExportingChanged(bool value) => NotifyStateChanged();

    /// <summary>统一刷新页码、提示和按钮能否点击，不执行查询。</summary>
    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditFilters));
        OnPropertyChanged(nameof(HasLoadedRecords));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(RefreshButtonText));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalRecords));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(RecordSummary));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(FilterStatusText));

        RefreshRecordsCommand.NotifyCanExecuteChanged();
        ResetFiltersCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ExportRecordsCommand.NotifyCanExecuteChanged();
        ShowTrendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>退出时禁止新操作，取消导出，再等查询、导出和子窗口操作收尾。</summary>
    public async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        NotifyStateChanged();
        ExportRecordsCommand.Cancel();
        Task?[] tasks = { RefreshRecordsCommand.ExecutionTask, ResetFiltersCommand.ExecutionTask,
            PreviousPageCommand.ExecutionTask, NextPageCommand.ExecutionTask, ExportRecordsCommand.ExecutionTask };
        await Task.WhenAll(tasks.OfType<Task>().Append(Alarms.StopOperationsAsync())
            .Append(_openTrend?.StopOperationsAsync() ?? Task.CompletedTask));
    }
}

/// <summary>读数历史和报警历史共用的时间检查。用户填的是本机时间，数据库比较的是 UTC 时间。</summary>
internal static class HistoryQueryTime
{
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive) ReadRange(
        DateTime? startDate, string startText, DateTime? endDate, string endText)
    {
        DateTime start = ReadLocalTime(startDate, startText, "开始");
        DateTime end = ReadLocalTime(endDate, endText, "结束");
        if (start > end)
            throw new ArgumentException("开始时间不能晚于结束时间。");

        return (ToUtc(start), ToUtc(end.AddSeconds(1)));
    }

    private static DateTime ReadLocalTime(DateTime? date, string text, string label)
    {
        if (!date.HasValue)
            throw new ArgumentException($"请选择{label}日期。");
        if (!TimeSpan.TryParseExact(text?.Trim(), new[] { "hh\\:mm\\:ss", "hh\\:mm" },
                CultureInfo.InvariantCulture, out TimeSpan time) || time.TotalDays >= 1)
            throw new ArgumentException($"{label}时间请写成 HH:mm:ss，例如 08:30:00。");
        return DateTime.SpecifyKind(date.Value.Date + time, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ToUtc(DateTime local)
    {
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new ArgumentException("这个当地时间不存在或有两种解释，请避开夏令时切换时刻。");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
