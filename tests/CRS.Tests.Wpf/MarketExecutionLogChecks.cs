using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class MarketExecutionLogChecks
{
    public static void Run()
    {
        var now=CRS.Timing.BeijingClock.Now;
        string Log(string job,params BackgroundProgress[] details)
        {
            var record=new ExecutionRecord("fixture","crs.national-team",job,now,now,now,"Succeeded",null,1,
                details.Select((x,i)=>new ExecutionProgressEvent(i+1,now,x)).ToArray());
            using var model=new ExecutionLogViewModel(record,new BackgroundPipeClient(),default);
            return model.FullLogText;
        }
        var old=Log("market-activity-sync",new("SourceParsed",Received:1),new("DayCommitted"));
        if(!old.Contains("1条市场日成交记录")||!old.Contains("旧版未记录当日计数")||old.Contains("新增已接受0"))
            throw new Exception("旧成交日志仍将未记录的计数显示为零。");
        var measured=Log("market-activity-sync",new("MarketDayCompleted",Received:2){Reused=0,Closed=0,Empty=0,Deferred=0,Issues=0},
            new("MarketDayCompleted",Received:0){Reused=2,Closed=0,Empty=0,Deferred=0,Issues=0},new("MarketNoData"));
        if(!measured.Contains("收到2条市场日记录")||!measured.Contains("复用2条")||!measured.Contains("不作为零成交")||measured.Contains("新增已接受"))
            throw new Exception("新成交日志计数或空响应含义不准确。");
        var etf=Log("official-share-sync",new BackgroundProgress("DayCommitted",Received:3,Added:1,Duplicates:1,Pending:1));
        if(!etf.Contains("收到3条；新增已接受1条，重复跳过1条，新增待审核1条"))throw new Exception("ETF进度计数发生退化。");
        Console.WriteLine("PASS 成交执行日志：旧计数显示未记录；新日志区分收到/复用/无数据；ETF计数保留。");
    }
}
