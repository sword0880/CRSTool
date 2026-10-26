using System.IO.Pipes;
using System.Security.Principal;
namespace CRS.Worker.Ipc;

/// <summary>桌面只通过本机管道访问服务；服务不可用时明确返回，不启动替代调度器。</summary>
public sealed class BackgroundPipeClient(string pipeName = BackgroundPipeServer.DefaultPipeName)
{
    public async Task<BackgroundResponse> SendAsync(BackgroundRequest request, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(32));
        using var identity = WindowsIdentity.GetCurrent();
        var management = BackgroundPipeServer.IsMutation(request.Command) && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        await using var pipe = new NamedPipeClientStream(".", pipeName + (management ? BackgroundPipeServer.ControlSuffix : ""), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        var submitted=false;
        try
        {
            using var connecting=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);connecting.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(connecting.Token);
            if (pipeName == BackgroundPipeServer.DefaultPipeName) PipeServerIdentity.Verify(pipe);
            submitted=true;await Framing.WriteAsync(pipe, request, deadline.Token);
            var response = await Framing.ReadAsync<BackgroundResponse>(pipe, deadline.Token);
            if (response.RequestId != request.RequestId || response.ProtocolVersion != Framing.Version)
                throw new InvalidDataException("服务协议或请求标识不匹配。");
            if(response.Code is "RequestTimedOut" or "CommandOutcomeUnknown")
                throw Failure(response.Code);
            return response;
        }
        catch(BackgroundServiceException){throw;}
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(OperationCanceledException error){throw Failure(submitted?"ResponseTimedOut":"ServiceUnavailable",error);}
        catch(PipeFrameInterruptedException error){throw Failure("ResponseInterrupted",error);}
        // 权限和SCM身份核验失败同样属于通信边界；不得从async Loaded逃逸导致桌面退出。
        // submitted仍决定写命令是否结果未知，拒绝连接或身份核验失败绝不触发降级重试。
        catch(UnauthorizedAccessException error){throw Failure("AccessDenied",error);}
        catch(System.ComponentModel.Win32Exception error){throw Failure("ServiceIdentityUnavailable",error);}
        catch(IOException error){throw Failure("ConnectionFailed",error);}
        BackgroundServiceException Failure(string code,Exception? error=null)
        {
            var unknown=submitted&&BackgroundPipeServer.IsMutation(request.Command);
            var text=unknown?"后台响应未确认，操作可能已受理。请保留原参数并到任务中心核对，勿重新创建相同任务。":
                code=="RequestTimedOut"?"后台查询超时，请稍后刷新；当前结果未更新。":
                code is "AccessDenied" or "ServiceIdentityUnavailable"?"后台访问被拒绝或服务身份无法核实；未取得可用结果，请核对服务及当前Windows权限。":
                "后台连接中断或服务暂不可用，请确认配套服务已更新后重新查询。";
            return new(code,request.RequestId,unknown,text,error);
        }
    }
    public Task<BackgroundResponse> QueryAsync(string command, CancellationToken token = default) =>
        SendAsync(new(Framing.Version, Guid.NewGuid(), command), token);
}
