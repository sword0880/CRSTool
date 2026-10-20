using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using CRS.Worker.Ipc;

internal static class PipeBoundaryChecks
{
    public static async Task RunAsync()
    {
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 管道边界："+label);}
        var control=new Control();var name="CRS.PipeBoundary."+Guid.NewGuid().ToString("N");
        using(var stop=new CancellationTokenSource())
        {
            var running=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
            try
            {
                var client=new BackgroundPipeClient(name);
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin",Payload:"slow"));
                Check(reply.Code=="Ok"&&timer.Elapsed>=TimeSpan.FromSeconds(9),"超过旧8秒期限的合作查询返回完整结果");
                foreach(var length in new[]{1,131073})
                {
                    await using var malformed=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
                    await malformed.ConnectAsync(2000);var bytes=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(bytes,length);
                    await malformed.WriteAsync(bytes);await malformed.FlushAsync();
                }
                Check((await client.QueryAsync("GetHealth")).Code=="Ok","畸形帧不终止其它管道监听");
                var failed=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin",Payload:"fail"));
                Check(failed.Code=="RequestFailed","已认证控制层IO失败返回明确响应而非断流");
            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
        }
        name="CRS.PipeTimeout."+Guid.NewGuid().ToString("N");
        using(var stop=new CancellationTokenSource())
        {
            var running=new BackgroundPipeServer(control,name,TimeSpan.FromMilliseconds(150),(_,_)=>throw new IOException("synthetic diagnostic fault")).RunAsync(stop.Token);
            try
            {
                var client=new BackgroundPipeClient(name);var id=Guid.NewGuid();
                try{await client.SendAsync(new(WorkerProtocol.Version,id,"QueryPlugin",Payload:"timeout"));throw new Exception("查询未超时");}
                catch(BackgroundServiceException error){Check(error.Code=="RequestTimedOut"&&!error.OutcomeUnknown&&error.RequestId==id,"查询超时提供可展示类别与原请求ID");}
                var before=control.Writes;
                try{await client.SendAsync(new(WorkerProtocol.Version,id,"RunNow"));throw new Exception("写入未超时");}
                catch(BackgroundServiceException error){Check(error.Code=="CommandOutcomeUnknown"&&error.OutcomeUnknown&&error.RequestId==id&&control.Writes==before+1,"写请求超时保留受理未知，不创建重试或新ID");}
                Check((await client.QueryAsync("GetHealth")).Code=="Ok","超时及诊断回调故障后监听继续服务");
            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
        }
        foreach(var cut in new[]{0,2,7})
        {
            name="CRS.PipeCut."+Guid.NewGuid().ToString("N");
            await using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            var peer=Task.Run(async()=>
            {
                await server.WaitForConnectionAsync();var request=new byte[4096];await server.ReadAsync(request);
                if(cut>0){var frame=new byte[cut];BinaryPrimitives.WriteInt32LittleEndian(cut>=4?frame:new byte[4],20);await server.WriteAsync(frame);await server.FlushAsync();}
                await server.DisposeAsync();
            });
            try{await new BackgroundPipeClient(name).QueryAsync("GetHealth");throw new Exception("截断帧被接受");}
            catch(BackgroundServiceException error){Check(error.Code=="ResponseInterrupted"&&!error.OutcomeUnknown,"响应在字节"+cut+"截断不会泄露原始EndOfStreamException");}
            await peer;
        }
        name="CRS.PipeFragment."+Guid.NewGuid().ToString("N");
        await using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        {
            var id=Guid.NewGuid();var body=JsonSerializer.SerializeToUtf8Bytes(new BackgroundResponse(WorkerProtocol.Version,id,"Ok"));
            var frame=new byte[body.Length+4];BinaryPrimitives.WriteInt32LittleEndian(frame,body.Length);body.CopyTo(frame,4);
            var peer=Task.Run(async()=>{await server.WaitForConnectionAsync();var input=new byte[4096];await server.ReadAsync(input);foreach(var b in frame)await server.WriteAsync(new[]{b});await server.FlushAsync();});
            var reply=await new BackgroundPipeClient(name).SendAsync(new(WorkerProtocol.Version,id,"GetHealth"));await peer;
            Check(reply.Code=="Ok","分片到单字节的完整帧仍正确重组");
        }
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();try{await new BackgroundPipeClient("unused").QueryAsync("GetHealth",cancel.Token);throw new Exception("取消未传播");}
            catch(OperationCanceledException){Check(true,"用户取消保持取消语义");}
        }
    }
    private sealed class Control:IBackgroundControl
    {
        public int Writes;
        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command=="RunNow"){Interlocked.Increment(ref Writes);await Task.Delay(Timeout.Infinite,token);}
            if(request.Payload=="slow")await Task.Delay(TimeSpan.FromSeconds(9),token);
            if(request.Payload=="timeout")await Task.Delay(Timeout.Infinite,token);
            if(request.Payload=="fail")throw new IOException("synthetic private detail");
            return new(WorkerProtocol.Version,request.RequestId,"Ok");
        }
    }
}
