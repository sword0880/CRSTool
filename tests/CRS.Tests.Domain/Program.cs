using CRS.Domain;
var key = new SecurityKey("SYNTHETIC", "DEMO", "USD");
var trades = new[]
{
    new Trade(key,"DEMO",new(2025,1,2,10,0,0,TimeSpan.Zero),true,"BUY",10m,100m,1000m,2m,-1002m,"B","synthetic",1,"STK",null),
    new Trade(key,"DEMO",new(2025,1,3,10,0,0,TimeSpan.Zero),true,"SELL",10m,150m,1500m,2m,1498m,"S","synthetic",2,"STK",null)
};
var engine = new FifoEngine();
var matches = engine.Calculate(trades,[],[],[],2025,(_,_)=>1m);
if (matches.Sum(m=>m.Gain)!=496m || engine.EndingLots.Any()) throw new Exception("拆层后 FIFO 含费成本守恒失败。");
var summary = TaxEngine.Calculate([new("SYNTHETIC",2025,"USD",100m,20m)],matches,new Dictionary<string,decimal>{{"USD",6m}},2025,(_,_)=>1m);
if (summary.SupplementTax!=117.2m) throw new Exception("拆层后收入、抵免与资本收益合并金额改变。");
if (typeof(FifoEngine).Assembly.GetReferencedAssemblies().Any(a=>a.Name?.StartsWith("CRS.")==true)) throw new Exception("Domain 反向依赖外层。");
Console.WriteLine("Domain 验证通过：FIFO、税额及程序集独立性（3 项）。");
FifoOrderingChecks.Run();
