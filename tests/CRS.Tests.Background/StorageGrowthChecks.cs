using System.Security.AccessControl;
using System.Security.Principal;
using CRS.Worker.Service;

/// <summary>真实NTFS目录增长验证；随机测试树超十万项，绝不访问或清理正式Worker目录。</summary>
internal static class StorageGrowthChecks
{
    public static async Task RunAsync(string root)
    {
        StorageSecurity.Prepare(root,true);
        // 将文件分散到子目录，避免单目录枚举特性替代整个数据树的容量验证。
        for(var group=0;group<101;group++)
        {
            var directory=Path.Combine(root,"part-"+group);Directory.CreateDirectory(directory);
            for(var i=0;i<1000;i++)using(File.Create(Path.Combine(directory,i+".raw"))){}
            if(group%25==0)Console.WriteLine("PROFILE 存储增长：已创建"+((group+1)*1000)+"个隔离空文件");
        }
        var count=StorageSecurity.Scan(root,true);
        if(count<=100000)throw new Exception("未实际跨越旧十万项上限");
        Console.WriteLine("PASS 存储增长："+count+"个合法目录项通过完整ACL核验，不因数量而停止");
        using(var cancel=new CancellationTokenSource())
        {
            var warning=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor=StorageSecurity.MonitorAsync(root,true,n=>warning.TrySetResult(n),cancel.Token,TimeSpan.FromMilliseconds(10));
            var observed=await warning.Task.WaitAsync(TimeSpan.FromMinutes(2));cancel.Cancel();
            try{await monitor;}catch(OperationCanceledException){}
            if(observed!=count)throw new Exception("容量预警计数不一致");
            Console.WriteLine("PASS 存储增长：独立巡检发出容量预警，并可正常取消");
        }
        using(var cancel=new CancellationTokenSource())
        {cancel.Cancel();try{StorageSecurity.Scan(root,true,cancel.Token);throw new Exception("巡检忽略取消");}catch(OperationCanceledException){Console.WriteLine("PASS 存储增长：启动与全量巡检响应取消");}}
        var file=new FileInfo(Path.Combine(root,"part-0","0.raw"));var acl=file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),FileSystemRights.ReadData,AccessControlType.Allow));file.SetAccessControl(acl);
        try{StorageSecurity.Scan(root,true);throw new Exception("额外读取权限被放行");}catch(StorageSecurityException){Console.WriteLine("PASS 存储增长：取消容量停服后，真实ACL漂移仍然失败关闭");}
        // 调用者保留隔离目录作为诊断证据，不将删除策略引入生产存证生命周期。
    }
}
