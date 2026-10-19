using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private async Task<BackgroundJobResult> SearchEquityReportsAsync(EtfRequest request,Guid? commandId,CancellationToken token)
    {
        var key=Key(request);
        if(key.Market!="SSE")return new("Rejected","reports=SseOnly");
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var from=request.From??today.AddDays(-400);var to=request.To??today;
        if(to>today)return new("Rejected","reports=FutureDate");
        try{_=OfficialEquityReports.Request(key,from,to,1);}catch(InvalidDataException){return new("Rejected","reports=InvalidRange");}
        if(commandId is {} committed&&await workspace!.EquityReportCommandCompleteAsync(committed,token))return BackgroundJobResult.Success("reports=AlreadyCommitted");
        var pages=new List<byte[]>();var pageCount=1;
        try
        {
            for(var page=1;page<=pageCount;page++)
            {
                var uri=OfficialEquityReports.Request(key,from,to,page);var response=await http!.GetAsync(uri,token);
                if(response.StatusCode!=200)return new("RetryDeferred","reports=SourceUnavailable",TimeSpan.FromMinutes(30));
                var parsed=OfficialEquityReports.Parse(key,from,to,page,response.Content);pageCount=parsed.Pages;pages.Add(response.Content);
                await ReportAsync(new("ReportIndexPage",Market:key.Market,Received:parsed.Documents.Count));
            }
            await workspace!.SaveEquityReportsAsync(key,from,to,pages,token,commandId);
            await ReportAsync(new("ReportIndexCommitted",Market:key.Market,Added:pages.Count));
            return BackgroundJobResult.Success($"reports=Indexed;pages={pages.Count};periodsStillRequireReview=true");
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException or JsonException or FormatException or KeyNotFoundException)
        {return new("RetryDeferred","reports=IncompleteOrUnavailable",TimeSpan.FromMinutes(30));}
    }
}
