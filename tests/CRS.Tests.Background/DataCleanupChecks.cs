using System.Security.Cryptography;
using System.Text.Json;
using CRS.Maintenance;
using CRS.Worker.Service;

internal static class DataCleanupChecks
{
    private sealed class Clock:TimeProvider{public DateTimeOffset Value=new(2026,10,27,12,0,0,TimeSpan.FromHours(8));public override DateTimeOffset GetUtcNow()=>Value.ToUniversalTime();}
    private static int passed;
    private static void Check(bool condition,string text){if(!condition)throw new Exception("清理验收失败："+text);Console.WriteLine("PASS "+text);passed++;}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Put(string path,string value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value);}
    private sealed record Fixture(string Root,string Current,string[] Old,Clock Clock);
    private static Fixture Create(string parent)
    {
        var root=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var clock=new Clock();
        Put(Path.Combine(root,"scheduler-config.db"),"live database");Put(Path.Combine(root,"scheduler-config.db-wal"),"uncheckpointed facts");Put(Path.Combine(root,"scheduler-config.db-shm"),"shared");Put(Path.Combine(root,"service-key.dpapi"),"synthetic key only");Put(Path.Combine(root,"plugins/crs.national-team/raw/evidence.pdf"),"original evidence");
        var ids=Enumerable.Range(0,5).Select(_=>Guid.NewGuid().ToString("N")).ToArray();
        for(var i=0;i<ids.Length;i++)
        {
            var folder=Path.Combine(root,"maintenance",ids[i]);var files=new Dictionary<string,string>();
            foreach(var name in new[]{"service-key.dpapi","scheduler-config.db","plugins/crs.national-team/raw/captured.txt"}){var path=Path.Combine(folder,"snapshot",name);Put(path,"synthetic backup "+name);files.Add(name,Hash(path));}
            var release=new string('A',64);Put(Path.Combine(folder,"snapshot.json"),JsonSerializer.Serialize(new{Format=1,ReleaseHash=release,Files=files}));
            var record=new WorkerUpgradeRecord(1,ids[i],release,new string('B',64),"1.0.0","1.1.0","Committed",true,clock.Value.AddDays(-i),Hash(Path.Combine(folder,"snapshot.json")));
            Put(Path.Combine(root,"maintenance",i==0?"upgrade.json":ids[i]+".json"),JsonSerializer.Serialize(record));
        }
        var old=Path.Combine(root,"logs/service-20260901.jsonl");Put(old,"expired log");File.SetLastWriteTimeUtc(old,clock.Value.UtcDateTime.AddDays(-50));
        Put(Path.Combine(root,"logs/service-20261027.jsonl"),"today");Put(Path.Combine(root,"logs/unknown.tmp"),"unknown retain");
        Put(Path.Combine(root,"MaintenanceBackups/unknown.db"),"unknown preserve");
        return new(root,ids[0],ids.Skip(1).ToArray(),clock);
    }
    private static Dictionary<string,string> Fingerprints(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(x=>Path.GetRelativePath(root,x),Hash);
    private static async Task<DataCleanupStatus> Wait(WorkerDataCleanup service,DataCleanupStatus status,string sid="admin")
    {
        var until=DateTimeOffset.UtcNow.AddSeconds(20);
        while(status.State is "Scanning" or "Running"){if(DateTimeOffset.UtcNow>until)throw new TimeoutException();await Task.Delay(10);status=service.Status(status.OperationId,sid);}return status;
    }
    private static async Task<DataCleanupStatus> Preview(WorkerDataCleanup service,bool backups=true)=>await Wait(service,service.BeginPreview(Guid.NewGuid(),"admin",new(14,2,backups)));
    private static async Task Denied(Action action,string name)
    {await Task.Yield();try{action();}catch(Exception error) when(error is InvalidDataException or UnauthorizedAccessException or InvalidOperationException){Check(true,name);return;}throw new Exception("预期拒绝："+name);}
    public static async Task RunAsync()
    {
        var parent=Path.Combine(Path.GetTempPath(),"CRS.DataCleanup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(parent);
        var f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var before=Fingerprints(f.Root);var directories=Directory.GetDirectories(f.Root,"*",SearchOption.AllDirectories).Length;
            var audit=service.Audit(new());var after=Fingerprints(f.Root);
            Check(before.Count==after.Count&&before.All(x=>after[x.Key]==x.Value)&&directories==Directory.GetDirectories(f.Root,"*",SearchOption.AllDirectories).Length,"扫描完全只读，不写目录或记录");
            Check(audit.Entries.Count(x=>x.Selected)==1&&audit.Entries.Single(x=>x.Selected).Category=="Logs","默认仅选择超过保留期的已关闭日志");
            Check(!audit.Entries.Single(x=>x.Id==f.Current).Eligible,"当前维护引用的快照始终保护");
            Check(audit.Entries.Count(x=>x.Category=="UpgradeBackup"&&x.Eligible)==2,"保留当前快照和最近两份完整历史快照");
            await Denied(()=>service.Audit(new(0,2,true)),"拒绝非法日志保留期");await Denied(()=>service.Audit(new(14,1,true)),"拒绝少于两份历史回退副本");
            var preview=await Preview(service);await Denied(()=>service.Status(preview.OperationId,"another-sid"),"预览按实际调用者身份隔离");
            var executeId=Guid.NewGuid();var running=service.BeginExecute(executeId,preview.OperationId,"admin");var complete=await Wait(service,running);
            Check(complete.State=="Completed"&&complete.DeletedFiles==9,"只清理一个过期日志和两份各四文件的旧快照");
            Check(!Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[2]))&&!Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[3])),"旧快照已从回退位置弃用并完成清理");
            Check(Directory.Exists(Path.Combine(f.Root,"maintenance",f.Current))&&f.Old.Take(2).All(x=>Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"保留当前和最近两份回退快照");
            Check(before.Where(x=>!x.Key.StartsWith("maintenance",StringComparison.OrdinalIgnoreCase)&&!x.Key.EndsWith("20260901.jsonl")).All(x=>File.Exists(Path.Combine(f.Root,x.Key))&&Hash(Path.Combine(f.Root,x.Key))==x.Value),"活动数据库、WAL/SHM、密钥、原件和未知资料字节未改变");
            Check(f.Old.All(x=>File.Exists(Path.Combine(f.Root,"maintenance",x+".json"))),"历史维护记录始终保留");
            Check(service.BeginExecute(executeId,preview.OperationId,"admin").DeletedFiles==complete.DeletedFiles,"同一操作编号重试不重复清理");
            await Denied(()=>service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"),"已使用的预览不能重复提交新清理");
        }
        await using(var restart=new WorkerDataCleanup(f.Root,f.Clock))Check(restart.Status(Directory.GetFiles(Path.Combine(f.Root,"maintenance/cleanup"),"*.json").Select(Path.GetFileNameWithoutExtension).Select(x=>Guid.ParseExact(x!,"N")).Single(),"admin").State=="Completed","完成结果经后台重启仍可查询");
        var interrupted=Guid.NewGuid();Put(Path.Combine(f.Root,"maintenance/cleanup",interrupted.ToString("N")+".json"),JsonSerializer.Serialize(new{Format=1,Id=interrupted,Sid="admin",PreviewId=Guid.NewGuid(),Status=new DataCleanupStatus(interrupted,"Running",f.Clock.Value,null,[],0,0,"synthetic crash")}));
        await using(var restart=new WorkerDataCleanup(f.Root,f.Clock)){var status=restart.Status(interrupted,"admin");Check(status.State=="Interrupted"&&!status.CountsComplete,"重启遇到在途记录时数量保持未知，不能报告删除零个");}
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            Put(Path.Combine(f.Root,"maintenance",f.Old[0],"snapshot/service-key.dpapi"),"damaged");var result=service.Audit(new(14,2,true));
            Check(!result.Entries.Single(x=>x.Id==f.Old[0]).Eligible&&result.Entries.Count(x=>x.Category=="UpgradeBackup"&&x.Eligible)==1,"损坏副本不计入保留两份，改保留后续完整副本");
            var preview=await Preview(service);f.Clock.Value=f.Clock.Value.AddMinutes(16);await Denied(()=>service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"),"过期预览不能执行");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service);Put(Path.Combine(f.Root,"maintenance/upgrade.json"),"changed after preview");var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));
            Check(result.State=="Interrupted"&&result.DeletedFiles==0&&File.Exists(Path.Combine(f.Root,"logs/service-20260901.jsonl")),"维护状态改变会在删除前拒绝整次清理");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service);using var lease=new FileStream(Path.Combine(f.Root,"maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));Check(result.State=="Interrupted"&&result.DeletedFiles==0,"升级维护锁被占用时不清理");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service,false);Put(Path.Combine(f.Root,"logs/service-20260901.jsonl"),"new write after preview");
            var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));Check(result.State=="Interrupted"&&result.DeletedFiles==0,"扫描后日志被写入时保留，不能误删");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service);Put(Path.Combine(f.Root,"maintenance",f.Old[0],"snapshot/service-key.dpapi"),"damaged after preview");
            var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));Check(result.State=="Interrupted"&&result.DeletedFiles==0,"准备保留的副本损坏时，在任何删除前拒绝执行");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service);var added=Path.Combine(f.Root,"maintenance",f.Old[2],"snapshot/unknown-new.bin");Put(added,"new unknown data");
            var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));Check(result.State=="Interrupted"&&result.DeletedFiles==1&&File.Exists(added),"扫描后出现未知文件时保留整个副本，如实报告前面已删日志");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var currentPath=Path.Combine(f.Root,"maintenance/upgrade.json");var current=JsonSerializer.Deserialize<WorkerUpgradeRecord>(File.ReadAllText(currentPath))!;
            Put(currentPath,JsonSerializer.Serialize(current with{Stage="Prepared",NewFactsPossible=false}));var result=service.Audit(new(14,2,true));
            Check(!result.Entries.Any(x=>x.Category=="UpgradeBackup"&&x.Eligible),"未完成维护状态下所有历史副本均保留");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var outside=Path.Combine(parent,"outside-original");Directory.CreateDirectory(outside);Put(Path.Combine(outside,"do-not-touch"),"outside evidence");
            Directory.CreateSymbolicLink(Path.Combine(f.Root,"logs/linked-directory"),outside);
            // 日志范围从不递归扫描子目录；备份内的链接则直接保护该副本。
            Directory.CreateSymbolicLink(Path.Combine(f.Root,"maintenance",f.Old[2],"snapshot/linked-directory"),outside);
            var result=service.Audit(new(14,2,true));Check(!result.Entries.Single(x=>x.Id==f.Old[2]).Eligible&&File.ReadAllText(Path.Combine(outside,"do-not-touch"))=="outside evidence","备份链接不被遍历或选中，外部原件保留");
        }
        f=Create(parent);
        await using(var service=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=await Preview(service);var path=Path.Combine(f.Root,"maintenance",f.Old[2],"snapshot/scheduler-config.db");
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {var result=await Wait(service,service.BeginExecute(Guid.NewGuid(),preview.OperationId,"admin"));Check(result.State=="Interrupted","文件占用时如实报告未完成");}
            var next=await Preview(service);Check(next.Entries.Any(x=>x.Category=="CleanupRemainder"&&x.Selected),"中断后识别已确认弃用的残留副本");
            var result2=await Wait(service,service.BeginExecute(Guid.NewGuid(),next.OperationId,"admin"));Check(result2.State=="Completed"&&!Directory.EnumerateDirectories(Path.Combine(f.Root,"maintenance/cleanup-trash")).Any(),"重新扫描后完成残留副本清理");
        }
        Console.WriteLine($"资料清理隔离验证通过：{passed} 项。隔离资料：{parent}");
    }
}
