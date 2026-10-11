using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Domain;

/// <summary>独立检查冻结边界、确定性编码和业务／执行指纹篡改拒绝。</summary>
internal static class FrozenEvidenceChecks
{
    public static void Run()
    {
        var lot=new CostLot {Key=new("A","S","USD"),Symbol="S",BuyTime=new(2024,1,1,0,0,0,TimeSpan.Zero),
            RecordId="OPEN",SourceFile="synthetic.xml",Quantity=2,Cost=10};
        var result=new CalculationResult {Year=2025,Summary=new(0,0,0,0,0,0,0),EndingLots=[lot],CoveredAccounts=["A"],SnapshotId="RUN"};
        result.SealForPersistence("synthetic"); result.Freeze();
        lot.Cost=99; // 冻结必须复制可变 LOT，外部引用不能修改已发布结果。
        if(result.EndingLots[0].Cost!=10) throw new Exception("外部 LOT 改写冻结结果。");
        Reject(()=>result.CoveredAccounts.Add("B")); Reject(()=>result.EndingLots[0].Cost=20);
        Reject(()=>result.SnapshotId="NEW"); Reject(()=>result.EvidenceSeal=null);
        var restored=JsonSerializer.Deserialize<CalculationResult>(JsonSerializer.Serialize(result))!.Freeze();
        RunEvidenceSeal.Verify(restored); Reject(()=>restored.EndingLots.Clear());
        void Tamper(Action<JsonObject> change)
        {
            var node=JsonSerializer.SerializeToNode(result)!.AsObject(); change(node);
            Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!));
        }
        Tamper(n=>n["Summary"]!["GainCny"]=1m);
        Tamper(n=>n["EvidenceSeal"]!["ApplicationSha256"]="changed");
        Tamper(n=>n["EvidenceSeal"]!["EvidenceDigest"]="changed");
        // 属性顺序、十进制尾零及负零不应改变固定编码，数组顺序必须保留。
        string Hash(string json) {using var doc=JsonDocument.Parse(json); return CanonicalEvidence.Hash(doc.RootElement);}
        if(Hash("{\"b\":-0,\"a\":1.00}")!=Hash("{\"a\":1,\"b\":0}") || Hash("[1,2]")==Hash("[2,1]"))
            throw new Exception("确定性证据编码不一致。");
        Reject(()=>Hash("{\"a\":1,\"a\":2}"));
        Console.WriteLine("冻结证据验证通过：集合和 LOT 防改写、持久往返、载荷与构建篡改拒绝、确定性编码。");
    }
    private static void Reject(Action action) {try {action();} catch(CrsException) {return;} throw new Exception("冻结证据未拒绝改写。");}
}
