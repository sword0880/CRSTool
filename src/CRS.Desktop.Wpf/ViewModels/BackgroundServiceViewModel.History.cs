using CommunityToolkit.Mvvm.ComponentModel;
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

    private async Task LoadHistoryAsync()
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var (rows,pending)=await ReadHistoryPagesAsync(deadline.Token);
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
        PendingStatus=pending is null?"后台尚未提供待执行请求信息。":pending.Count==0?"当前筛选下没有待执行的手动请求。":"当前待执行请求（与记录日期无关）；详情请到“当前任务”查看。";
        HistorySummary=$"{appliedHistoryStartDate:yyyy-MM-dd} 至 {appliedHistoryEndDate:yyyy-MM-dd}（北京时间） · 共 {rows.Count} 条执行记录";
        UpdateHistorySuggestions();
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
