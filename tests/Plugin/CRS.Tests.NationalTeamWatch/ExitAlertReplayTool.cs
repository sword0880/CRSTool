using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>本地研究工具仅查询服务和写报告，不补采、不审核、不发送通知。</summary>
internal static class ExitAlertReplayTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task FromFile(string input,string output)
    {
        var request=JsonSerializer.Deserialize<AlertReplayRequest>(await File.ReadAllTextAsync(input))??throw new InvalidDataException("回放输入为空。");
        await Save(request,output);
    }
    public static async Task Installed(string fromText,string toText,string output)
    {
        var from=DateOnly.ParseExact(fromText,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        var to=DateOnly.ParseExact(toText,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        if(from>to||to.DayNumber-from.DayNumber>3660)throw new InvalidDataException("研究区间无效。");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var workspace=new WorkerEtfWorkspace();var config=await workspace.AlertConfigurationAsync(token);
        var pool=(await workspace.ConfigurationAsync(token)).Where(x=>x.Active).OrderBy(x=>x.Market).ThenBy(x=>x.Ticker).ToArray();
        var metadata=await workspace.AlertMetadataAsync(token);var members=new List<AlertReplayMember>();var holdings=new List<NamedEtfHolding>();
        var evidence=new List<object>();var cutoff=DateTimeOffset.UtcNow;
        foreach(var fund in pool)
        {
            var key=new EtfKey(fund.Market,fund.Ticker);
            var coverage=await workspace.CoverageAsync(key,from.AddDays(-90),to,token);
            var points=(await workspace.PointsAsync(key,token)).Where(x=>x.Date>=from.AddDays(-90)&&x.Date<=to).ToArray();
            var revisions=await workspace.EvidenceAsync(key,token);var ids=points.Select(x=>x.RevisionId).ToHashSet();
            var used=revisions.Where(x=>ids.Contains(x.RevisionId)).ToArray();
            if(used.Length!=ids.Count||used.Any(x=>x.State!="Accepted"))throw new InvalidDataException("读取期间修订发生变化，请重新生成研究快照。");
            members.Add(new(new(key,fund.Name,coverage.Days.Select(x=>x.Date).ToArray(),points,
                metadata.SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker),coverage.CalendarVerified&&coverage.CalendarRangeCovered,cutoff),from,to));
            holdings.AddRange(await workspace.NamedEtfHoldingsAsync(key,token));
            evidence.Add(new{Key=key,Coverage=coverage,Revisions=used});
        }
        var request=new AlertReplayRequest(from,to,cutoff,config.Rules,members,holdings,
            "使用本次读取时启用的固定样本和规则作事后研究；历史成员有效期及规则当时可用性未证明，不代表无幸存者偏差的策略验收。");
        await Save(request,output);
        await File.WriteAllTextAsync(Path.Combine(output,"evidence.json"),JsonSerializer.Serialize(evidence,Json));
    }
    private static async Task Save(AlertReplayRequest request,string output)
    {
        var report=ExitAlertReplay.Run(request);Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,Json));
        await File.WriteAllTextAsync(Path.Combine(output,"inputs.json"),JsonSerializer.Serialize(report.FrozenInputs,Json));
        var text=new StringBuilder($"# ETF减持预警历史研究回放\n\n区间：{request.From:yyyy-MM-dd} 至 {request.Through:yyyy-MM-dd}\n\n规则：{report.RuleVersion}；时效证明：{report.TimingProof}\n\n{request.SampleBasis}\n\n{report.Limitations}\n\n输入指纹：{report.InputFingerprint}\n\n|证券|已接受/登记交易日|日历核验|有效5日窗口|有效20日窗口|\n|---|---:|---|---:|---:|\n");
        foreach(var row in report.Coverage)text.AppendLine($"|{row.Key.Market}:{row.Key.Ticker}|{row.AcceptedDays}/{row.RegisteredDays}|{(row.CalendarVerified?"已核验":"未通过")}|{row.Valid5}|{row.Valid20}|");
        text.AppendLine("\n|规则|范围|首次模拟触发日期|\n|---|---|---|");
        foreach(var row in report.FirstTriggers)text.AppendLine($"|{row.RuleId}|{row.ScopeId}|{row.Date:yyyy-MM-dd}|");
        if(report.FirstTriggers.Count==0)text.AppendLine("\n未形成可计算的触发证据，不能据此断言没有发生异常流出。");
        await File.WriteAllTextAsync(Path.Combine(output,"report.md"),text.ToString());
        Console.WriteLine($"READ 研究回放：{report.Coverage.Count}只，首次模拟触发{report.FirstTriggers.Count}项；TimingProof={report.TimingProof}。报告：{Path.GetFullPath(output)}");
    }
}
