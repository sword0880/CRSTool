using System.Net;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>合成正文只验证固定报告的大文件边界，不把HTTP替身称作真实PDF或正式持仓验收。</summary>
internal static class IcbcSourceChecks
{
    public static async Task Run(string root)
    {
        async Task Check(string name,Uri uri,int size,bool allowed)
        {
            using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,Guid.NewGuid().ToString("N")),TestMarketData.Key),new Handler(size));
            try{var reply=await gateway.GetAsync(uri,default);if(!allowed||reply.Content.Length!=size)throw new Exception(name);}
            catch(InvalidDataException) when(!allowed){}
            Console.WriteLine("PASS 工行来源边界："+name);
        }
        await Check("精确已核报告接受13.35MiB正文",new("https://v.icbc.com.cn"+OfficialEquityDocuments.IcbcHalfPath),13993544,true);
        await Check("精确报告超过16MiB拒绝",new("https://v.icbc.com.cn"+OfficialEquityDocuments.IcbcHalfPath),16*1024*1024+1,false);
        await Check("交易所普通正文仍限8MiB",new("https://www.sse.com.cn/fixture.pdf"),9*1024*1024,false);
    }
    private sealed class Handler(int size):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[size])});
    }
}
