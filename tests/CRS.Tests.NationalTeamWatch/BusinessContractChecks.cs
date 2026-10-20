using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class BusinessContractChecks
{
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS 业务契约："+name);}
    private static void Reject(Action action,string code)
    {
        try{action();}catch(EtfContractException error) when(error.Code==code){Check(true,code);return;}
        throw new Exception("未拒绝业务契约："+code);
    }
    public static async Task Run(string root)
    {
        Check(EtfBusinessProtocol.ParseRequest("{\"Operation\":\"configuration\"}").ContractVersion==1,"旧客户端缺版本明确映射V1");
        Check(EtfBusinessProtocol.ParseRequest("{\"Operation\":\"configuration\",\"ContractVersion\":1,\"FutureDisplayHint\":true}").Operation=="configuration","同V1新增可选字段向前兼容");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"ContractVersion\":2}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"ContractVersion\":null}"),"InvalidContractVersion");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{}"),"InvalidRequest");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":null}"),"InvalidRequest");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"Operation\":\"sync\"}"),"DuplicateProperty");
        Reject(()=>EtfBusinessProtocol.ParseRequest("{\"Operation\":\"status\",\"Name\":\""+new string('牛',1500)+"\"}"),"PayloadTooLarge");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null,\"ContractVersion\":99}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"ContractError\":\"UnsupportedContractVersion\",\"SupportedContractVersion\":1}"),"UnsupportedContractVersion");
        Reject(()=>EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Snapshot\":\"s\",\"NextOffset\":null}"),"InvalidResponse");
        Check(EtfBusinessProtocol.ReadResponse<EtfQueryPage>("{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null}").ContractVersion==1,"新客户端接受既有V1服务分页");
        Check(EtfBusinessProtocol.ReadResponse<string>("\"公开状态\"")=="公开状态","V1状态字符串兼容，不伪造新版包裹");
        Directory.CreateDirectory(root);
        await using(var session=new NationalTeamBackgroundSession(new Context(root),new NoHttp()))
        {
            await session.StartAsync(default);
            var failure=JsonSerializer.Deserialize<EtfContractFailure>(await session.QueryAsync("{\"Operation\":\"configuration\",\"ContractVersion\":99}",default));
            Check(failure is {ContractError:"UnsupportedContractVersion",SupportedContractVersion:1},"后台查询明确返回版本拒绝，不降级为数据");
            var rejected=await session.ExecuteAsync("watchlist","{\"Operation\":\"watchlist\",\"ContractVersion\":99}",default);
            Check(rejected.Status=="Rejected"&&rejected.Checkpoint=="contract=UnsupportedContractVersion","持久写命令未知版本拒绝且不执行");
            try{await session.QueryAsync("{\"Operation\":\"unknown-operation\"}",default);throw new Exception("未知操作被放行。");}
            catch(ArgumentException){Check(true,"未知查询操作沿白名单拒绝");}
            var reply=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync("{\"Operation\":\"configuration\"}",default));
            Check(reply.ContractVersion==1&&JsonSerializer.Deserialize<JsonElement[]>(reply.Items)!.Length>0,"拒绝之后旧版查询仍能返回已提交完整分页");
        }
        var future=Path.Combine(root,"future-schema");Directory.CreateDirectory(future);SQLitePCL.Batteries_V2.Init();
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(future,"market.db"),Password=Convert.ToHexString(TestMarketData.Key)}.ToString()))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();}
        try{using var rejected=TestMarketData.Open(future,new RawEvidenceStore(Path.Combine(future,"raw")));throw new Exception("未知存储版本被放行。");}
        catch(InvalidDataException){Check(true,"未知持久Schema拒绝打开，与业务V1独立");}
        await ClientCompatibility();
    }
    private static async Task ClientCompatibility()
    {
        var control=new ResponseControl();var pipe="CRS.Contract.Tests."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listening=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var client=new WorkerEtfWorkspace(new BackgroundPipeClient(pipe));
            Check((await client.ConfigurationAsync(stop.Token)).Count==0,"真实管道客户端接受旧V1分页");
            foreach(var payload in new[]{"{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null,\"ContractVersion\":2}",
                "{\"ContractError\":\"UnsupportedContractVersion\",\"SupportedContractVersion\":1}"})
            {
                control.Payload=payload;
                try{await client.ConfigurationAsync(stop.Token);throw new Exception("客户端接受不兼容响应。");}
                catch(InvalidDataException error) when(error.Message.Contains("UnsupportedContractVersion"))
                {Check(true,"真实管道客户端拒绝未知版本/业务错误，不返回空列表冒充成功");}
            }
        }
        finally{stop.Cancel();try{await listening;}catch(OperationCanceledException){}}
    }
    private sealed class ResponseControl:IBackgroundControl
    {
        public string Payload="{\"Items\":\"[]\",\"Snapshot\":\"s\",\"NextOffset\":null}";
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var business=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(business.ContractVersion!=1)throw new Exception("客户端未发送V1。");
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:Payload));
        }
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoHttp:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("契约测试不可请求真实网络。");}
}
