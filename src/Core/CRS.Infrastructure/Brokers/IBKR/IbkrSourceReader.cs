using System.Collections.Immutable;
using System.Xml.Linq;

namespace CRS.Infrastructure;

/// <summary>IBKR两类投影共用唯一原件读取/结构归一入口；安全XML解析后立刻冻结，不暴露可变树。</summary>
internal static class IbkrSourceReader
{
    internal const string Version="IBKR.SourceFacts.v1";
    internal static BrokerSourceDocument Read(FrozenInput input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream=input.OpenRead();var document=XmlFields.ReadXml(stream);
        if(document.Root?.Name!="FlexQueryResponse"||XmlFields.Optional(document.Root,"type","AF")!="AF")
            throw new CrsException("请提供 Activity Flex XML，不接受请求回执或其他报告。");
        if(document.Root.Element("FlexStatements")?.Elements("FlexStatement").Any()!=true)throw new CrsException("XML 中没有 FlexStatement。");
        var count=0;var root=Freeze(document.Root,"FlexQueryResponse",token,0,ref count);
        token.ThrowIfCancellationRequested();return new("IBKR",input.Sha256,input.Sha256,Version,root);
    }
    private static BrokerSourceRecord Freeze(XElement element,string row,CancellationToken token,int depth,ref int count)
    {
        token.ThrowIfCancellationRequested();
        if(depth>64||row.Length>2048||++count>250000)throw new CrsException("IBKR来源事实结构超过单次处理限额。");
        var records=ImmutableArray.CreateBuilder<BrokerSourceRecord>();var index=0;
        foreach(var child in element.Elements())records.Add(Freeze(child,row+"/"+child.Name.LocalName+"["+(++index)+"]",token,depth+1,ref count));
        return new(element.Name.LocalName,row,element.Attributes().ToImmutableSortedDictionary(x=>x.Name.ToString(),x=>x.Value,StringComparer.Ordinal),
            records.ToImmutable(),element.HasElements?"":element.Value);
    }
}
