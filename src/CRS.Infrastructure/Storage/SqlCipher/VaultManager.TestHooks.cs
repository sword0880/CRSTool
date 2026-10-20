namespace CRS.Infrastructure;

// 仅供现有隔离验收使用，运行入口不依赖具体数据库测试能力。
public sealed partial class VaultManager
{
    private SqlCipherVaultBackend TestBackend=>backend as SqlCipherVaultBackend??throw new NotSupportedException("此提供方不支持SQLCipher测试注入。");
    internal Func<long> Clock {get=>TestBackend.Clock;set=>TestBackend.Clock=value;}
    internal string CodeForTest(bool enrollment=false,long? step=null)=>TestBackend.CodeForTest(enrollment,step);
    internal Microsoft.Data.Sqlite.SqliteConnection OpenForTest()=>TestBackend.OpenForTest();
    internal Task BackupCoreAsync(string path,CancellationToken cancellation=default,Action<CancellationToken>? afterStep=null)=>TestBackend.BackupCoreAsync(path,cancellation,afterStep);
}
