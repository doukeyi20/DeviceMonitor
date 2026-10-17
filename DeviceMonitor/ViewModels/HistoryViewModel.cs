using System;
using DeviceMonitor.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;

namespace DeviceMonitor.ViewModels;

/// <summary>
/// 读数历史页面的数据和命令：查询、翻页、导出，以及打开点位趋势。
/// 输入条件要点查询才生效，翻页和导出沿用上次已查询的范围。
/// </summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    public const int PageSize = 200; // 每页显示 200 条。
    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;
    private readonly HistoryCsvExporter _csvExporter;
    private readonly HistoryDeviceOption _allDevices = new(null, "全部设备");
    // 上次已查询的条件，和输入框正在编辑的条件分开保存。
    private HistoryFilter? _appliedFilter;
    // 本次查询的最大历史编号，翻页和导出不混入后来新增的记录。
    private long _snapshotId;
    private long _totalRecords; // 本次条件下的总条数，不只是当前页条数。
    private int _currentPage; // 当前页码，无记录时为 0。
    private int _filterVersion; // 判断等待查询时，用户是否又改了筛选输入。
    private int _exportVersion; // 判断进度消息是否仍属于当前这一次导出。
    private bool _updatingDeviceOptions; // 程序刷新下拉选项时，不误判为用户改了条件。
    private bool _hasLoadedRecords; // 已查过一次，切回页面不再自动重查。
    private bool _lastQueryFailed; // 上次查询失败时，保留旧表格，但暂不允许翻页和导出。
    private bool _isShuttingDown; // 退出时禁止新操作，也不再回填结果。
    private HistoryTrendViewModel? _openTrend; // 当前打开的趋势窗口数据，关窗收尾时也要等它。

    public ObservableCollection<HistoryDeviceOption> DeviceOptions { get; } = new(); // 设备筛选下拉框的选项。
    public ObservableCollection<HistoryRecord> Records { get; } = new(); // 历史表当前页，每项是一条已保存的读数。
    // “报警历史”标签的数据源，有自己的查询条件和表格。
    public AlarmHistoryViewModel Alarms { get; }

    // 筛选控件绑定下面这些属性，改完要点击查询才生效。
    [ObservableProperty] private HistoryDeviceOption? _selectedDevice;
    [ObservableProperty] private string _pointNameFilter = string.Empty;
    [ObservableProperty] private bool _useTimeFilter;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTimeText = "00:00:00";
    [ObservableProperty] private string _endTimeText = "23:59:59";
    [ObservableProperty] private bool _filtersChanged; // 输入已改，但还没按新条件查询。
    [ObservableProperty] private bool _isLoading; // 正在查询时限制重复操作。
    [ObservableProperty] private bool _isExporting; // 正在导出时限制查询和翻页。
    // 历史表选中的记录，趋势按其中的设备编号、点位编号查。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowTrendCommand))]
    private HistoryRecord? _selectedRecord;

    // 页码、数量和提示由查询结果计算，供界面显示。
    public int CurrentPage => _currentPage;
    // 页面 Loaded 用它判断是否需要首次查询。
    public bool HasLoadedRecords => _hasLoadedRecords;
    public long TotalRecords => _totalRecords;
    public long TotalPages => _totalRecords == 0 ? 0 : (_totalRecords - 1) / PageSize + 1;
    public bool IsBusy => IsLoading || IsExporting || _isShuttingDown;
    public bool CanEditFilters => !IsBusy;
    public bool ShowEmptyMessage => _hasLoadedRecords && !_lastQueryFailed && !IsBusy && !FiltersChanged && Records.Count == 0;
    public string RefreshButtonText => IsLoading ? "查询中…" : "查询 / 刷新";
    public string RecordSummary => $"本页 {Records.Count} 条 / 匹配 {_totalRecords} 条";
    public string PageDescription => $"第 {CurrentPage} / {TotalPages} 页 · 每页 {PageSize} 条";
    public string FilterStatusText => FiltersChanged
        ? "筛选条件已更改，请重新查询。"
        : string.Empty;

    private string _pageMessage = string.Empty;
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    public event Func<string?>? ExportPathRequested;
    public event Action<HistoryTrendViewModel>? TrendRequested;

    /// <summary>准备历史页面和报警历史标签；首次查询在页面加载后执行。</summary>
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

    /// <summary>
    /// 由 RefreshRecordsCommand 或 ResetFiltersAsync 调用；首次显示页面也会执行此命令。
    /// 按当前输入查询第一页，并把最新保存的记录纳入本次查询。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords()) return;
        try
        {
            // 先检查并固定要查询的条件，得到查询条件对象，再后台查询第一页。
            HistoryFilter filter = BuildFilter();

            await LoadPageAsync(filter, 1, null, refreshDeviceOptions: true);
        }
        catch (Exception ex)
        {
            FiltersChanged = true;
            PageMessage = $"查询条件无效：{ex.Message}";
        }
    }

    /// <summary>
    /// 由 RefreshRecordsCommand、ResetFiltersCommand 判断按钮能不能点时调用。
    /// 没有查询、导出或退出工作时，才允许重新查询。
    /// </summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>
    /// 由 RefreshRecordsAsync 调用。
    /// 把筛选输入装成查询条件；启用时间筛选时，先转成数据库使用的 UTC 时间。
    /// </summary>
    private HistoryFilter BuildFilter()
    {
        // 如果查询条件没有时间范围，就只按设备和点位名称筛选。
        if (!UseTimeFilter) return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter);

        // 如果有，就先调用时间工具类把本机时间和输入的时分秒解析成 UTC 范围，再装成查询条件。
        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter, range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>
    /// 由 RefreshRecordsAsync、PreviousPageAsync 和 NextPageAsync 调用。
    /// 后台查一页记录，成功后更新表格和页码，失败就保留旧结果。
    /// </summary>
    private async Task LoadPageAsync(HistoryFilter filter, int page, long? snapshotId, bool refreshDeviceOptions = false)
    {
        // 标记正在查询，记住输入版本，等待期间改了条件就需要再查。
        IsLoading = true;
        _lastQueryFailed = false;
        int filterVersion = _filterVersion;
        PageMessage = "正在查询历史记录…";
        try
        {
            // 设备选项按需重查，记录每次都查；后台不直接操作界面集合。
            var result = await Task.Run(() =>
            {
                var options = refreshDeviceOptions ? _historyStore.GetDeviceOptions(_currentUser) : null;

                // 真正查找历史记录，返回的是一个HistoryPage 对象
                var data = _historyStore.GetPage(_currentUser, filter, page, PageSize, snapshotId);
                
                return (Options: options, Data: data);
            });
            // 退出后不再把结果填回旧页面。
            if (_isShuttingDown) return;
            if (result.Options is not null)
            {
                // 重建设备选项并按编号恢复选择，这不算用户改条件。
                _updatingDeviceOptions = true;
                try
                {
                    DeviceOptions.Clear();
                    DeviceOptions.Add(_allDevices);

                    foreach (HistoryDeviceOption option in result.Options) DeviceOptions.Add(option);
                    SelectedDevice = filter.DeviceId.HasValue
                        ? DeviceOptions.FirstOrDefault(item => item.DeviceId == filter.DeviceId) : _allDevices;
                    // 这个编号已经没有历史选项时，也保留它作为当前筛选目标。
                    if (filter.DeviceId.HasValue && SelectedDevice is null)
                    {
                        SelectedDevice = new HistoryDeviceOption(filter.DeviceId, "暂无历史的设备");
                        DeviceOptions.Add(SelectedDevice);
                    }
                }
                finally { _updatingDeviceOptions = false; }
            }

            // 查询成功了才替换表格，每个 HistoryRecord 就是表里的一行。
            SelectedRecord = null;
            Records.Clear();
            foreach (HistoryRecord record in result.Data.Records) Records.Add(record);

            // 记住已查询的条件、最大编号和页码，后续翻页、导出沿用它们。
            _appliedFilter = filter;
            _snapshotId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;
            _hasLoadedRecords = true;
            FiltersChanged = filterVersion != _filterVersion;
            PageMessage = _totalRecords == 0 ? "没有匹配的历史记录。"
                : $"查询成功，匹配 {_totalRecords} 条，当前显示第 {_currentPage} 页；最新读数在前。";
        }
        catch (Exception ex)
        {
            AppLog.Error("采集历史查询失败", exception: ex);
            _lastQueryFailed = true;
            PageMessage = $"查询失败：{ex.Message}。已保留上次结果，请检查后重试。";
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// 由 ResetFiltersCommand 调用。
    /// 恢复默认筛选条件，再查询第一页。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task ResetFiltersAsync()
    {
        if (!CanRefreshRecords()) return;
        SelectedDevice = _allDevices;
        PointNameFilter = string.Empty;
        UseTimeFilter = false;
        StartDate = EndDate = DateTime.Today;
        StartTimeText = "00:00:00";
        EndTimeText = "23:59:59";
        await RefreshRecordsAsync();
    }

    /// <summary>
    /// 由 PreviousPageCommand 调用。
    /// 按上次已查询的范围加载上一页。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => CanPreviousPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage - 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 由 PreviousPageCommand 判断按钮能不能点时调用。
    /// 结果有效、条件未改且当前不是第一页，才允许上一页。
    /// </summary>
    private bool CanPreviousPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage > 1;

    /// <summary>
    /// 由 NextPageCommand 调用。
    /// 按同一查询范围加载下一页，不混入后来新增的数据。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage + 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 由 NextPageCommand 判断按钮能不能点时调用。
    /// 结果有效、条件未改且后面还有页，才允许下一页。
    /// </summary>
    private bool CanNextPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage < TotalPages;

    /// <summary>
    /// 由 ShowTrendCommand 调用。
    /// 为选中记录的点位打开趋势窗口；趋势会单独查这个点的历史，不只画当前页。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowTrend))]
    private void ShowTrend()
    {
        if (!CanShowTrend()) return;
        if (TrendRequested is null) { PageMessage = "页面尚未准备好，无法打开趋势窗口。"; return; }
        try
        {
            _openTrend = CreateTrendViewModel(SelectedRecord!);
            ShowTrendCommand.NotifyCanExecuteChanged();
            // HistoryView.xaml.cs 的 OpenTrendWindow 用 _openTrend 打开窗口，关闭后再返回。
            TrendRequested.Invoke(_openTrend);
        }
        catch (Exception ex) { PageMessage = $"打开趋势失败：{ex.Message}"; }
        finally { _openTrend = null; ShowTrendCommand.NotifyCanExecuteChanged(); }
    }

    // 选中了有效记录，且当前没有另一个趋势窗口，才允许打开。
    // 由 ShowTrendCommand 判断按钮能不能点时调用。
    private bool CanShowTrend() => !IsBusy && !FiltersChanged && _hasLoadedRecords && !_lastQueryFailed
        && _openTrend is null && SelectedRecord is not null && Records.Contains(SelectedRecord);

    /// <summary>
    /// 由 ShowTrend 打开趋势窗口前调用。
    /// 把所选点位和本次查询范围交给趋势窗口的数据对象。
    /// </summary>
    public HistoryTrendViewModel CreateTrendViewModel(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (IsBusy || FiltersChanged || _lastQueryFailed || _appliedFilter is null || !Records.Contains(record))
            throw new InvalidOperationException("请先查询历史，再选中当前表格中的一条记录。");
        return new HistoryTrendViewModel(_currentUser, _historyStore, record, _appliedFilter, _snapshotId);
    }

    /// <summary>
    /// 由 ExportRecordsCommand 调用。
    /// 选择文件后，导出本次查询的全部结果，不只当前页。
    /// 工具包生成 ExportRecordsCancelCommand，取消按钮用它发出停止信号。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords()) return;
        // 固定上次已查询的范围和本次导出编号，不重新读取输入框。
        HistoryFilter filter = _appliedFilter!;
        long snapshot = _snapshotId;
        long total = _totalRecords;
        int exportVersion = ++_exportVersion;
        IsExporting = true;
        try
        {
            if (ExportPathRequested is null) throw new InvalidOperationException("页面尚未准备好，无法选择导出文件。");
            // HistoryView.xaml.cs 的 ChooseExportPath 弹出保存窗口，返回路径；取消返回 null。
            string? path = ExportPathRequested.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) { PageMessage = "已取消选择导出文件。"; return; }

            // 后台进度送回界面显示，过期的导出通知不再采用。
            var progress = new Progress<long>(count =>
            {
                if (!_isShuttingDown && IsExporting && exportVersion == _exportVersion)
                    PageMessage = $"正在导出：已写入 {count} / {total} 条，可取消。";
            });

            // 真正的查库和写 CSV 在后台执行，等结束后显示写入条数。
            long written = await Task.Run(() => _csvExporter.Export(_currentUser, filter, snapshot,
                path, cancellationToken, progress), cancellationToken);
            if (!_isShuttingDown) PageMessage = $"已导出 {written} 条记录：{path}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_isShuttingDown) PageMessage = "导出已取消，原目标文件未被覆盖。";
        }
        catch (Exception ex)
        {
            AppLog.Error("采集历史导出失败", exception: ex);
            if (!_isShuttingDown) PageMessage = $"导出失败：{ex.Message}。原目标文件未被覆盖。";
        }
        finally { IsExporting = false; }
    }

    /// <summary>
    /// 由 ExportRecordsCommand 判断按钮能不能点时调用。
    /// 有有效结果、条件未改且当前不忙，才允许导出。
    /// </summary>
    private bool CanExportRecords() => !IsBusy && !FiltersChanged && _hasLoadedRecords && !_lastQueryFailed && _totalRecords > 0;

    /// <summary>
    /// SelectedDevice 改变后自动调用；用户换设备就标记条件已改，程序刷新选项时跳过。
    /// </summary>
    partial void OnSelectedDeviceChanged(HistoryDeviceOption? value) { if (!_updatingDeviceOptions) MarkFiltersChanged(); }
    // 下面这些输入属性变化后也会自动调用对应方法，只标记待查询，不立即查库。
    partial void OnPointNameFilterChanged(string value) => MarkFiltersChanged();
    partial void OnUseTimeFilterChanged(bool value) => MarkFiltersChanged();
    partial void OnStartDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnEndDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnStartTimeTextChanged(string value) => MarkFiltersChanged();
    partial void OnEndTimeTextChanged(string value) => MarkFiltersChanged();

    /// <summary>
    /// 输入改了就递增版本、提醒重新查询，不立即查库。
    /// </summary>
    private void MarkFiltersChanged() { _filterVersion++; FiltersChanged = true; }
    // 条件或忙碌状态变化后，自动刷新界面提示和按钮状态。
    partial void OnFiltersChangedChanged(bool value) => NotifyStateChanged();
    partial void OnIsLoadingChanged(bool value) => NotifyStateChanged();
    partial void OnIsExportingChanged(bool value) => NotifyStateChanged();

    /// <summary>
    /// 由查询、导出或筛选状态变化，以及退出处理调用。
    /// 统一刷新页码、提示和按钮能否点击，不执行查询。
    /// </summary>
    private void NotifyStateChanged()
    {
        // 先刷新界面显示，再刷新按钮状态。
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanEditFilters));
        OnPropertyChanged(nameof(HasLoadedRecords));
        OnPropertyChanged(nameof(ShowEmptyMessage)); OnPropertyChanged(nameof(RefreshButtonText));
        OnPropertyChanged(nameof(CurrentPage)); OnPropertyChanged(nameof(TotalRecords)); OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(RecordSummary)); OnPropertyChanged(nameof(PageDescription)); OnPropertyChanged(nameof(FilterStatusText));

        RefreshRecordsCommand.NotifyCanExecuteChanged(); ResetFiltersCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
        ExportRecordsCommand.NotifyCanExecuteChanged();
        ShowTrendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 由 MainViewModel.StopBackgroundWorkAsync 在退出前调用。
    /// 退出时禁止新操作，取消导出，再等查询、导出和子窗口操作收尾。
    /// </summary>
    public async Task StopOperationsAsync()
    {
        // 先禁止新操作并发出导出取消信号，再收集所有未结束任务一起等待。
        _isShuttingDown = true;
        NotifyStateChanged();
        ExportRecordsCommand.Cancel();
        Task?[] tasks = { RefreshRecordsCommand.ExecutionTask, ResetFiltersCommand.ExecutionTask,
            PreviousPageCommand.ExecutionTask, NextPageCommand.ExecutionTask, ExportRecordsCommand.ExecutionTask };
        await Task.WhenAll(tasks.OfType<Task>().Append(Alarms.StopOperationsAsync())
            .Append(_openTrend?.StopOperationsAsync() ?? Task.CompletedTask));
    }
}
