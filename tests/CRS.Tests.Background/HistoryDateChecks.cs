using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

internal static class HistoryDateChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.HistoryDate",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);var path=Path.Combine(root,"history.db");
        try
        {
            using var repository=new EncryptedTaskRepository(path,key);
            repository.RegisterDefault("plugin",new("one","任务","0 * * * * ?","China Standard Time",true));
            var plan=repository.Plans().Single();
            using var fixture=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Password=Convert.ToHexString(key),Pooling=false}.ToString());fixture.Open();
            string Seed(string started,string result="Succeeded")
            {
                var id=repository.Begin(plan,CRS.Timing.BeijingClock.Now);
                repository.End(id,plan,new(result,new string('测',1024)));
                using var edit=fixture.CreateCommand();edit.CommandText="UPDATE execution SET started=$at WHERE id=$id";
                edit.Parameters.AddWithValue("$at",started);edit.Parameters.AddWithValue("$id",id);edit.ExecuteNonQuery();return id;
            }
            var expected=new HashSet<string>();
            for(var i=0;i<60;i++)expected.Add(Seed($"2026-10-22T12:{i:00}:00.0000000+08:00",i%2==0?"Succeeded":"RetryDeferred"));
            expected.Add(Seed("2026-10-22T00:00:00.0000000+08:00"));
            expected.Add(Seed("2026-10-22T23:59:59.9999999+08:00"));
            Seed("2026-10-21T23:59:59.9999999+08:00");Seed("2026-10-23T00:00:00.0000000+08:00");
            var date=new DateOnly(2026,10,22);
            Check(repository.HistoryPage(null,10,date:date).Rows.All(x=>expected.Contains(x.ExecutionId)),"日期先于分页且不受较新其他日期干扰");
            await using var catalog=new BackgroundPluginCatalog();await using var runtime=new BackgroundRuntime(repository,catalog);
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var pipe="CRS.HistoryDate."+Guid.NewGuid().ToString("N");
            var server=new BackgroundPipeServer(runtime,pipe).RunAsync(stop.Token);
            try
            {
                var client=new BackgroundPipeClient(pipe);var found=new HashSet<string>();string? cursor=null;
                do
                {
                    var page=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryByDate",Payload:"2026-10-22",Cursor:cursor,PageSize:50),stop.Token);
                    Check(page.Code=="Ok"&&page.Payload=="2026-10-22"&&JsonSerializer.SerializeToUtf8Bytes(page).Length<131072,"只读日期查询确认条件且长检查点不超过帧限制");
                    foreach(var row in page.History??[])Check(found.Add(row.ExecutionId),"内部多批读取没有重复记录");
                    cursor=page.NextCursor;
                }while(cursor is not null);
                Check(found.SetEquals(expected),"当天62条完整返回，精确包含午夜和最后一刻并排除邻日");
                var errors=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryByDate",PluginId:"plugin",JobId:"one",ResultFilter:"Issues",Payload:"2026-10-22",PageSize:50));
                Check(errors.AppliedResultFilter=="Issues"&&errors.History!.All(x=>x.Result=="RetryDeferred"),"日期、插件、任务、异常条件同时生效");
                var empty=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryByDate",Payload:"2026-10-20"));
                Check(empty.History is {Count:0}&&empty.NextCursor is null,"无记录日期返回空结果");
                var invalid=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryByDate",Payload:"2026-02-30"));
                Check(invalid.Code=="InvalidRequest","非法日期明确拒绝");
            }
            finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static void Check(bool ok,string text)
    {if(!ok)throw new Exception(text);Console.WriteLine("PASS 日期记录："+text);}
}
