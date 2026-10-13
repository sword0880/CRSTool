using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;

internal static class AccountPeriodChecks
{
    /// <summary>生命周期依据不得变成跳过缺口、矛盾或凭证校验的万能开关。</summary>
    internal static void Run()
    {
        var opened=new DateOnly(2025,6,17); var end=new DateOnly(2025,12,31);
        AccountPeriodEvidence Evidence(DateOnly? closed=null)=>new("A",opened,closed,true,"synthetic-proof.pdf",new string('b',64),true);
        ImportData Data(DateOnly? start=null,DateOnly? stop=null)
        {
            var data=new ImportData(); var from=start??opened; var to=stop??end;
            data.Sources.Add(new("synthetic.xml",new string('a',64),"A",from,to,true,true,true));
            data.CashChecks.Add(new("A","USD",from,to,0,0,0,0,0,"一致","","synthetic.xml")); return data;
        }
        CalculationService Service(ImportData data)=>new(new Rates(),new Importer(data),new Carry(),new EvidencePort());
        CalculationResult Calculate(ImportData data,params AccountPeriodEvidence[] periods)=>Service(data)
            .Calculate(["synthetic.xml"],2025,false,true,saveCanonicalSnapshot:true,accountPeriods:periods);
        var baseline=Calculate(Data());
        if(!baseline.Issues.Any(i=>i.Code=="INCOMPLETE_PERIOD") || baseline.EstimatedTopUpCny is not null)
            throw new Exception("没有期间依据就跳过了完整自然年检查。");
        var service=Service(Data()); var result=service.Calculate(["synthetic.xml"],2025,false,true,saveCanonicalSnapshot:true,accountPeriods:[Evidence()]);
        var replay=service.Replay(result);
        if(!result.Complete || result.AccountCoverage!.Single().Kind!="AccountActivity" || !result.AccountCoverage!.Single().PeriodComplete
            || !replay.AccountPeriods!.SequenceEqual(result.AccountPeriods!) || replay.Summary!=result.Summary
            || !result.CanonicalInputJson.Contains("CRS.CanonicalInput.v3")) throw new Exception("有依据的开户活动期未完整保留与复算。");
        var gap=Calculate(Data(opened.AddDays(1)),Evidence());
        if(gap.Complete || gap.AccountCoverage!.Single().PeriodComplete || !gap.Issues.Any(i=>i.Code=="INCOMPLETE_PERIOD"))
            throw new Exception("开户后的实际缺口被期间依据覆盖。");
        var closed=new DateOnly(2025,10,9); var closure=Calculate(Data(stop:closed),Evidence(closed));
        if(!closure.Complete || closure.CarryEligible || closure.AccountCoverage!.Single().End!=closed)
            throw new Exception("关闭账户未按活动期核对或误生成下一年结转。");
        Reject(()=>Calculate(Data(),Evidence() with {Reviewed=false}));
        Reject(()=>Calculate(Data(),Evidence() with {Account="OTHER"}));
        Reject(()=>Calculate(Data(),Evidence(),Evidence()));
        Reject(()=>Calculate(Data(),Evidence() with {EvidenceSha256="not-a-hash"}));
        var conflict=Data(); conflict.Cash.Add(new("A",opened.AddDays(-1),"Dividends","USD",1,"C","synthetic.xml"));
        Reject(()=>Calculate(conflict,Evidence()));
        var noZero=Calculate(Data(),Evidence() with {OpeningZeroConfirmed=false});
        if(!noZero.Issues.Any(i=>i.Code=="OPENING_UNCONFIRMED")) throw new Exception("开户日期被当作零持仓确认。");
        Console.WriteLine("账户期间通过：无依据保守、有依据活动期、缺一天、关闭账户、矛盾／重复／跨账户／未核对拒绝及快照复算。");
    }
    private static void Reject(Action action) {try {action();} catch(CrsException) {return;} throw new Exception("无效期间依据被接受。");}
    private sealed class Rates:IExchangeRateProvider
    {
        public string Fingerprint=>"synthetic";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","https://example.com","test",new(year,1,1),new(year,12,31),false,true);
        public List<AppliedRate> ForYear(int year)=>[];
    }
    private sealed class Importer(ImportData data):IBrokerImporter
    {public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)=>data;}
    private sealed class Carry:ICarryValidator {public void Validate(CarryDocument document,int year,string[] accounts) {}}
    private sealed class EvidencePort:ICalculationEvidence
    {
        public string InfrastructureAssemblyHash=>"synthetic";
        public string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
        public string HashJson(object value)=>Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        public string FileHash(string path)=>new string('a',64);
    }
}
