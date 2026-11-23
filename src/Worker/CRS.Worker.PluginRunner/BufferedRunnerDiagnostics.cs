using System.Threading.Channels;
using CRS.Worker.PluginHost;

namespace CRS.Worker.PluginRunner;

/// <summary>有限生命周期诊断不得阻塞业务返回或取消确认；金融审计、任务进度和持久回执不走本队列。</summary>
internal sealed class BufferedRunnerDiagnostics(Func<RunnerLifecycle,CancellationToken,Task> send):IAsyncDisposable
{
    private readonly Channel<RunnerLifecycle> queue=Channel.CreateBounded<RunnerLifecycle>(new BoundedChannelOptions(32)
        {SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.DropOldest});
    private readonly CancellationTokenSource stopping=new();
    private Task? pump;
    private int disposed,unavailable;
    public void Start()=>pump??=Task.Run(DrainAsync);
    public void Report(string phase,string code)
    {
        // 只接受原有固定分类，不把异常正文、参数、URL或密钥排进队列；拥塞丢弃旧诊断而不是堵住查询。
        if(Volatile.Read(ref disposed)!=0||Volatile.Read(ref unavailable)!=0||!System.Text.RegularExpressions.Regex.IsMatch(phase,"^[A-Za-z][A-Za-z0-9.-]{0,60}$")
            ||!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Za-z][A-Za-z0-9.=]{0,80}$"))return;
        queue.Writer.TryWrite(new(phase,code));
    }
    private async Task DrainAsync()
    {
        try
        {
            await foreach(var value in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);deadline.CancelAfter(TimeSpan.FromSeconds(1));
                try{await send(value,deadline.Token).ConfigureAwait(false);}
                catch(Exception)
                {
                    // 超时不能证明对端诊断已结束；停止本次会话的诊断发送，避免堆满对端RPC容量后误伤业务。
                    Interlocked.Exchange(ref unavailable,1);queue.Writer.TryComplete();return;
                }
            }
        }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        queue.Writer.TryComplete();stopping.Cancel();var work=pump??Task.CompletedTask;
        // 日志消费者失去响应也不能无限延长Runner停止；晚到完成只回收本队列资源。
        try{await work.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);stopping.Dispose();}
        catch(TimeoutException){_=work.ContinueWith(done=>{_=done.Exception;stopping.Dispose();},TaskScheduler.Default);}
    }
}
