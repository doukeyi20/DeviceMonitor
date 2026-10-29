// 管理报警历史筛选、分页和 CSV 导出。
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

public sealed record AlarmCategoryOption(AlarmCategory? Category, string DisplayName);

/// <summary>报警历史标签的数据和命令，负责查询、翻页和导出。</summary>
public sealed partial class AlarmHistoryViewModel : ObservableObject
{
    public const int PageSize = 100;

    private readonly UserAccount _currentUser;
    private readonly AlarmStore _alarmStore;
    private readonly AlarmCsvExporter _csvExporter;
    private readonly DeviceFilterOption _allDevices = new(null, "全部设备");

    // 未执行查询的新输入，不改变旧结果的翻页和导出范围。
    private AlarmFilter? _appliedFilter;
    // 固定新增事件的编号范围，但不冻结已有事件后续的状态。
    private long _snapshotId;
    private long _totalRecords;
    private int _currentPage;
    private bool _isShuttingDown;

    public event Func<string?>? ExportPathRequested;

    public ObservableCollection<DeviceFilterOption> DeviceOptions { get; } = new();
    public ObservableCollection<AlarmRecord> Records { get; } = new();
    public AlarmCategoryOption[] CategoryOptions { get; } =
    {
        new(null, "全部类别"),
        new(AlarmCategory.Value, "数值报警"),
        new(AlarmCategory.Acquisition, "采集异常")
    };

    [ObservableProperty] private DeviceFilterOption? _selectedDevice;
    [ObservableProperty] private AlarmCategoryOption? _selectedCategory;
    [ObservableProperty] private bool _useTimeFilter;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTimeText = "00:00:00";
    [ObservableProperty] private string _endTimeText = "23:59:59";
    [ObservableProperty] private bool _filtersChanged;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;

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
        ? "条件已改但尚未查询；翻页和导出仍使用上次查询结果。"
        : string.Empty;

    private string _pageMessage = string.Empty;
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    public AlarmHistoryViewModel(UserAccount currentUser, AlarmStore alarmStore)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _alarmStore = alarmStore ?? throw new ArgumentNullException(nameof(alarmStore));
        _csvExporter = new AlarmCsvExporter(_alarmStore);
        if (_currentUser.Role != UserRole.Administrator || !_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的管理员可以查看报警历史。");
        DeviceOptions.Add(_allDevices);
        _selectedDevice = _allDevices;
        _selectedCategory = CategoryOptions[0];
    }

    /// <summary>按当前筛选输入查询第一页。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords())
            return;
        try
        {
            AlarmFilter filter = BuildFilter();
            await LoadPageAsync(filter, 1, null, refreshDeviceOptions: true);
        }
        catch (Exception ex)
        {
            FiltersChanged = true;
            PageMessage = $"查询条件无效：{ex.Message}";
        }
    }

    /// <summary>当前不忙时，查询和重置按钮才可点。</summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>把输入装成报警查询条件，需要时把本机时间转成 UTC。</summary>
    private AlarmFilter BuildFilter()
    {
        if (!UseTimeFilter)
            return new AlarmFilter(SelectedDevice?.DeviceId, SelectedCategory?.Category);
        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new AlarmFilter(SelectedDevice?.DeviceId, SelectedCategory?.Category,
            range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>后台查询一页报警，成功后更新表格和页码；失败时保留原结果。</summary>
    private async Task LoadPageAsync(AlarmFilter filter, int page, long? snapshotId, bool refreshDeviceOptions = false)
    {
        IsLoading = true;
        PageMessage = "正在查询报警历史…";
        try
        {
            var result = await Task.Run(() =>
            {
                var options = refreshDeviceOptions ? _alarmStore.GetDeviceOptions(_currentUser) : null;
                var data = _alarmStore.GetPage(_currentUser, filter, page, PageSize, snapshotId);
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
                    SelectedDevice = new DeviceFilterOption(filter.DeviceId, "暂无报警历史的设备");
                    DeviceOptions.Add(SelectedDevice);
                }
            }

            Records.Clear();
            foreach (AlarmRecord record in result.Data.Records)
                Records.Add(record);

            _appliedFilter = filter;
            _snapshotId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;

            if (refreshDeviceOptions)
                FiltersChanged = false;
            PageMessage = _totalRecords == 0
                ? "没有匹配的报警记录。"
                : $"查询成功，匹配 {_totalRecords} 条，当前显示第 {_currentPage} 页。";
        }
        catch (Exception ex)
        {
            AppLog.Error("报警历史查询失败", exception: ex);
            PageMessage = $"查询失败：{ex.Message}。已保留上次结果，请重试查询。";
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
        SelectedCategory = CategoryOptions[0];
        UseTimeFilter = false;
        StartDate = EndDate = DateTime.Today;
        StartTimeText = "00:00:00";
        EndTimeText = "23:59:59";
        await RefreshRecordsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => CanPreviousPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage - 1, _snapshotId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且不是第一页，才允许上一页。</summary>
    private bool CanPreviousPage() => !IsBusy && HasLoadedRecords && _currentPage > 1;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage + 1, _snapshotId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且后面还有页，才允许下一页。</summary>
    private bool CanNextPage() => !IsBusy && HasLoadedRecords && _currentPage < TotalPages;

    /// <summary>选择文件后，后台导出本次查询的全部报警，不只当前页。</summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords())
            return;

        AlarmFilter filter = _appliedFilter!;
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
                    PageMessage = $"正在导出报警：已写入 {count} / {total} 条，可取消。";
            });

            long written = await Task.Run(() => _csvExporter.Export(_currentUser, filter, snapshot,
                path, cancellationToken, progress), cancellationToken);
            if (!_isShuttingDown)
                PageMessage = $"已导出 {written} 条报警记录：{path}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_isShuttingDown)
                PageMessage = "导出已取消，原目标文件未被覆盖。";
        }
        catch (Exception ex)
        {
            AppLog.Error("报警历史导出失败", exception: ex);
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
    private bool CanExportRecords() => !IsBusy && HasLoadedRecords
        && _totalRecords > 0;

    partial void OnSelectedDeviceChanged(DeviceFilterOption? value) => MarkFiltersChanged();

    partial void OnSelectedCategoryChanged(AlarmCategoryOption? value) => MarkFiltersChanged();
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

    /// <summary>统一刷新数量、页码、提示和按钮能否点击，不执行查询。</summary>
    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasLoadedRecords));
        OnPropertyChanged(nameof(CanEditFilters));
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
    }

    /// <summary>退出时禁止新操作、取消导出，并等已发起的查询和导出收尾。</summary>
    public async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        NotifyStateChanged();
        ExportRecordsCommand.Cancel();
        Task?[] tasks = { RefreshRecordsCommand.ExecutionTask, ResetFiltersCommand.ExecutionTask,
            PreviousPageCommand.ExecutionTask, NextPageCommand.ExecutionTask, ExportRecordsCommand.ExecutionTask };
        await Task.WhenAll(tasks.OfType<Task>());
    }
}
