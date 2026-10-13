using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Application;

/// <summary>历史输入的统一有界读取入口；缺字段不能依靠 DTO 默认值变成有效事实。</summary>
internal static class CanonicalInputReader
{
    internal const int MaxBytes=64*1024*1024;
    internal const int MaxRows=200000;
    private static readonly string[] Collections=["annualIncome","fundMovements","tradeAmountChecks","securityMovements",
        "beginningPositions","trades","reportedSales","cash","openingLots","positions","sources","openingRounding","cashChecks","warnings","issues"];
    private static readonly JsonSerializerOptions Options=new()
    {
        PropertyNameCaseInsensitive=true,MaxDepth=32,RespectRequiredConstructorParameters=true,
        RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow
    };

    internal static JsonDocument Read(CalculationResult snapshot,bool full=false,bool deserializeRecords=true)
    {
        RunEvidenceSeal.Verify(snapshot);
        var json=snapshot.CanonicalInputJson;
        // 先计 UTF-8 字节数再解析／分配字节数组，字符数不能代替实际文件大小。
        if(string.IsNullOrWhiteSpace(json) || json.Length>MaxBytes || Encoding.UTF8.GetByteCount(json)>MaxBytes)
            throw new CrsException("规范化输入快照为空或超过 64 MB，不能读取；仍可查看冻结汇总。");
        var digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if(!string.Equals(digest,snapshot.CanonicalInputDigest,StringComparison.OrdinalIgnoreCase))
            throw new CrsException("规范化输入快照摘要不一致，不能读取。");
        JsonDocument? document=null;
        try
        {
            document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=32});
            var total=0; CheckTree(document.RootElement,ref total);
            var root=document.RootElement; Require(root,["schema","files","opening","confirmations"]);
            var schema=Field(root,"schema").GetString();
            var withPeriods=schema is "CRS.CanonicalInput.v3" or "CRS.InputReferences.v2";
            var complete=schema is "CRS.CanonicalInput.v2" or "CRS.CanonicalInput.v3";
            if(!complete && schema is not ("CRS.InputReferences.v1" or "CRS.InputReferences.v2") || full && !complete)
                throw new CrsException("规范化输入快照版本不受当前程序支持，请重新导入原件；仍可查看冻结结果。");
            if(complete && snapshot.InputRecoveryMode!=InputRecoveryMode.CanonicalLocalSnapshot
                || !complete && snapshot.InputRecoveryMode!=InputRecoveryMode.ReimportOriginalFiles) throw new JsonException();
            var allowed=new HashSet<string>(["schema","files","opening","confirmations"],StringComparer.OrdinalIgnoreCase);
            if(withPeriods)
            {
                Require(root,["accountPeriods"]); allowed.Add("accountPeriods");
                var periods=Field(root,"accountPeriods").Deserialize<List<AccountPeriodEvidence>>(Options);
                if(periods is null || periods.Count is <1 or >1000 || snapshot.AccountPeriods is null
                    || !periods.SequenceEqual(snapshot.AccountPeriods)) throw new JsonException();
            }
            else if(snapshot.AccountPeriods is not null || snapshot.AccountCoverage is not null) throw new JsonException();
            if(complete)
            {
                Require(root,["broker","carry",..Collections]); allowed.UnionWith(["broker","carry",..Collections]);
                foreach(var name in Collections) if(Field(root,name).ValueKind!=JsonValueKind.Array) throw new JsonException();
                if(Field(root,"broker").GetString()!=snapshot.Broker || snapshot.Broker is not ("IBKR" or "FUTU")) throw new JsonException();
            }
            if(root.EnumerateObject().Any(p=>!allowed.Contains(p.Name))) throw new JsonException();
            var files=Field(root,"files"); if(files.ValueKind!=JsonValueKind.Array || files.GetArrayLength() is <1 or >36) throw new JsonException();
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in files.EnumerateArray()) CheckReference(file,names);
            var opening=Field(root,"opening"); if(opening.ValueKind!=JsonValueKind.Null) CheckReference(opening,names);
            var confirmations=Field(root,"confirmations"); Require(confirmations,["openingZero","scopeConfirmed","taxpayerScopeId"]);
            if(confirmations.EnumerateObject().Count()!=3
                || Field(confirmations,"openingZero").GetBoolean()!=snapshot.OpeningZero
                || Field(confirmations,"scopeConfirmed").GetBoolean()!=snapshot.ScopeConfirmed
                || Field(confirmations,"taxpayerScopeId").GetString()!=snapshot.TaxpayerScopeId) throw new JsonException();
            if(complete && deserializeRecords) _=Deserialize(document);
            return document;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            document?.Dispose(); throw new CrsException("规范化输入快照结构、必填字段或执行归属无效，不能读取；请重新导入原件。");
        }
        catch {document?.Dispose(); throw;}
    }

    /// <summary>返回完整 DTO 时只反序列化一次，避免大快照重复分配全部记录。</summary>
    internal static CanonicalInputDocument ReadFull(CalculationResult snapshot)
    {
        using var document=Read(snapshot,true,false);
        try {return Deserialize(document);}
        catch(JsonException) {throw new CrsException("规范化输入快照记录字段缺失或无效，不能读取；请重新导入原件。");}
    }
    private static CanonicalInputDocument Deserialize(JsonDocument document)
        =>document.RootElement.Deserialize<CanonicalInputDocument>(Options)??throw new JsonException();

    private static void CheckReference(JsonElement reference,HashSet<string> names)
    {
        Require(reference,["name","sha256"]);
        var name=Field(reference,"name").GetString(); var hash=Field(reference,"sha256").GetString();
        if(reference.EnumerateObject().Count()!=2 || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':'])>=0
            || !names.Add(name) || hash is null || hash.Length!=64 || hash.Any(c=>!Uri.IsHexDigit(c))) throw new JsonException();
    }
    private static JsonElement Field(JsonElement item,string name)=>item.EnumerateObject()
        .First(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;
    private static void Require(JsonElement item,string[] fields)
    {
        if(item.ValueKind!=JsonValueKind.Object || fields.Any(name=>!item.TryGetProperty(name,out _))) throw new JsonException();
    }
    private static void CheckTree(JsonElement item,ref int total)
    {
        if(item.ValueKind==JsonValueKind.Object)
        {
            // 任意层级重复字段（含大小写差异）都拒绝，避免哈希内容与反序列化解释不一致。
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in item.EnumerateObject())
            {
                if(property.Name.Length>128 || !seen.Add(property.Name)) throw new JsonException();
                if(property.Name.Equals("Key",StringComparison.OrdinalIgnoreCase)) Require(property.Value,["Account","Instrument","Currency","Broker","Market"]);
                if(property.Name.Equals("openingLots",StringComparison.OrdinalIgnoreCase) || property.Name.Equals("Lots",StringComparison.OrdinalIgnoreCase))
                {
                    if(property.Value.ValueKind!=JsonValueKind.Array) throw new JsonException();
                    foreach(var lot in property.Value.EnumerateArray()) Require(lot,["Key","Symbol","BuyTime","HasOffset","RecordId","SourceFile","Quantity","Cost","Fee","CostBasisMode"]);
                }
                CheckTree(property.Value,ref total);
            }
        }
        else if(item.ValueKind==JsonValueKind.Array)
        {
            var count=item.GetArrayLength(); total+=count;
            if(count>MaxRows || total>500000) throw new JsonException();
            foreach(var value in item.EnumerateArray()) {if(value.ValueKind==JsonValueKind.Null) throw new JsonException(); CheckTree(value,ref total);}
        }
        else if(item.ValueKind==JsonValueKind.String && item.GetString()!.Length>32768) throw new JsonException();
    }
}
