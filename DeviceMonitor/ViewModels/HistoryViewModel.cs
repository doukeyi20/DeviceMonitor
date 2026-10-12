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
/// 采集读数历史的数据源：先保存筛选输入，点查询才生成固定条件；翻页和导出沿用查过的范围。
/// 页面首次 Loaded 查询，不在构造函数中查库；选择文件和开趋势窗口交给页面后台，本类不启动采集。
/// </summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    public const int PageSize = 200; // 每页 200 条，不再是只能看最近 200 条。
    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;
    private readonly HistoryCsvExporter _csvExporter;
    private readonly HistoryDeviceOption _allDevices = new(null, "全部设备");
    // 上次查询真正使用的条件；输入框后来改了，不等于已经重新查询。
    private HistoryFilter? _appliedFilter;
    // 本次查询允许的最大历史编号；翻页/导出不混入后来新增的记录，不是数据库结构版本。
    private long _snapshotId;
    private long _totalRecords;
    private int _currentPage;
    private int _filterVersion; // 判断等待查询时，用户是否又改了筛选输入。
    private int _exportVersion; // 判断进度消息是否仍属于当前这一次导出。
    private bool _updatingDeviceOptions;
    private bool _hasLoadedRecords;
    private bool _lastQueryFailed;
    private bool _isShuttingDown;
    private HistoryTrendViewModel? _openTrend;

    public ObservableCollection<HistoryDeviceOption> DeviceOptions { get; } = new();
    public ObservableCollection<HistoryRecord> Records { get; } = new();
    // 现有历史入口下的第二个标签。它独立保存报警筛选条件，不和读数查询混在一起。
    public AlarmHistoryViewModel Alarms { get; }

    // 下面是输入框绑定的条件；只改变输入不会立即查库，要点击“查询 / 刷新”。
    [ObservableProperty] private HistoryDeviceOption? _selectedDevice;
    [ObservableProperty] private string _pointNameFilter = string.Empty;
    [ObservableProperty] private bool _useTimeFilter;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTimeText = "00:00:00";
    [ObservableProperty] private string _endTimeText = "23:59:59";
    [ObservableProperty] private bool _filtersChanged;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;
    // 表格选中一行后保存这里；趋势按该行的设备/点位编号查，不按名称猜。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowTrendCommand))]
    private HistoryRecord? _selectedRecord;

    // 这些只读信息来自已查询的结果，不能让输入框随便修改，所以保留手写 getter。
    public int CurrentPage => _currentPage;
    // 页面切走再回来时用它判断：已有结果就保留，不自动清掉选择、页码和查询批次。
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
        ? "条件已修改，请点击查询；当前表格仍是上次结果，暂不能翻页或导出。"
        : "翻页和导出使用本次已查询的结果；新采集的数据请点击刷新。";

    private string _pageMessage = "进入页面后自动查询。可按设备、点位名称和本机时间筛选。";
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    public event Func<string?>? ExportPathRequested;
    public event Action<HistoryTrendViewModel>? TrendRequested;

    /// <summary>MainViewModel 创建，接住同一个账户和 HistoryStore；不在构造函数里查库。</summary>
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
    /// 页面 Loaded 或查询按钮执行 RefreshRecordsCommand 后调用。
    /// 先把输入整理成固定条件，再查第一页；刷新会把查询之后的新采集记录包含进来。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords()) return;
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

    /// <summary>
    /// 查询/重置命令判断按钮能不能点时调用。没有查询、导出或退出工作正在进行才允许。
    /// </summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>
    /// 刷新命令调用：把输入整理成固定的查询条件；需要时将本机日期时间转成 UTC。
    /// 条件对象交给 LoadPageAsync；改输入不会直接修改已经应用的旧条件。
    /// </summary>
    private HistoryFilter BuildFilter()
    {
        if (!UseTimeFilter) return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter);
        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter, range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>
    /// 查询和翻页共用这里。后台返回普通列表；await 后才更新界面集合和页码。
    /// 查失败时保留旧结果，不先 Clear；设备选项只在重新查询时刷新。
    /// </summary>
    private async Task LoadPageAsync(HistoryFilter filter, int page, long? snapshotId, bool refreshDeviceOptions = false)
    {
        IsLoading = true;
        _lastQueryFailed = false;
        int filterVersion = _filterVersion;
        PageMessage = "正在查询历史记录…";
        try
        {
            var result = await Task.Run(() =>
            {
                var options = refreshDeviceOptions ? _historyStore.GetDeviceOptions(_currentUser) : null;
                var data = _historyStore.GetPage(_currentUser, filter, page, PageSize, snapshotId);
                return (Options: options, Data: data);
            });
            // 退出登录已经在等这次查询，不再把旧账户的结果写回离开的页面。
            if (_isShuttingDown) return;
            if (result.Options is not null)
            {
                // 换下拉框对象时不当作用户修改条件，按设备编号恢复同一个筛选目标。
                _updatingDeviceOptions = true;
                try
                {
                    DeviceOptions.Clear();
                    DeviceOptions.Add(_allDevices);
                    foreach (HistoryDeviceOption option in result.Options) DeviceOptions.Add(option);
                    SelectedDevice = filter.DeviceId.HasValue
                        ? DeviceOptions.FirstOrDefault(item => item.DeviceId == filter.DeviceId) : _allDevices;
                    if (filter.DeviceId.HasValue && SelectedDevice is null)
                    {
                        SelectedDevice = new HistoryDeviceOption(filter.DeviceId, "暂无历史的设备");
                        DeviceOptions.Add(SelectedDevice);
                    }
                }
                finally { _updatingDeviceOptions = false; }
            }
            SelectedRecord = null;
            Records.Clear();
            foreach (HistoryRecord record in result.Data.Records) Records.Add(record);
            _appliedFilter = filter;
            _snapshotId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;
            _hasLoadedRecords = true;
            FiltersChanged = filterVersion != _filterVersion;
            PageMessage = _totalRecords == 0 ? "当前条件下没有历史记录。可调整条件或等待采集成功保存后再查询。"
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

    /// <summary>重置按钮调用，恢复全部设备/全部时间，清空点位名称，并重新查询第一页。</summary>
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

    /// <summary>上一页按钮调用，沿用已查询条件和最大记录编号，不读取新输入框的条件。</summary>
    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => CanPreviousPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage - 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 上一页按钮条件：有已查结果、条件未改、上次查询没失败，而且当前不是第一页。
    /// </summary>
    private bool CanPreviousPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage > 1;

    /// <summary>下一页按钮调用，读取同一批结果的下一页；新数据等点击查询再纳入。</summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage + 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 下一页按钮条件：有已查结果、条件未改、上次查询没失败，而且后面还有页。
    /// </summary>
    private bool CanNextPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage < TotalPages;

    /// <summary>
    /// 点击“点位趋势”后调用。先准备所选点位的数据源，再通过事件请后台打开窗口。
    /// 不把当前页的 200 条直接画成曲线；子窗口会按同一批查询范围重新取这个点的历史。
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
            // 调用 HistoryView.xaml.cs 的 OpenTrendWindow：
            // 把刚准备好的 _openTrend 作为数据源，打开 HistoryTrendWindow；窗口关闭后才回到这里。
            TrendRequested.Invoke(_openTrend);
        }
        catch (Exception ex) { PageMessage = $"打开趋势失败：{ex.Message}"; }
        finally { _openTrend = null; ShowTrendCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanShowTrend() => !IsBusy && !FiltersChanged && _hasLoadedRecords && !_lastQueryFailed
        && _openTrend is null && SelectedRecord is not null && Records.Contains(SelectedRecord);

    /// <summary>ShowTrend 创建子窗口的数据源时调用；沿用账户、Store、已经应用的条件和记录快照。</summary>
    public HistoryTrendViewModel CreateTrendViewModel(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (IsBusy || FiltersChanged || _lastQueryFailed || _appliedFilter is null || !Records.Contains(record))
            throw new InvalidOperationException("请先查询历史，再选中当前表格中的一条记录。");
        return new HistoryTrendViewModel(_currentUser, _historyStore, record, _appliedFilter, _snapshotId);
    }

    /// <summary>
    /// 导出按钮调用。先触发事件让后台选择路径，再在后台导出已查询条件的全部结果，不只当前页。
    /// 工具还会生成 ExportRecordsCancelCommand，取消按钮用它通知这里的 cancellationToken。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords()) return;
        HistoryFilter filter = _appliedFilter!;
        long snapshot = _snapshotId;
        long total = _totalRecords;
        int exportVersion = ++_exportVersion;
        IsExporting = true;
        try
        {
            if (ExportPathRequested is null) throw new InvalidOperationException("页面尚未准备好，无法选择导出文件。");
            // 调用 HistoryView.xaml.cs 的 ChooseExportPath：弹出保存文件窗口。
            // 选好后把完整路径返回给 path；点取消则返回 null，这一步还没有开始导出数据。
            string? path = ExportPathRequested.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) { PageMessage = "已取消选择导出文件。"; return; }
            // 进度会排队回到界面线程；旧导出晚到的通知不能盖住新一轮提示。
            var progress = new Progress<long>(count =>
            {
                if (!_isShuttingDown && IsExporting && exportVersion == _exportVersion)
                    PageMessage = $"正在导出：已写入 {count} / {total} 条，可取消。";
            });
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
    /// 导出按钮条件：有非空的已查结果，条件未改且没有忙碌或查询失败；不导出尚未查询的新输入。
    /// </summary>
    private bool CanExportRecords() => !IsBusy && !FiltersChanged && _hasLoadedRecords && !_lastQueryFailed && _totalRecords > 0;

    /// <summary>
    /// 自动入口：生成属性 SelectedDevice 的 setter 调用。用户改设备则标记条件已改；程序刷新下拉选项时暂不标记。
    /// </summary>
    partial void OnSelectedDeviceChanged(HistoryDeviceOption? value) { if (!_updatingDeviceOptions) MarkFiltersChanged(); }
    partial void OnPointNameFilterChanged(string value) => MarkFiltersChanged();
    partial void OnUseTimeFilterChanged(bool value) => MarkFiltersChanged();
    partial void OnStartDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnEndDateChanged(DateTime? value) => MarkFiltersChanged();
    partial void OnStartTimeTextChanged(string value) => MarkFiltersChanged();
    partial void OnEndTimeTextChanged(string value) => MarkFiltersChanged();

    /// <summary>
    /// 筛选输入的自动 On...Changed 回调调用。增加输入版本号，标记待重新查询，不在这里查数据库。
    /// </summary>
    private void MarkFiltersChanged() { _filterVersion++; FiltersChanged = true; }
    partial void OnFiltersChangedChanged(bool value) => NotifyStateChanged();
    partial void OnIsLoadingChanged(bool value) => NotifyStateChanged();
    partial void OnIsExportingChanged(bool value) => NotifyStateChanged();

    /// <summary>
    /// 输入/查询/导出状态变化后调用，通知页码、总数、空结果提示和按钮条件更新；不会替用户执行查询。
    /// </summary>
    private void NotifyStateChanged()
    {
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

    /// <summary>主窗口关闭时调用：取消导出，并等查询和导出真正结束，再让应用退出。</summary>
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
