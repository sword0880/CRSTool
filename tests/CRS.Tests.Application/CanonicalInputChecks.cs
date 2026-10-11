using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;

internal static class CanonicalInputChecks
{
    /// <summary>损坏快照重新计算摘要也不能绕过结构检查，所有读取用例使用同一门槛。</summary>
    internal static async Task RunAsync()
    {
        var service=new CalculationService(new Rates(),new Importer(),new Validator(),new Evidence());
        var original=service.Calculate(["report.xml"],2025,true,true,saveCanonicalSnapshot:true);
        var workflow=new DesktopWorkflow(new Operations(original));
        if(workflow.Trades(original).Count!=1 || (await workflow.OriginalFileRequirementsAsync(original.SnapshotId)).Reports.Single()!="report.xml")
            throw new Exception("有效 v2 快照无法读取。");
        var cases=new List<CalculationResult>();
        void Change(Action<JsonObject> change)
        {
            var root=JsonNode.Parse(original.CanonicalInputJson)!.AsObject(); change(root); cases.Add(Clone(original,root.ToJsonString()));
        }
        Change(root=>root.Remove("trades"));
        Change(root=>root["cash"]=null);
        Change(root=>root["trades"]!.AsArray().Add((JsonNode?)null));
        Change(root=>root["trades"]![0]!.AsObject().Remove("Quantity"));
        Change(root=>root["trades"]![0]!["unexpected"]=1);
        Change(root=>root["trades"]![0]!["Key"]!.AsObject().Remove("Currency"));
        Change(root=>root["confirmations"]!.AsObject().Remove("scopeConfirmed"));
        Change(root=>root["confirmations"]!["scopeConfirmed"]=false);
        Change(root=>root["broker"]="FUTU");
        Change(root=>root["unexpected"]=true);
        Change(root=>root["files"]![0]!["name"]="C:/private/report.xml");
        Change(root=>root["files"]![0]!["sha256"]="not-a-digest");
        Change(root=>root["files"]=new JsonArray());
        Change(root=>root["warnings"]=new JsonArray(new string('x',32769)));
        Change(root=>root["warnings"]=new JsonArray(Enumerable.Repeat("x",200001).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()));
        Change(root=>root["openingLots"]!.AsArray().Add(JsonSerializer.SerializeToNode(new CostLot
        {Key=new("A","STOCK","USD"),Symbol="STOCK",BuyTime=new(2024,1,1,0,0,0,TimeSpan.Zero),RecordId="OPEN",SourceFile="opening.xml",Quantity=1,Cost=10} )));
        // 对刚添加的批次删除成本，不能使用默认零成本。
        var lotRoot=JsonNode.Parse(cases[^1].CanonicalInputJson)!.AsObject(); lotRoot["openingLots"]![0]!.AsObject().Remove("Cost");
        cases[^1]=Clone(original,lotRoot.ToJsonString());
        cases.Add(Clone(original,original.CanonicalInputJson.Replace("\"trades\":","\"trades\":[],\"trades\":")));
        cases.Add(Clone(original,original.CanonicalInputJson.Replace("\"trades\":","\"TRADES\":[],\"trades\":")));
        cases.Add(Clone(original,"{"));
        cases.Add(Clone(original,"[]"));
        Change(root=> {JsonNode nested=JsonValue.Create(1)!; for(var i=0;i<40;i++) nested=new JsonArray(nested); root["unexpected"]=nested;});
        // 过大内容在解析和字节数组分配前拒绝；错误摘要不能把这个用例变成摘要测试。
        cases.Add(Clone(original,new string(' ',64*1024*1024+1),hash:false));
        for(var index=0;index<cases.Count;index++)
        {
            var damaged=cases[index];
            Reject(()=>service.Replay(damaged),$"复算／场景 {index+1}");
            Reject(()=>new DesktopWorkflow(new Operations(damaged)).Trades(damaged),$"明细／场景 {index+1}");
            try {await new DesktopWorkflow(new Operations(damaged)).OriginalFileRequirementsAsync(damaged.SnapshotId); throw new Exception("原件要求入口接受损坏快照。");}
            catch(CrsException) { }
        }
        var references=service.Calculate(["report.xml"],2025,true,true);
        if((await new DesktopWorkflow(new Operations(references)).OriginalFileRequirementsAsync(references.SnapshotId)).Reports.Single()!="report.xml")
            throw new Exception("有效引用快照不能查询原件。");
        Console.WriteLine($"快照契约验证通过：有效完整／引用快照、{cases.Count} 类损坏及超限输入在复算／明细／原件要求入口统一拒绝。");
    }
    private static CalculationResult Clone(CalculationResult original,string json,bool hash=true)
    {
        var node=JsonSerializer.SerializeToNode(original)!.AsObject(); node["CanonicalInputJson"]=json;
        // 结构校验需独立覆盖，不能被整份结果封存校验提前截断。
        node.Remove(nameof(CalculationResult.EvidenceSeal));
        node["CanonicalInputDigest"]=hash?Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))):"unused";
        return node.Deserialize<CalculationResult>()!;
    }
    private static void Reject(Action action,string scenario) {try {action();} catch(CrsException) {return;} throw new Exception($"损坏快照未被拒绝：{scenario}。");}
    private sealed class Importer:IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)
        {
            var data=new ImportData(); data.Sources.Add(new("report.xml",new string('a',64),"A",new(2025,1,1),new(2025,12,31),true,true,true));
            data.Trades.Add(new(new("A","STOCK","USD"),"STOCK",new(2025,1,1,0,0,0,TimeSpan.Zero),true,"BUY",1,10,10,0,-10,"BUY","report.xml",1,"STK",null));
            return data;
        }
    }
    private sealed class Rates:IExchangeRateProvider
    {public string Fingerprint=>"synthetic"; public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);}
    private sealed class Validator:ICarryValidator {public void Validate(CarryDocument document,int year,string[] accounts) { }}
    private sealed class Evidence:ICalculationEvidence
    {
        public string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
        public string HashJson(object value)=>Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        public string FileHash(string path)=>new('a',64);
        public string InfrastructureAssemblyHash=>new('b',64);
    }
    private sealed class Operations(CalculationResult result):IDesktopOperations
    {
        public string ConfigPath=>"unused"; public ICalculationRepository Repository {get;}=new Store(result);
        public IReportExporter Reports=>throw new NotSupportedException(); public IExchangeRateProvider LoadRates()=>throw new NotSupportedException();
        public CalculationService CreateCalculator(IExchangeRateProvider rates,string broker="IBKR",int year=2025)=>throw new NotSupportedException();
        public string ReadQueryId()=>""; public CarryDocument ReadCarry(string path)=>throw new NotSupportedException();
        public ForeignCreditEvidence ReadForeignCredit(string path)=>throw new NotSupportedException();
        public void SaveCarry(string path,CalculationResult value,CancellationToken cancellation=default)=>throw new NotSupportedException();
        public Task DownloadAsync(string queryId,string token,DateOnly from,DateOnly to,string path,CancellationToken cancellation)=>throw new NotSupportedException();
        public void LogFailure(string exceptionType) { }
    }
    private sealed class Store(CalculationResult result):ICalculationRepository
    {
        public CalculationResult Load(string id)=>result; public List<ReviewNote> Reviews(string id)=>[];
        public void Save(CalculationResult value)=>throw new Exception("检查快照不应保存。");
        public List<HistoryItem> History()=>[]; public HistoryPageResult QueryHistory(int pageNumber,int pageSize)=>new([],0,1,pageSize);
        public void AddReview(string id,string category,string evidence)=>throw new NotSupportedException();
    }
}
