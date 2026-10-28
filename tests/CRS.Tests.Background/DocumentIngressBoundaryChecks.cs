using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.IO.Staging;
using CRS.Serialization;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class DocumentIngressBoundaryChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var token=deadline.Token;var passed=0;
        void Check(bool value,string message){if(!value)throw new InvalidDataException(message);passed++;Console.WriteLine("PASS 文件入口边界："+message);}
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            using var repository=new EncryptedTaskRepository(Path.Combine(root,"synthetic-scheduler.db"),key);await using var catalog=new BackgroundPluginCatalog();await using var runtime=new BackgroundRuntime(repository,catalog,documentInbox:new WorkerDocumentInbox(root));
            var reader=new BackgroundCaller("synthetic-reader",false);
            foreach(var command in new[]{"ListPluginDocuments","GetPluginDocument","PutPluginDocument"})
                Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),command,"crs.test"),reader,token)).Code=="Forbidden","普通身份被服务端拒绝："+command);
            Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"ListPluginDocuments","../outside"),new("synthetic-admin",true),token)).Code=="InvalidRequest","未加载或非合法插件不能指定暂存目录");
            var strict=false;try{StrictJson.Read<DocumentUploadRequest>("{\"Action\":\"Begin\",\"Document\":{\"Id\":\""+Guid.NewGuid()+"\",\"Sha256\":\""+new string('A',64)+"\",\"Length\":1,\"Purpose\":\"test\"},\"Path\":\"C:/outside\"}",4096);}catch(InvalidDataException){strict=true;}Check(strict,"文件参数拒绝外部路径和未知字段");
            using var identity=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("实际管理管道验收需要管理员身份。");
            var spy=new IngressSpy();var pipe="CRS.DocumentBoundary."+Guid.NewGuid().ToString("N");using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);var serving=new BackgroundPipeServer(spy,pipe).RunAsync(stop.Token);
            try
            {
                var client=new BackgroundPipeClient(pipe);var reference=new StagedDocumentReference(Guid.NewGuid(),new string('A',64),16384,"test-document-v1");
                var payload=JsonSerializer.Serialize(new DocumentUploadRequest("Chunk",reference,0,new byte[16384]));
                Check(payload.Length>4096&&payload.Length<32768,"有界文件块测试实际超过普通业务额度");
                var upload=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"PutPluginDocument","crs.test",Payload:payload);
                Check((await client.SendAsync(upload,token)).Code=="Dispatched"&&spy.Uploads==1,"实际管理通道允许有界文件块进入专用处理器");
                Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.test","test",Payload:new string('x',4097)),token)).Code=="InvalidRequest"&&spy.Runs==0,"普通业务命令仍强制4096字符上限");
                Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.test",Payload:new string('x',4097)),token)).Code=="InvalidRequest","普通插件查询没有获得文件块额度");
                Check((await client.SendAsync(upload with{RequestId=Guid.NewGuid(),Payload=new string('x',32769)},token)).Code=="InvalidRequest","文件块超过专用上限仍被拒绝");
                var before=spy.Uploads;var publicReply=await PublicEndpoint(pipe,upload with{RequestId=Guid.NewGuid()},token);
                Check(publicReply.Code=="Forbidden"&&spy.Uploads==before,"即使实际调用者是管理员，写请求发到公共查询通道也被拒绝");
                BackgroundResponse? limited=null;for(var i=0;i<70;i++){limited=await client.SendAsync(upload with{RequestId=Guid.NewGuid()},token);if(limited.Code=="RateLimited")break;}
                Check(limited?.Code=="RateLimited","上传请求按专用身份配额限流");
                Check((await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.test","test",Payload:"{}"),token)).Code=="Dispatched"&&spy.Runs==1,"上传配额耗尽不占用其他管理命令额度");
            }
            finally{stop.Cancel();try{await serving;}catch(OperationCanceledException){}}
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticDocumentIngressBoundary"}));
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static async Task<BackgroundResponse> PublicEndpoint(string pipe,BackgroundRequest request,CancellationToken token)
    {
        await using var connection=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);await connection.ConnectAsync(token);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(request);var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);await connection.WriteAsync(header,token);await connection.WriteAsync(bytes,token);await connection.FlushAsync(token);
        await connection.ReadExactlyAsync(header,token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length is <2 or >131072)throw new InvalidDataException("验收响应帧无效。");var response=new byte[length];await connection.ReadExactlyAsync(response,token);return JsonSerializer.Deserialize<BackgroundResponse>(response)??throw new InvalidDataException("验收响应为空。");
    }
    private sealed class IngressSpy:IBackgroundControl
    {
        public int Uploads,Runs;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {if(request.Command=="PutPluginDocument")Interlocked.Increment(ref Uploads);if(request.Command=="RunNow")Interlocked.Increment(ref Runs);return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Dispatched"));}
    }
}
