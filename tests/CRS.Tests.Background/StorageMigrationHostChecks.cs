using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.IO;
using CRS.Worker.FixturePlugin;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

/// <summary>合成插件经真实服务、私有维护Runner和维护日志；不接触已安装服务。</summary>
internal static class StorageMigrationHostChecks
{
    private static int passed;
    private const string Plugin="crs.fixture";
    public static async Task RunAsync(string source,string root)
    {
        source=Path.GetFullPath(source);root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("隔离目录必须不存在。");Directory.CreateDirectory(root);passed=0;
        var program=Prepare(source,Path.Combine(root,"success-program"),"GoodStorageMigrationEntry");ParserChecks(program);
        var normal=Path.Combine(root,"normal-data");
        await Running(program,normal,async client=>{Check((await client.QueryAsync("GetHealth")).Health?.State=="Running"&&!File.Exists(Marker(normal)),"发行含计划但无维护代次时，普通启动不执行迁移");});
        var data=Path.Combine(root,"maintenance-data");var record=Seed(data,program);var journal=new WorkerUpgradeJournal(data);
        Save(data,record with{Stage="Switched"});
        journal.BeforeStorageOpen(program);
        Reject(()=>journal.MarkHealthy(program,[new(Plugin,"Started")],[Plugin]),"即使插件状态正常，缺少迁移回执仍不能确认Healthy");
        Save(data,record with{Stage="Switched"});var protectedKey=Hash(Path.Combine(data,"service-key.dpapi"));
        await Running(program,data,async client=>
        {
            Check((await client.QueryAsync("GetHealth")).Health?.State=="Running"&&journal.Read() is{Stage:"Healthy",StorageMigrationReceiptHash:{Length:64}},"实际服务完成维护Runner及普通插件启动后才写Healthy");
            var master=ServiceKey.Open(data);var key=new WorkerPluginContext(Plugin,data,master).OpenDataKey();
            try
            {
                var marker=AuthenticatedJsonFile.Read<StorageMigrationMarker>(Marker(data),key,"Synthetic.StorageMigration/v1");
                Check(marker.OperationId==Guid.ParseExact(record.Id,"N")&&marker.KeyProof==Convert.ToHexString(HMACSHA256.HashData(key,"synthetic-plugin-key-proof"u8))&&marker.RunnerPid!=Environment.ProcessId,"维护运行于独立进程，收到正确的插件派生密钥和操作ID");
            }
            finally{CryptographicOperations.ZeroMemory(master);CryptographicOperations.ZeroMemory(key);}
            File.AppendAllText(Path.Combine(data,"plugins",Plugin,"before.raw"),"-new-fact");
        });
        Save(data,journal.Read()! with{Stage="Switched"});
        await Running(program,data,async _=>{Check(File.ReadAllText(Path.Combine(data,"plugins",Plugin,"before.raw"))=="synthetic-preserved-before-new-fact"&&journal.Read()!.Stage=="Healthy","显式重试同一代次保留新增事实，不从旧快照覆盖");await Task.CompletedTask;});
        Check(Hash(Path.Combine(data,"service-key.dpapi"))==protectedKey,"维护与重试不更换机器密钥文件");
        var markerHash=Hash(Marker(data));Save(data,journal.Read()! with{Stage="Committed"});
        await Running(program,data,async _=>{Check(Hash(Marker(data))==markerHash&&journal.Read()!.Stage=="Committed","已提交代次普通重启不再次调用存储迁移");await Task.CompletedTask;});
        foreach(var (name,entry,seconds) in new[]{("failed","FailedStorageMigrationEntry",30),("timeout","HangingStorageMigrationEntry",8),("missing", "",30)})
        {
            var failedProgram=Prepare(source,Path.Combine(root,name+"-program"),entry,seconds);var failedData=Path.Combine(root,name+"-data");var failedRecord=Seed(failedData,failedProgram);var original=Hash(Path.Combine(failedData,"plugins",Plugin,"before.raw"));Save(failedData,failedRecord with{Stage="Switched"});
            using var process=Launch(failedProgram,failedData,"CRS.Migration.Failure."+Guid.NewGuid().ToString("N"));
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Check(process.ExitCode!=0&&new WorkerUpgradeJournal(failedData).Read() is{Stage:"Starting",NewFactsPossible:true,StorageMigrationReceiptHash:null},name+"不会被当作迁移完成或Healthy");
                Check(Hash(Path.Combine(failedData,"plugins",Plugin,"before.raw"))==original,name+"保留原业务事实");
                var pidFile=Path.Combine(failedData,"plugins",Plugin,"maintenance-pid.txt");
                if(name=="timeout")
                {
                    Check(File.Exists(pidFile),"超时夹具确实进入维护入口，未仅在进程启动阶段超时");
                    var pid=int.Parse(File.ReadAllText(pidFile),System.Globalization.CultureInfo.InvariantCulture);var exited=false;
                    try{using var child=Process.GetProcessById(pid);exited=child.HasExited;}catch(ArgumentException){exited=true;}
                    using var lease=new FileStream(Path.Combine(failedData,"plugins",Plugin,"runner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                    Check(exited,"失控维护子进程已退出且目录租约可重新独占");
                }
            }
            finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
        }
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticRealServiceMaintenanceRunnerAndJournal",FormalLocalServiceMigrationVerified=false},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void ParserChecks(string program)
    {
        var path=Path.Combine(program,"release.json");var bytes=File.ReadAllBytes(path);var release=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(bytes)!;
        foreach(var json in new[]{"null","[{}]","[{\"pluginId\":\"crs.fixture\",\"migrationId\":\"synthetic-migration-v1\",\"timeoutSeconds\":0}]","[{\"pluginId\":\"crs.fixture\",\"migrationId\":\"synthetic-migration-v1\",\"timeoutSeconds\":30,\"unknown\":true}]","[{\"pluginId\":\"crs.fixture\",\"pluginId\":\"other\",\"migrationId\":\"synthetic-migration-v1\",\"timeoutSeconds\":30}]"})
        {
            release["storageMigrations"]=JsonSerializer.Deserialize<JsonElement>(json);File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(release));Reject(()=>WorkerStorageMigrationPlans.Read(program),"无效维护计划拒绝："+json);
        }
        File.WriteAllBytes(path,bytes);
    }
    private static string Prepare(string source,string target,string entry,int seconds=30)
    {
        Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        var plugin=Path.Combine(target,"Plugins/crs.fixture/1.0.0");var manifestPath=Path.Combine(plugin,"plugin.json");var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllBytes(manifestPath))!;
        if(entry.Length>0)manifest["storageMigrationEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.FixturePlugin.dll",type="CRS.Worker.FixturePlugin."+entry});
        File.WriteAllBytes(manifestPath,JsonSerializer.SerializeToUtf8Bytes(manifest));
        var files=Directory.GetFiles(plugin,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(plugin,f).Replace('\\','/'),Hash);
        File.WriteAllText(Path.Combine(target,"trusted-background-plugins.json"),JsonSerializer.Serialize(new[]{new{id=Plugin,version="1.0.0",directory="crs.fixture/1.0.0",files}}));
        var allFiles=Directory.GetFiles(target,"*",SearchOption.AllDirectories).Where(f=>Path.GetFileName(f)!="release.json").ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),Hash);
        File.WriteAllText(Path.Combine(target,"release.json"),JsonSerializer.Serialize(new{format=1,product="CRS.Worker.Service",version="2.0.0",files=allFiles,storageMigrations=new[]{new{pluginId=Plugin,migrationId="synthetic-migration-v1",timeoutSeconds=seconds}}}));return target;
    }
    private static WorkerUpgradeRecord Seed(string data,string program)
    {Directory.CreateDirectory(Path.Combine(data,"plugins",Plugin));File.WriteAllText(Path.Combine(data,"plugins",Plugin,"before.raw"),"synthetic-preserved-before");return WorkerUpgradeChecks.Seed(data,program,Plugin,"one");}
    private static string Marker(string data)=>Path.Combine(data,"plugins",Plugin,"synthetic-migration.json");
    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    private static void Save(string data,WorkerUpgradeRecord record)=>File.WriteAllBytes(Path.Combine(data,"maintenance/upgrade.json"),JsonSerializer.SerializeToUtf8Bytes(record));
    private static Process Launch(string program,string data,string pipe)
    {
        var info=new ProcessStartInfo(Path.Combine(program,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var value in new[]{"--console","--data",data,"--pipe",pipe})info.ArgumentList.Add(value);
        var process=Process.Start(info)!;process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();return process;
    }
    private static async Task Running(string program,string data,Func<BackgroundPipeClient,Task> check)
    {
        var pipe="CRS.Migration.Success."+Guid.NewGuid().ToString("N");using var process=Launch(program,data,pipe);
        try{var client=new BackgroundPipeClient(pipe);var ready=await WorkerProcessReadiness.WaitAsync(client,process);Check(ready.Health?.State=="Running","真实隔离服务健康端点已就绪");await check(client);}
        finally{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}
    }
    private static void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 维护迁移宿主："+text);}
    private static void Reject(Action action,string text){try{action();}catch(Exception error) when(error is InvalidDataException or InvalidOperationException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
