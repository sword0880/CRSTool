using CommunityToolkit.Mvvm.ComponentModel;
using CRS.DesktopClient.Services;
using CRS.Maintenance;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    [ObservableProperty] private DateTime? historyStartDate=CRS.Timing.BeijingClock.Today;
    [ObservableProperty] private DateTime? historyEndDate=CRS.Timing.BeijingClock.Today;
    [ObservableProperty] private string historySummary="默认选择北京时间当天，请点击“查询记录”。";
    private DateOnly appliedHistoryStartDate;
    private DateOnly appliedHistoryEndDate;
    private readonly HashSet<(string PluginId,string JobId)> knownHistoryTasks = [(DataCleanupTask.ServiceId,DataCleanupTask.JobId)];
    private bool loadingHistoryOptions;

    partial void OnHistoryPluginChanged(string value)
    {
        UpdateHistoryJobs();
        if (!string.IsNullOrWhiteSpace(value) && !HistoryJobs.Contains(HistoryJob?.Trim() ?? ""))
            HistoryJob = "";
    }

    private void UpdateHistorySuggestions() => RememberHistoryTasks(Jobs.Select(x => (x.PluginId,x.JobId))
        .Concat(History.Select(x => (x.PluginId,x.JobId))).Concat(PendingRequests.Select(x => (x.PluginId,x.JobId))));

    private void RememberHistoryTasks(IEnumerable<(string PluginId,string JobId)> tasks)
    {
        foreach (var task in tasks)
        {
            if (knownHistoryTasks.Add(task) && !HistoryPlugins.Contains(task.PluginId))
                HistoryPlugins.Add(task.PluginId);
        }
        UpdateHistoryJobs();
    }

    private void UpdateHistoryJobs()
    {
        var plugin = HistoryPlugin?.Trim() ?? "";
        var jobs = knownHistoryTasks.Where(x => plugin.Length == 0 || x.PluginId == plugin)
            .Select(x => x.JobId).ToHashSet(StringComparer.Ordinal);
        // 保留仍然可选的项，避免重新绑定列表时清掉有效的任务筛选。
        for (var i = HistoryJobs.Count - 1; i > 0; i--)
            if (!jobs.Contains(HistoryJobs[i])) HistoryJobs.RemoveAt(i);
        foreach (var job in jobs.Order(StringComparer.Ordinal))
            if (!HistoryJobs.Contains(job)) HistoryJobs.Add(job);
    }

    internal async Task LoadHistoryOptionsAsync()
    {
        if (loadingHistoryOptions || lifetime.IsCancellationRequested) return;
        loadingHistoryOptions = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            if (await ReadHistoryOptionsAsync("GetHistoryJobs", deadline.Token) == "UnknownCommand")
            {
                await ReadHistoryOptionsAsync("GetJobs", deadline.Token);
                await ReadHistoryOptionsAsync("GetDiagnosticJobs", deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (BackgroundServiceException) { } // 保留已经知道的任务，仍可手工输入任务编号。
        finally { loadingHistoryOptions = false; }
    }

    private async Task<string> ReadHistoryOptionsAsync(string command, CancellationToken token)
    {
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        while (true)
        {
            var page = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), command, Cursor: cursor), token);
            token.ThrowIfCancellationRequested();
            if (page.Code == "RateLimited") { await Task.Delay(500, token); continue; }
            if (page.Code != "Ok") return page.Code;
            RememberHistoryTasks((page.Jobs ?? []).Select(x => (x.PluginId,x.JobId)));
            cursor = page.NextCursor;
            if (cursor is null || !cursors.Add(cursor)) return "Ok";
        }
    }

    private async Task LoadHistoryAsync()
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var cleanupOnly=appliedPlugin==DataCleanupTask.ServiceId||appliedJob==DataCleanupTask.JobId;
        var (rows,pending)=cleanupOnly?(new List<ExecutionRecord>(),null):await ReadHistoryPagesAsync(deadline.Token);
        var cleanupNote="";
        if((appliedPlugin is null or DataCleanupTask.ServiceId)&&(appliedJob is null or DataCleanupTask.JobId))
        {
            var (maintenanceRows,note)=await ReadCleanupHistoryAsync(deadline.Token);
            rows.AddRange(maintenanceRows);
            cleanupNote=note;
            rows=rows.OrderByDescending(x=>x.StartedUtc).ToList();
        }
        lifetime.ThrowIfCancellationRequested();
        // 收齐整个日期范围后更新，失败不展示半份记录。
        var ids=rows.Select(x=>x.ExecutionId).ToHashSet();
        for(var i=History.Count-1;i>=0;i--)if(!ids.Contains(History[i].ExecutionId))History.RemoveAt(i);
        for(var i=0;i<rows.Count;i++)
        {
            var existing=History.FirstOrDefault(x=>x.ExecutionId==rows[i].ExecutionId);
            if(existing is null)History.Insert(i,rows[i]);
            else{var index=History.IndexOf(existing);if(existing!=rows[i])History[index]=rows[i];if(index!=i)History.Move(index,i);}
        }
        PendingRequests.Clear();foreach(var row in pending??[])PendingRequests.Add(row);
        PendingStatus=cleanupOnly?"正在等待或运行的清理请到「当前任务」查看。":pending is null?"后台尚未提供待执行请求信息。":pending.Count==0?"当前筛选下没有待执行的手动请求。":"当前待执行请求（与记录日期无关）；详情请到“当前任务”查看。";
        HistorySummary=$"{appliedHistoryStartDate:yyyy-MM-dd} 至 {appliedHistoryEndDate:yyyy-MM-dd}（北京时间） · 共 {rows.Count} 条执行记录"+cleanupNote;
        UpdateHistorySuggestions();
    }
    private async Task<(IEnumerable<ExecutionRecord> Rows,string Note)> ReadCleanupHistoryAsync(CancellationToken token)
    {
        (string Code, IReadOnlyList<ExecutionRecord>? History, bool LatestOnly) reply;
        do
        {
            reply=await WorkerDataCleanupGateway.ReadHistoryAsync(client,token);
            if(reply.Code=="RateLimited")await Task.Delay(500,token);
        }while(reply.Code=="RateLimited");
        if(reply.Code=="Forbidden")return([]," · 清理记录需要管理员权限");
        if(reply.Code is "UnknownCommand" or "UnsupportedCommand")return([]," · 当前后台版本尚未提供清理记录");
        if(reply.Code!="Ok")return([]," · 清理记录暂不可用，请稍后重试");
        if(reply.History is not {Count:<=32} history||history.Any(x=>x.PluginId!=DataCleanupTask.ServiceId||x.JobId!=DataCleanupTask.JobId
            ||!x.ExecutionId.StartsWith("cleanup:",StringComparison.Ordinal)||x.EndedUtc is null)
            ||history.Select(x=>x.ExecutionId).Distinct().Count()!=history.Count)
            throw new System.IO.InvalidDataException("后台清理记录格式不一致，请核对前后台版本。");
        return(history.Where(row=>
        {
            var day=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Normalize(row.StartedUtc).DateTime);
            return day>=appliedHistoryStartDate&&day<=appliedHistoryEndDate&&(appliedResultFilter!="Issues"||IsIssueResult(row.Result));
        }),reply.LatestOnly?" · 当前后台仅提供最近一次清理结果":"");
    }
    private async Task<(List<ExecutionRecord> Rows,IReadOnlyList<BackgroundPendingRequest>? Pending)> ReadHistoryPagesAsync(CancellationToken token)
    {
        var rows=new List<ExecutionRecord>();var ids=new HashSet<string>();
        IReadOnlyList<BackgroundPendingRequest>? pending=null;var firstPage=true;
        // 沿用已安装后台的按日接口，逐日收齐所有分页；不在当前页上模拟范围筛选。
        for(var day=appliedHistoryEndDate;;day=day.AddDays(-1))
        {
            var date=day.ToString("yyyy-MM-dd");
            var cursors=new HashSet<string>();string? cursor=null;
            do
            {
                var page=await ReadHistoryPageAsync(date,cursor,token);
                if(firstPage){pending=page.PendingRequests;firstPage=false;}
                foreach(var row in page.History??[])
                {
                    if(DateOnly.FromDateTime(CRS.Timing.BeijingClock.Normalize(row.StartedUtc).DateTime)!=day||
                        appliedResultFilter=="Issues"&&!IsIssueResult(row.Result)||!ids.Add(row.ExecutionId))
                        throw new System.IO.InvalidDataException("后台执行记录与查询条件不一致，请核对前后台版本。");
                    rows.Add(row);
                }
                cursor=page.NextCursor;
                if(rows.Count>4096)throw new System.IO.InvalidDataException("记录数量超出单次查询上限，请缩小日期范围。");
                if(cursor is not null&&!cursors.Add(cursor))throw new System.IO.InvalidDataException("执行记录分页无效。");
            }while(cursor is not null);
            if(day==appliedHistoryStartDate)break;
        }
        return(rows.OrderByDescending(x=>x.StartedUtc).ToList(),pending);
    }
    private async Task<BackgroundResponse> ReadHistoryPageAsync(string date,string? cursor,CancellationToken token)
    {
        while(true)
        {
            var reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryByDate",PluginId:appliedPlugin,
                JobId:appliedJob,ResultFilter:appliedResultFilter,Payload:date,Cursor:cursor,PageSize:50),token);
            if(reply.Code=="RateLimited"){await Task.Delay(500,token);continue;}
            if(reply.Code=="UnknownCommand")throw new System.IO.InvalidDataException("后台尚不支持按日期查询执行记录，请更新配套后台服务。");
            if(reply.Code!="Ok")throw new InvalidOperationException("执行记录读取失败："+reply.Code);
            if(reply.Payload!=date||reply.AppliedResultFilter!=appliedResultFilter)
                throw new System.IO.InvalidDataException("后台未确认日期或异常筛选，请更新配套后台服务。");
            return reply;
        }
    }
}
