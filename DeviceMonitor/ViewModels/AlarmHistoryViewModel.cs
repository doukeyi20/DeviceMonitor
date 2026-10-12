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

// 类别下拉框的一项；null 表示“全部”，不是一种新的报警类型。
public sealed record AlarmCategoryOption(AlarmCategory? Category, string DisplayName);

/// <summary>
/// 报警历史的数据源：先保存筛选输入，点查询才生成固定条件；翻页和导出沿用查过的范围。
/// 页面首次 Loaded 查询，不在构造函数中查库；选择文件交给页面后台，本类不启动采集。
/// </summary>
public sealed partial class AlarmHistoryViewModel : ObservableObject
{
    public const int PageSize = 100;
    private readonly UserAccount _currentUser;
    private readonly AlarmStore _alarmStore;
    private readonly AlarmCsvExporter _csvExporter;
    private readonly HistoryDeviceOption _allDevices = new(null, "全部设备");
    private AlarmFilter? _appliedFilter; // 上次已查询的条件，不是尚未提交的输入框内容。
    private long _snapshotId; // 本次查询的最大报警编号；不含后来新增的报警，旧报警状态仍可能更新。
    private long _totalRecords;
    private int _currentPage;
    private int _filterVersion;
    private int _exportVersion;
    private bool _updatingDeviceOptions;
    private bool _hasLoadedRecords;
    private bool _lastQueryFailed;
    private bool _isShuttingDown;

    public event Func<string?>? ExportPathRequested;

    // 设备选项来自报警历史；设备后来被删除，也能用原编号查到以前的报警。
    public ObservableCollection<HistoryDeviceOption> DeviceOptions { get; } = new();
    public ObservableCollection<AlarmRecord> Records { get; } = new();
    public AlarmCategoryOption[] CategoryOptions { get; } =
    {
        new(null, "全部类别"),
        new(AlarmCategory.Value, "数值报警"),
        new(AlarmCategory.Acquisition, "采集异常")
    };

    [ObservableProperty] private HistoryDeviceOption? _selectedDevice;
    [ObservableProperty] private AlarmCategoryOption? _selectedCategory;
    [ObservableProperty] private bool _useTimeFilter;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private string _startTimeText = "00:00:00";
    [ObservableProperty] private string _endTimeText = "23:59:59";
    [ObservableProperty] private bool _filtersChanged;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;

    // 这些值由查询结果计算，不让输入框写入，所以保留普通的只读属性。
    public int CurrentPage => _currentPage;
    // 切回标签时保留已经查过的结果；更新数据由“查询 / 刷新”明确发起。
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
        ? "条件已修改，请点击查询；当前仍显示上次结果，暂不能翻页或导出。"
        : "翻页沿用本次查询条件；新发生或刚恢复的报警请点击刷新。";
    public string StateExplanation => "“未恢复”只表示这条历史报警还没有恢复时间，不代表设备现在一定在线、离线或仍在采集。";
    public string ExportExplanation => "导出全部匹配页，不含查询后新增的报警；状态取各条导出读取时的值，不是整份文件同一时刻的状态快照。";

    private string _pageMessage = "进入标签后自动查询。可以按设备、类别和报警发生时间筛选。";
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    /// <summary>
    /// 主界面创建后，交给报警历史标签作为 DataContext。
    /// 这里先准备筛选选项；真正的第一次查询由 AlarmHistoryView.Loaded 调用命令。
    /// </summary>
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

    /// <summary>标签加载或点击查询后调用。先固定输入条件，再到后台查第一页。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords()) return;
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

    /// <summary>
    /// 查询/重置命令判断按钮能不能点时调用。没有查询、导出或退出工作正在进行才允许。
    /// </summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>
    /// 刷新命令调用：把输入整理成固定的查询条件；需要时将本机日期时间转成 UTC。
    /// 条件对象交给 LoadPageAsync；改输入不会直接修改已经应用的旧条件。
    /// </summary>
    private AlarmFilter BuildFilter()
    {
        if (!UseTimeFilter)
            return new AlarmFilter(SelectedDevice?.DeviceId, SelectedCategory?.Category);
        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new AlarmFilter(SelectedDevice?.DeviceId, SelectedCategory?.Category,
            range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>
    /// 查询、上一页、下一页共用这里。后台只返回普通数据；await 回到界面线程再更新表格。
    /// 查失败不清空表格，保留原结果并说明原因。
    /// </summary>
    private async Task LoadPageAsync(AlarmFilter filter, int page, long? snapshotId, bool refreshDeviceOptions = false)
    {
        IsLoading = true;
        _lastQueryFailed = false;
        int filterVersion = _filterVersion;
        PageMessage = "正在查询报警历史…";
        try
        {
            var result = await Task.Run(() =>
            {
                var options = refreshDeviceOptions ? _alarmStore.GetDeviceOptions(_currentUser) : null;
                var data = _alarmStore.GetPage(_currentUser, filter, page, PageSize, snapshotId);
                return (Options: options, Data: data);
            });
            // 关窗后等查询结束即可，不再把结果写回已经退出的页面。
            if (_isShuttingDown) return;

            if (result.Options is not null)
            {
                // 刷新下拉框会换成新对象，按编号找回原来的选择，不把它误当成用户改条件。
                long? selectedId = SelectedDevice?.DeviceId;
                _updatingDeviceOptions = true;
                try
                {
                    DeviceOptions.Clear();
                    DeviceOptions.Add(_allDevices);
                    foreach (HistoryDeviceOption option in result.Options) DeviceOptions.Add(option);
                    SelectedDevice = selectedId.HasValue
                        ? DeviceOptions.FirstOrDefault(item => item.DeviceId == selectedId) : _allDevices;
                    if (selectedId.HasValue && SelectedDevice is null)
                    {
                        SelectedDevice = new HistoryDeviceOption(selectedId, "暂无报警历史的设备");
                        DeviceOptions.Add(SelectedDevice);
                    }
                }
                finally { _updatingDeviceOptions = false; }
            }

            Records.Clear();
            foreach (AlarmRecord record in result.Data.Records) Records.Add(record);
            _appliedFilter = filter;
            _snapshotId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;
            _hasLoadedRecords = true;
            FiltersChanged = filterVersion != _filterVersion;
            PageMessage = _totalRecords == 0
                ? "当前条件下没有报警记录。正常采集不会生成报警，单次测试也不生成报警。"
                : $"查询成功，匹配 {_totalRecords} 条，当前显示第 {_currentPage} 页。";
        }
        catch (Exception ex)
        {
            AppLog.Error("报警历史查询失败", exception: ex);
            _lastQueryFailed = true;
            PageMessage = $"查询失败：{ex.Message}。已保留上次结果，请重试查询。";
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    /// <summary>重置按钮调用：恢复全部设备、全部类别、全部时间，然后查询第一页。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task ResetFiltersAsync()
    {
        if (!CanRefreshRecords()) return;
        SelectedDevice = _allDevices;
        SelectedCategory = CategoryOptions[0];
        UseTimeFilter = false;
        StartDate = EndDate = DateTime.Today;
        StartTimeText = "00:00:00";
        EndTimeText = "23:59:59";
        await RefreshRecordsAsync();
    }

    // 翻页只使用已经查过的条件和最大记录编号；不会把查询后新增的记录突然插进中间。
    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private Task PreviousPageAsync() => CanPreviousPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage - 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 上一页按钮条件：有已查结果、条件未改、上次查询没失败，而且当前不是第一页。
    /// </summary>
    private bool CanPreviousPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage > 1;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_appliedFilter!, _currentPage + 1, _snapshotId) : Task.CompletedTask;

    /// <summary>
    /// 下一页按钮条件：有已查结果、条件未改、上次查询没失败，而且后面还有页。
    /// </summary>
    private bool CanNextPage() => !IsBusy && !FiltersChanged && !_lastQueryFailed && _hasLoadedRecords && _currentPage < TotalPages;

    /// <summary>
    /// “导出全部结果”按钮调用。先请 View 后台选择文件，再到后台线程分批查库、写 CSV。
    /// IncludeCancelCommand 会生成 ExportRecordsCancelCommand，把取消请求传给下面的 token。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords()) return;
        // 记住的是上次查询的条件和最大编号，不能重新读取尚未点击查询的输入框。
        AlarmFilter filter = _appliedFilter!;
        long snapshot = _snapshotId;
        long total = _totalRecords;
        int exportVersion = ++_exportVersion;
        IsExporting = true;
        try
        {
            if (ExportPathRequested is null)
                throw new InvalidOperationException("页面尚未准备好，无法选择导出文件。");
            // 调用 AlarmHistoryView.xaml.cs 的 ChooseExportPath：弹出保存文件窗口。
            // 选好后把完整路径返回给 path；点取消则返回 null，真正写 CSV 在后面的导出器里。
            string? path = ExportPathRequested.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                PageMessage = "已取消选择导出文件。";
                return;
            }

            // Progress 把数量送回界面线程；过期的通知不能盖住下一次导出的提示。
            var progress = new Progress<long>(count =>
            {
                if (!_isShuttingDown && IsExporting && exportVersion == _exportVersion)
                    PageMessage = $"正在导出报警：已写入 {count} / {total} 条，可取消。";
            });
            long written = await Task.Run(() => _csvExporter.Export(_currentUser, filter, snapshot,
                path, cancellationToken, progress), cancellationToken);
            if (!_isShuttingDown)
                PageMessage = $"已导出 {written} 条报警记录：{path}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_isShuttingDown) PageMessage = "导出已取消，原目标文件未被覆盖。";
        }
        catch (Exception ex)
        {
            AppLog.Error("报警历史导出失败", exception: ex);
            if (!_isShuttingDown) PageMessage = $"导出失败：{ex.Message}。原目标文件未被覆盖。";
        }
        finally { IsExporting = false; }
    }

    /// <summary>
    /// 导出按钮条件：有非空的已查结果，条件未改且没有忙碌或查询失败；不导出尚未查询的新输入。
    /// </summary>
    private bool CanExportRecords() => !IsBusy && !FiltersChanged && _hasLoadedRecords
        && !_lastQueryFailed && _totalRecords > 0;

    /// <summary>
    /// 自动入口：生成属性 SelectedDevice 的 setter 调用。用户改设备则标记条件已改；程序刷新下拉选项时暂不标记。
    /// </summary>
    partial void OnSelectedDeviceChanged(HistoryDeviceOption? value) { if (!_updatingDeviceOptions) MarkFiltersChanged(); }
    partial void OnSelectedCategoryChanged(AlarmCategoryOption? value) => MarkFiltersChanged();
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

    /// <summary>关闭程序或退出登录时调用：取消导出，等查询、翻页和文件清理都结束后再退出。</summary>
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
