using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;

internal static class BrokerSourceFactsChecks
{
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS 共享来源事实："+name);}
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        foreach(var fixture in new[]{"ibkr_activity.xml","ibkr_reconciliation.xml"})
        {
            var path=Path.Combine("tests/fixtures",fixture);var input=FrozenInput.Read(path);var facts=IbkrSourceReader.Read(input,default);
            Check(facts.Broker=="IBKR"&&facts.SourceFileHash==input.Sha256&&facts.ImportBatchId==input.Sha256&&facts.ParserVersion=="IBKR.SourceFacts.v1","原件/批次/读取版本明确绑定");
            Check(JsonSerializer.Serialize(facts)==JsonSerializer.Serialize(IbkrSourceReader.Read(input,default)),"同原件不可变事实可确定性重建");
            var expected=JsonSerializer.Serialize(PerformanceBrokerImporter.Ibkr(input,default));var callbacks=0;
            var parsed=new IbkrXmlParser{SharedImport=(frozen,token,warnings,shared)=>
            {
                callbacks++;Check(shared is not null&&shared.SourceFileHash==frozen.Sha256,"税务完成后交出实际共享事实");
                Check(JsonSerializer.Serialize(IbkrPerformanceAdapter.Read(frozen,token,shared))==expected,"共享快照投影与独立导入逐字段一致");
            }}.Parse([path]);
            Check(callbacks==1&&parsed.Sources.All(x=>x.Sha256==facts.SourceFileHash),"每份原件同步一次且税务追溯相同原件");
            Reject(()=>IbkrPerformanceAdapter.Read(input,default,facts with{SourceFileHash="invalid"}));
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            try{IbkrSourceReader.Read(input,canceled.Token);throw new Exception("取消仍读取事实。");}catch(OperationCanceledException){Check(true,"读取前取消拒绝半份事实");}
        }
        var source=File.ReadAllText("tests/fixtures/ibkr_activity.xml");
        var extension=source.Replace("</FlexStatement>","<FutureActivities><FutureEvent currency=\"USD\" opaque=\"unchanged\">unclassified</FutureEvent></FutureActivities></FlexStatement>");
        var extended=IbkrSourceReader.Read(FrozenInput.FromBytes("extension.xml",Encoding.UTF8.GetBytes(extension)),default);
        var record=extended.Root.Section("FlexStatements")!.Rows("FlexStatement").Single().Section("FutureActivities")!.Rows().Single();
        Check(record.Name=="FutureEvent"&&record.Fields["opaque"]=="unchanged"&&record.Text=="unclassified"&&record.SourceRow.Contains("FutureActivities"),"未知区段/属性/文本及位置保留，不伪装已识别交易");
        var changed=record with{Fields=record.Fields.SetItem("opaque","other")};
        Check(record.Fields["opaque"]=="unchanged"&&changed.Fields["opaque"]=="other","派生修订不修改原不可变记录");
        var deep="<FlexQueryResponse><FlexStatements><FlexStatement>"+string.Concat(Enumerable.Repeat("<x>",70))+string.Concat(Enumerable.Repeat("</x>",70))+"</FlexStatement></FlexStatements></FlexQueryResponse>";
        Reject(()=>IbkrSourceReader.Read(FrozenInput.FromBytes("deep.xml",Encoding.UTF8.GetBytes(deep)),default));
        Reject(()=>IbkrSourceReader.Read(FrozenInput.FromBytes("dtd.xml",Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY f SYSTEM 'file:///no-file'>]><FlexQueryResponse>&f;</FlexQueryResponse>")),default));
    }
    internal static void CompareBefore(string baseline)
    {
        var context=new AssemblyLoadContext("IbkrBefore",isCollectible:true);
        try
        {
            var assembly=context.LoadFromAssemblyPath(Path.GetFullPath(baseline));
            var type=assembly.GetType("CRS.Infrastructure.IbkrXmlParser",true)!;
            foreach(var fixture in new[]{"ibkr_activity.xml","ibkr_reconciliation.xml"})
            {
                var files=new[]{Path.GetFullPath(Path.Combine("tests/fixtures",fixture))};
                var before=(ImportData)type.GetMethod("Parse")!.Invoke(Activator.CreateInstance(type),[files,null,CancellationToken.None])!;
                var after=new IbkrXmlParser().Parse(files);
                Check(after.ImportProvenance is{Count:1} versions&&versions[0].ReaderVersion==IbkrSourceReader.Version&&versions[0].ProjectionVersion==IbkrXmlParser.ProjectionVersion,"新增税务版本审计明确且仅记录实际版本");
                var oldNode=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(before))!.AsObject();
                var newNode=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(after))!.AsObject();
                oldNode.Remove(nameof(ImportData.ImportProvenance));newNode.Remove(nameof(ImportData.ImportProvenance));
                Check(oldNode.ToJsonString()==newNode.ToJsonString(),fixture+"除新增版本审计外，税务解析与旧构建逐字段一致（非独立金额签认）");
            }
        }
        finally{context.Unload();}
    }
    private static void Reject(Action action)
    {try{action();}catch(CrsException){Check(true,"错误原件/结构/版本安全拒绝");return;}throw new Exception("无效共享来源事实未拒绝。");}
}
