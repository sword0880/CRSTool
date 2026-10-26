using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只读冻结跨日实际返回值；新日历行与新增金融证据分别计数，不接触私有市场库或密钥。</summary>
internal static class EtfCrossDayLive
{
    public static async Task RunAsync(DateOnly boundary,string baselinePath,string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("冻结目录已存在，拒绝覆盖。");
        var baselineBytes=await File.ReadAllBytesAsync(baselinePath);using var baseline=JsonDocument.Parse(baselineBytes);
        string? Expected(string name)=>baseline.RootElement.GetProperty("Results").EnumerateArray()
            .Where(x=>x.GetProperty("Name").GetString()==name&&x.GetProperty("Status").GetString()=="Succeeded")
            .Select(x=>x.GetProperty("ResultContentSha256").GetString()).FirstOrDefault();
        string Hash<T>(T value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        Directory.CreateDirectory(output);using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=stop.Token;
        var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);BackgroundResponse? before=null,after=null;
        async Task Save(string name,object data)=>await File.WriteAllTextAsync(Path.Combine(output,name),JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}),token);
        try
        {
            before=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
            var dashboard=await reader.DashboardAsync(null,token)??throw new InvalidDataException("正式后台不支持完整整页。");
            var history=await reader.HistoryAsync(new EtfKey("SSE","510300"),token);
            var evidence=await reader.EvidenceAsync(new EtfKey("SSE","510300"),token);
            await Save("dashboard.json",dashboard);await Save("history-510300.json",history);await Save("evidence-510300.json",evidence);
            after=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);
            if(before.Health?.State!="Running"||after.Health?.State!="Running"||before.Health.ProcessId!=after.Health.ProcessId)
                throw new InvalidDataException("读取期间正式服务健康或PID发生变化，不批准完整冻结。");
            // 对基线只比较原有前缀；保存完整新结果，不能靠删除新增行伪造整份结果不变。
            var dashboardPrefix=new EtfDashboard(dashboard.Overview,dashboard.History.Where(x=>x.Date<boundary).ToList(),dashboard.Activity);
            var historyPrefix=history.Where(x=>x.Date<boundary).ToList();var newRows=history.Where(x=>x.Date>=boundary).ToArray();
            var report=new{Status="Succeeded",CheckedAt=CRS.Timing.BeijingClock.Now,Boundary=boundary,ReadOnly=true,Before=before.Health,After=after.Health,
                BaselineSha256=Convert.ToHexString(SHA256.HashData(baselineBytes)),DashboardRows=dashboard.History.Count,HistoryRows=history.Count,EvidenceRows=evidence.Count,
                NewDashboardRows=dashboard.History.Where(x=>x.Date>=boundary).ToArray(),NewHistoryRows=newRows,
                NewEvidenceRows=evidence.Count(x=>DateOnly.ParseExact(x.TradeDate,"yyyy-MM-dd")>=boundary),
                UnfilledCalendarRows=newRows.Length>0&&newRows.All(x=>x.Shares is null&&x.DeltaShares is null&&x.Covered==0),
                DashboardPrefixMatches=Hash(dashboardPrefix)==Expected("dashboard-1"),HistoryPrefixMatches=Hash(historyPrefix)==Expected("history-510300"),
                EvidenceMatches=Hash(evidence)==Expected("evidence-510300"),DashboardSha256=Hash(dashboard),HistorySha256=Hash(history),EvidenceSha256=Hash(evidence)};
            await Save("receipt.json",report);Console.WriteLine(JsonSerializer.Serialize(report));
        }
        catch(Exception error)
        {
            // 已保存的原始返回值留作失败取证；失败回执不提升为完整快照或空数据成功。
            await File.WriteAllTextAsync(Path.Combine(output,"unavailable.json"),JsonSerializer.Serialize(new{Status="Unavailable",CheckedAt=CRS.Timing.BeijingClock.Now,Error=error.GetType().Name,error.Message,Before=before?.Health,After=after?.Health}));
            throw;
        }
    }
}
