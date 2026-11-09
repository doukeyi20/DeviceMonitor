// 管理采集历史的条件查询、分页和 CSV 导出。
using DeviceMonitor.Services;
using System.Globalization;
using System;
using DeviceMonitor;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceMonitor.Model;

namespace DeviceMonitor.ViewModel;

/// <summary>查询得到一批历史结果，翻页和导出继续使用这次查询的范围。</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    public const int PageSize = 200;

    private readonly UserAccount _currentUser;
    private readonly HistoryStore _historyStore;

    // Csv导出器
    private readonly HistoryCsvExporter _csvExporter;
    //下拉框选项中的写死的全部设备选项
    private readonly DeviceFilterOption _allDevices = new(null, "全部设备");

    // 查询条件
    private HistoryFilter? _queryFilter;

    // 本次查询允许的最大记录编号,记录编号不等于记录数量，只是每条记录的编号，删除后不会排序
    // 这个值在查询时就确定了，后续翻页和导出都用这个值，避免混入后来新增的记录。也就是防止正在采集时查询记录导致的变化
    private long _queryMaxRecordId;
    // 本次查询的记录数量
    private long _totalRecords;
    // 当前页码
    private int _currentPage;

    // 采集历史页的退出标记：收尾开始时打上，禁止新查询、翻页、导出，不再把晚返回的查询结果填入列表。
    // 旧任务仍要等它结束；这个页面对象以后不再使用，所以不清回 false。
    private bool _isShuttingDown;

    // 设备下拉框选项保存选中的设备和当前页查询结果列表
    public ObservableCollection<DeviceFilterOption> DeviceOptions { get; } = new();
    public ObservableCollection<HistoryRecord> Records { get; } = new();

    public AlarmHistoryViewModel Alarms { get; }

    // 当前选中的设备筛选项；null 表示“全部设备”
    [ObservableProperty] private DeviceFilterOption? _selectedDevice;

    // 点位名称输入框的文字
    [ObservableProperty] private string _pointNameFilter = string.Empty;

    // 勾选才使用起止时间；重置时清掉，查询全部时间。
    [ObservableProperty] private bool _useTimeFilter;

    [ObservableProperty] private DateTime? _startDate = DateTime.Today;

    [ObservableProperty] private DateTime? _endDate = DateTime.Today;

    [ObservableProperty] private string _startTimeText = "00:00:00";

    [ObservableProperty] private string _endTimeText = "23:59:59";

    // 正在查询或翻页取数的标记：查询前打上，完成或失败后在 finally 清掉，避免查询没回来又发起一轮。
    [ObservableProperty] private bool _isLoading;

    // 正在导出采集历史的标记：导出开始时打上，完成、失败或取消后清掉；期间挡住重复导出和修改筛选。
    [ObservableProperty] private bool _isExporting;

    public int CurrentPage => _currentPage;

    public bool HasQueried => _queryFilter is not null;
    public long TotalRecords => _totalRecords;
    public long TotalPages => _totalRecords == 0 ? 0 : (_totalRecords - 1) / PageSize + 1;
    public bool IsBusy => IsLoading || IsExporting || _isShuttingDown;
    public bool CanEditFilters => !IsBusy;
    public bool ShowEmptyMessage => HasQueried && !IsBusy && Records.Count == 0;
    public string RefreshButtonText => IsLoading ? "查询中…" : "查询 / 刷新";
    public string RecordSummary => $"本页 {Records.Count} 条 / 匹配 {_totalRecords} 条";
    public string PageDescription => $"第 {CurrentPage} / {TotalPages} 页 · 每页 {PageSize} 条";

    private string _pageMessage = string.Empty;
    public string PageMessage
    {
        get => _pageMessage;
        private set => SetProperty(ref _pageMessage, value);
    }

    public event Func<string?>? ExportPathRequested;

    public HistoryViewModel(UserAccount currentUser, HistoryStore historyStore, AlarmStore? alarmStore = null) : base("历史记录")
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        if (!_currentUser.IsEnabled)
            throw new UnauthorizedAccessException("只有已启用的账户可以进入历史记录。");
        _csvExporter = new HistoryCsvExporter(historyStore);
        Alarms = new AlarmHistoryViewModel(currentUser, alarmStore ?? new AlarmStore(historyStore.DatabasePath));
       
        // 默认选中全部设备
        DeviceOptions.Add(_allDevices);
        _selectedDevice = _allDevices;
    }

    /// <summary>
    /// 整理好查询条件，数据库只查第一页
    /// </summary>
    /// <returns></returns>
    [RelayCommand(CanExecute = nameof(CanRefreshRecords))]
    private async Task RefreshRecordsAsync()
    {
        if (!CanRefreshRecords())
            return;
        try
        {
            HistoryFilter filter = BuildFilter();
            // 只查第一页，给最大编号传入null，得到本次查询的最大记录编号，后续翻页和导出都用这个值，避免混入后来新增的记录。
            await LoadPageAsync(filter, 1, null, refreshDeviceOptions: true);
        }
        catch (Exception ex)
        {
            PageMessage = $"查询条件无效：{ex.Message}";
        }
    }

    /// <summary>没有查询、导出或退出工作时，才允许重新查询。</summary>
    private bool CanRefreshRecords() => !IsBusy;

    /// <summary>把筛选输入装成查询条件；启用时间筛选时，先转成数据库使用的 UTC 时间。</summary>
    private HistoryFilter BuildFilter()
    {
        // 没勾选时间筛选
        if (!UseTimeFilter)
            return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter);

        // 勾选了时间筛选
        var range = HistoryQueryTime.ReadRange(StartDate, StartTimeText, EndDate, EndTimeText);
        return new HistoryFilter(SelectedDevice?.DeviceId, PointNameFilter, range.StartUtc, range.EndUtcExclusive);
    }

    /// <summary>
    /// 真正取数据，并填入页面，但是只查指定的一页
    /// 这里说是分页，其实是跳过（当前页数-1）*每页数量条，然后接着查每页数量，就能得到指定页数
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="page">决定查哪页，跟前端选择第几页有关，就是数据库的分页查询条件</param>
    /// <param name="queryMaxRecordId"></param>
    /// <param name="refreshDeviceOptions"></param>
    /// <returns></returns>
    private async Task LoadPageAsync(HistoryFilter filter, int page, long? queryMaxRecordId, bool refreshDeviceOptions = false)
    {
        IsLoading = true;
        PageMessage = "正在查询历史记录…";
        try
        {
            var result = await Task.Run(() =>
            {
                // 查有哪些设备，供下拉框使用
                var options = refreshDeviceOptions ? _historyStore.GetDeviceOptions(_currentUser) : null;

                // 真正查指定页数据，得到的是HistroyPage对象，存储的不仅有历史记录，还有总数、页码和最大记录编号
                var data = _historyStore.GetPage(_currentUser, filter, page, PageSize, queryMaxRecordId);

                return (Options: options, Data: data);
            });

            if (_isShuttingDown)
                return;

            // 如果刷新了设备列表，就替换下拉框选项；否则保留原来的显示。
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

            // 把这一页记录放到表格历史记录集合，是HistoryRecord对象，这是一条历史记录
            Records.Clear();
            foreach (HistoryRecord record in result.Data.Records)
                Records.Add(record);

            _queryFilter = filter;
            _queryMaxRecordId = result.Data.SnapshotId;
            _totalRecords = result.Data.TotalRecords;
            _currentPage = _totalRecords == 0 ? 0 : result.Data.PageNumber;

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

    /// <summary>
    /// 重置条件按钮，恢复默认查询条件然后查询第一页
    /// </summary>
    /// <returns></returns>
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
        ? LoadPageAsync(_queryFilter!,
                        _currentPage - 1,
                        _queryMaxRecordId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且不是第一页，才允许上一页。</summary>
    private bool CanPreviousPage() => !IsBusy && HasQueried && _currentPage > 1;

    /// <summary>按同一查询范围加载下一页，不混入后来新增的数据。</summary>
    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => CanNextPage()
        ? LoadPageAsync(_queryFilter!, _currentPage + 1, _queryMaxRecordId) : Task.CompletedTask;

    /// <summary>有上次查询结果、当前不忙且后面还有页，才允许下一页。</summary>
    private bool CanNextPage() => !IsBusy && HasQueried && _currentPage < TotalPages;

    /// <summary>导出Csv。</summary>
    [RelayCommand(CanExecute = nameof(CanExportRecords), IncludeCancelCommand = true)]
    private async Task ExportRecordsAsync(CancellationToken cancellationToken)
    {
        if (!CanExportRecords())
            return;

        // 查询条件
        HistoryFilter filter = _queryFilter!;
        // 限制的最大记录编号，避免导出时混入后来新增的数据
        long queryMaxRecordId = _queryMaxRecordId;
        IsExporting = true;
        try
        {
            // 保存路径只需要一个结果：调用最后接上的界面，避免连续弹窗，后一次取消覆盖前一次路径。
            var choosePath = ExportPathRequested?.GetInvocationList().LastOrDefault() as Func<string?>;
            if (choosePath is null)
                throw new InvalidOperationException("页面尚未准备好，无法选择导出文件。");
            // 打开路径选择窗口，传回指定的路径
            string? path = choosePath.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                PageMessage = "已取消选择导出文件。";
                return;
            }

            PageMessage = "正在导出历史记录，可取消…";
            // 真正查询并导出到 CSV 文件
            long written = await Task.Run(() => _csvExporter.Export(_currentUser, filter, queryMaxRecordId,
                path, cancellationToken), cancellationToken);
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
            IsExporting = false;
        }
    }

    /// <summary>有上次成功查询的结果且当前不忙，才允许导出。</summary>
    private bool CanExportRecords() => !IsBusy && HasQueried && _totalRecords > 0;

    partial void OnIsLoadingChanged(bool value) => NotifyStateChanged();
    partial void OnIsExportingChanged(bool value) => NotifyStateChanged();

    /// <summary>统一刷新页码、提示和按钮能否点击，不执行查询。</summary>
    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEditFilters));
        OnPropertyChanged(nameof(HasQueried));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(RefreshButtonText));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalRecords));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(RecordSummary));
        OnPropertyChanged(nameof(PageDescription));

        RefreshRecordsCommand.NotifyCanExecuteChanged();
        ResetFiltersCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ExportRecordsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>退出时禁止新操作，取消导出，再等读数与报警历史的查询、导出收尾。</summary>
    public override async Task StopOperationsAsync()
    {
        _isShuttingDown = true;
        NotifyStateChanged();
        ExportRecordsCommand.Cancel();
        Task?[] tasks = { RefreshRecordsCommand.ExecutionTask, ResetFiltersCommand.ExecutionTask,
            PreviousPageCommand.ExecutionTask, NextPageCommand.ExecutionTask, ExportRecordsCommand.ExecutionTask };
        await Task.WhenAll(tasks.OfType<Task>().Append(Alarms.StopOperationsAsync()));
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
