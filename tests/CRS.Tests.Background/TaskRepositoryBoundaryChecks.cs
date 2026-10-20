using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

internal static class TaskRepositoryBoundaryChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        var path=Path.Combine(root,"atomic.db");
        var passed=0;
        void Check(bool value,string label)
        {if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 任务存储边界："+label);}
        void StorageFailure(Action work)
        {
            try{work();throw new Exception("存储故障未报告");}
            catch(IOException error) when(error.InnerException is SqliteException)
            {Check(true,"提供方故障转换为IO异常并保留内部原因");}
        }
        try
        {
            using var repository=new EncryptedTaskRepository(path,key);
            using var injection=new SqliteConnection(new SqliteConnectionStringBuilder
            {DataSource=path,Password=Convert.ToHexString(key),Pooling=false}.ToString());
            injection.Open();
            void Sql(string sql){using var command=injection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
            repository.RegisterDefault("test",new("job","任务","0 0 0 * * ?","China Standard Time",true));
            var plan=repository.Plans().Single();
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","test","job",Payload:"{}");
            repository.AcceptCommand(request,"test");

            // Save先写计划并取消Pending，再在关联命令完成时失败；三者必须共同回滚。
            Sql("CREATE TRIGGER fail_command BEFORE UPDATE ON command_request WHEN NEW.code='Ok' BEGIN SELECT RAISE(ABORT,'injected'); END");
            StorageFailure(()=>repository.Save(plan with{Enabled=false},plan.Revision,request.RequestId));
            Check(repository.Plans().Single()==plan && repository.FindCommand(request.RequestId,"test") is{State:"Pending",Code:"Accepted"},"计划修订、禁用取消和命令回执共同回滚");
            Sql("DROP TRIGGER fail_command");

            Sql("CREATE TRIGGER fail_claim BEFORE UPDATE ON command_request WHEN NEW.state='Running' BEGIN SELECT RAISE(ABORT,'injected'); END");
            StorageFailure(()=>repository.Begin(plan,DateTimeOffset.UtcNow,request.RequestId));
            Check(repository.History().Count==0 && repository.FindCommand(request.RequestId,"test") is{State:"Pending",ExecutionId:null},"开始执行失败不留下执行或占用命令");
            Sql("DROP TRIGGER fail_claim");

            var id=repository.Begin(plan,DateTimeOffset.UtcNow,request.RequestId);
            Sql("CREATE TRIGGER fail_complete BEFORE UPDATE ON command_request WHEN NEW.state='Completed' BEGIN SELECT RAISE(ABORT,'injected'); END");
            StorageFailure(()=>repository.End(id,plan,new("Failed"),request.RequestId));
            Check(repository.FindExecution(id) is{Result:"Running",EndedUtc:null} && repository.Plans().Single()==plan
                && repository.FindCommand(request.RequestId,"test") is{State:"Running"},"完成回执失败时结果、退避和命令状态共同回滚");
            Sql("DROP TRIGGER fail_complete");
            repository.End(id,plan,BackgroundJobResult.Success(),request.RequestId);
            Check(repository.FindExecution(id) is{Result:"Succeeded"} && repository.FindCommand(request.RequestId,"test") is{State:"Completed",Code:"Succeeded"},"故障注入移除后显式重试完整提交");
            try{repository.Save(plan,plan.Revision+10);throw new Exception("版本冲突未拒绝");}
            catch(InvalidOperationException){Check(true,"业务版本冲突保持业务异常");}
            try{repository.HistoryPage(null,0);throw new Exception("分页参数未拒绝");}
            catch(ArgumentException){Check(true,"输入错误保持参数异常");}

            Sql("DROP TABLE execution_progress");
            StorageFailure(()=>repository.FindExecution(id));
            await using var catalog=new BackgroundPluginCatalog();
            await using var runtime=new BackgroundRuntime(repository,catalog);
            var reply=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:id),new("test",true),default);
            Check(reply.Code=="StorageFault","真实仓储读取故障触发宿主失败关闭");
            var health=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),new("test",true),default);
            Check(health.Health?.State=="StorageFault","读取故障后健康状态仍可查询");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
        Console.WriteLine($"任务仓储边界通过：{passed}项。");
    }
}
