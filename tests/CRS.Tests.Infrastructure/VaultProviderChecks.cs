using CRS.Infrastructure;
using CRS.Application.Abstractions;

internal static class VaultProviderChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS 保险库存储提供方："+name);}
        void Reject(Action work,string name){try{work();throw new Exception("意外允许");}catch(Exception e) when(e is ArgumentException or NotSupportedException or System.Text.Json.JsonException or InvalidDataException){Check(true,name);}}
        var registry=new VaultStorageProviders();
        Reject(()=>registry.Create("postgresql"),"未注册类型明确拒绝且不创建资料");
        Reject(()=>registry.Create("../sqlite"),"非法类型标识拒绝");
        Reject(()=>new VaultStorageProviders([new("SQLCIPHER",_=>registry.Create("sqlcipher"))]),"不能覆盖内置类型");
        var calls=0;
        var custom=new VaultStorageProviders([new("fixture",hello=>{calls++;return registry.Create("sqlcipher",hello);})]);
        using var vault=new VaultManager(databaseProvider:"fixture",providers:custom);
        Check(calls==1&&vault.DatabaseProvider=="fixture"&&!vault.IsUnlocked,"自定义类型只在装配时解析一次");
        var directory=Path.Combine(root,"vault");await vault.CreateAsync(directory,"Synthetic-Provider-Password-2026");
        // 自定义注册仅转发现有SQLCipher族，验证选择链路，不冒充第二数据库。
        Check(vault.Authentication==AuthenticationState.PendingEnrollment,"自定义注册走完整认证入口");
        vault.Lock();Check(!vault.IsUnlocked&&vault.Authentication==AuthenticationState.Locked,"锁定转发到选中提供方");
        var config=Path.Combine(root,"storage.json");
        Check(VaultStorageProviders.ConfiguredProvider(config)=="sqlcipher","缺省配置保留SQLCipher");
        File.WriteAllText(config,"{\"Version\":1,\"Vault\":\"fixture\",\"NationalTeam\":\"sqlcipher\"}");
        Check(VaultStorageProviders.ConfiguredProvider(config)=="fixture","配置选择保险库类型");
        File.WriteAllText(config,"{\"Version\":1,\"Vault\":\"fixture\",\"Vault\":\"sqlcipher\",\"NationalTeam\":\"sqlcipher\"}");
        Reject(()=>VaultStorageProviders.ConfiguredProvider(config),"重复配置字段拒绝");
        File.WriteAllText(config,"{\"Version\":2,\"Vault\":\"fixture\",\"NationalTeam\":\"sqlcipher\"}");
        Reject(()=>VaultStorageProviders.ConfiguredProvider(config),"未知配置版本拒绝");
        Console.WriteLine($"保险库存储提供方通过：{passed}项。");
    }
}
