using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
var operations = new StubOperations();
var workflow = new DesktopWorkflow(operations);
var result = new CalculationResult { Year=2025, Summary=new(0,0,0,0,0,0,0), SnapshotId="SYNTHETIC" };
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await workflow.CalculateAsync(new([],2025,"IBKR",true,true,false,null,false),cancelled.Token); throw new Exception("取消请求未被拒绝。"); }
catch (OperationCanceledException) { }
if (operations.Store.Saved!=0) throw new Exception("取消请求被持久化。");
try { await workflow.ExportAsync("unused",result,"old-fingerprint"); throw new Exception("过期汇率允许导出。"); } catch (CrsException) { }
if (operations.Export.Count!=0) throw new Exception("校验失败仍调用导出适配器。");
await workflow.ExportAsync("unused",result,"");
if (operations.Export.Count!=1) throw new Exception("历史结果没有使用冻结汇率。");
try { await workflow.ExportCarryAsync("unused",result); throw new Exception("不可信结转允许保存。"); } catch (CrsException) { }
if (operations.CarryCount!=0) throw new Exception("结转门槛未阻止写入。");
await workflow.SaveReviewAsync(result.SnapshotId,"资料核对","合成依据");
if ((await workflow.ReviewsAsync(result.SnapshotId)).Count!=1) throw new Exception("复核用例未关联快照。");
if (typeof(DesktopWorkflow).Assembly.GetReferencedAssemblies().Any(a=>a.Name is "CRS.Infrastructure" or "CRS.Desktop.Wpf")) throw new Exception("Application 反向依赖实现。");
Console.WriteLine("Application 验证通过：取消、导出门槛、历史汇率、结转、复核及依赖（6 项）。");
AnnualCalculationChecks.Run();
ReplayChecks.Run();
CarryReplayChecks.Run();

sealed class StubOperations : IDesktopOperations
{
    public MemoryStore Store { get; } = new(); public CounterExporter Export { get; } = new(); public int CarryCount { get; private set; }
    public string ConfigPath => "unused"; public ICalculationRepository Repository => Store; public IReportExporter Reports => Export;
    public IExchangeRateProvider LoadRates()=>new Rates();
    public CalculationService CreateCalculator(IExchangeRateProvider rates,string broker="IBKR",int year=2025)=>throw new Exception("取消请求不应创建计算器。");
    public string ReadQueryId()=>""; public CarryDocument ReadCarry(string path)=>throw new NotSupportedException();
    public ForeignCreditEvidence ReadForeignCredit(string path)=>throw new NotSupportedException();
    public void SaveCarry(string path,CalculationResult result)=>CarryCount++;
    public Task DownloadAsync(string queryId,string token,DateOnly from,DateOnly to,string path,CancellationToken cancellation)=>Task.CompletedTask;
    public void LogFailure(string exceptionType){}
}
sealed class Rates : IExchangeRateProvider
{
    public string Fingerprint=>"current"; public AppliedRate Get(int year,string currency)=>new(year,currency,1m,"synthetic","","",null,null,false,true);
}
sealed class CounterExporter : IReportExporter
{
    public int Count { get; private set; }
    public void Export(string path,CalculationResult result,IEnumerable<ReviewNote>? reviews=null)=>Count++;
}
sealed class MemoryStore : ICalculationRepository
{
    public int Saved { get; private set; } private readonly List<ReviewNote> notes=[];
    public void Save(CalculationResult result)=>Saved++;
    public List<HistoryItem> History()=>[];
    public HistoryPageResult QueryHistory(int pageNumber,int pageSize)=>new([],0,1,pageSize);
    public CalculationResult Load(string id)=>throw new NotSupportedException();
    public void AddReview(string id,string category,string evidence)=>notes.Add(new(id,category,evidence,"synthetic"));
    public List<ReviewNote> Reviews(string id)=>notes.Where(n=>n.SnapshotId==id).ToList();
}
