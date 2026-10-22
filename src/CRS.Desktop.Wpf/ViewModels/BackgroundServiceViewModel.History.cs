using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    [ObservableProperty] private DateTime? historyDate=CRS.Timing.BeijingClock.Now.Date;
    [ObservableProperty] private string historySummary="默认查询北京时间当天的执行记录。";
    private DateOnly appliedHistoryDate=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
    [RelayCommand] private async Task TodayHistoryAsync()
    {HistoryDate=CRS.Timing.BeijingClock.Now.Date;await FilterHistoryAsync();}
    /// <summary>历史日期保留查询结果；今天的记录自动刷新。</summary>
    public Task RefreshRecentHistoryAsync()=>Busy||lifetime.IsCancellationRequested||
        appliedHistoryDate!=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime)
        ?Task.CompletedTask:WithStatusAsync(LoadHistoryAsync);

    private async Task LoadHistoryAsync()
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var (rows,pending)=await ReadHistoryPagesAsync(deadline.Token);
        lifetime.ThrowIfCancellationRequested();
        // 收齐当天结果后更新，失败不展示半份记录；保留现有选中行以便查看日志。
        var ids=rows.Select(x=>x.ExecutionId).ToHashSet();
        for(var i=History.Count-1;i>=0;i--)if(!ids.Contains(History[i].ExecutionId))History.RemoveAt(i);
        for(var i=0;i<rows.Count;i++)
        {
            var existing=History.FirstOrDefault(x=>x.ExecutionId==rows[i].ExecutionId);
            if(existing is null)History.Insert(i,rows[i]);
            else{var index=History.IndexOf(existing);if(existing!=rows[i])History[index]=rows[i];if(index!=i)History.Move(index,i);}
        }
        PendingRequests.Clear();foreach(var row in pending??[])PendingRequests.Add(row);
        PendingStatus=pending is null?"后台尚未提供待执行请求信息。":pending.Count==0?"当前筛选下没有待执行的手动请求。":"当前待执行请求（与记录日期无关）；详情请到“当前任务”查看。";
        HistorySummary=$"{appliedHistoryDate:yyyy-MM-dd}（北京时间） · 共 {rows.Count} 条执行记录";
        UpdateHistorySuggestions();
    }
    private async Task<(List<ExecutionRecord> Rows,IReadOnlyList<BackgroundPendingRequest>? Pending)> ReadHistoryPagesAsync(CancellationToken token)
    {
        var date=appliedHistoryDate.ToString("yyyy-MM-dd");
        var rows=new List<ExecutionRecord>();var cursors=new HashSet<string>();var ids=new HashSet<string>();
        IReadOnlyList<BackgroundPendingRequest>? pending=null;string? cursor=null;
        do
        {
            var page=await ReadHistoryPageAsync(date,cursor,token);
            if(cursor is null)pending=page.PendingRequests;
            foreach(var row in page.History??[])
            {
                if(DateOnly.FromDateTime(row.StartedUtc.DateTime)!=appliedHistoryDate||
                    appliedResultFilter=="Issues"&&!IsIssueResult(row.Result)||!ids.Add(row.ExecutionId))
                    throw new System.IO.InvalidDataException("后台执行记录与查询条件不一致，请核对前后台版本。");
                rows.Add(row);
            }
            cursor=page.NextCursor;
            if(rows.Count>4096||cursor is not null&&!cursors.Add(cursor))throw new System.IO.InvalidDataException("执行记录分页无效。");
        }while(cursor is not null);
        return(rows,pending);
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
