using System.IO;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Services;

/// <summary>收齐并核对同一份任务快照；Pending已由后台排除进入Waiting的请求，两个数量可相加。</summary>
internal static class WorkerTaskActivityReader
{
    internal static async Task<BackgroundTaskActivity?> ReadAsync(BackgroundPipeClient client,CancellationToken token)
    {
        var rows=new List<BackgroundTaskActivityRow>();var cursors=new HashSet<string>();
        string? cursor=null;BackgroundTaskActivity? snapshot=null;
        do
        {
            var reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",Cursor:cursor),token);
            if(reply.Code=="UnknownCommand"||reply.Code=="Ok"&&reply.TaskActivity is null)return null;
            if(reply.Code!="Ok"||reply.TaskActivity is not {} page)throw new InvalidOperationException("当前任务读取失败。");
            snapshot??=page;
            if(page.ObservedAt!=snapshot.ObservedAt||page.Running!=snapshot.Running||page.Waiting!=snapshot.Waiting||page.Pending!=snapshot.Pending)
                throw new InvalidDataException("任务快照不一致。");
            rows.AddRange(page.Rows);cursor=reply.NextCursor;
            if(rows.Count>4096||cursor is not null&&!cursors.Add(cursor))throw new InvalidDataException("任务分页无效。");
        }while(cursor is not null);
        token.ThrowIfCancellationRequested();
        if(rows.Count(x=>x.State=="Running")!=snapshot!.Running||rows.Count(x=>x.State=="Waiting")!=snapshot.Waiting||
            rows.Count(x=>x.State=="Pending")!=snapshot.Pending||rows.Any(x=>x.State is not ("Running" or "Waiting" or "Pending")))
            throw new InvalidDataException("任务状态数量不匹配。");
        return snapshot with{Rows=rows};
    }
}
