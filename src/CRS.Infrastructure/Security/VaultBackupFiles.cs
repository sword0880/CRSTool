using System.Security.Cryptography;
using System.Text.Json;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using Dapper;

namespace CRS.Infrastructure;

internal sealed record VaultBackupFile(string Schema,VaultEnvelope Envelope,SealedPayload Payload);
internal sealed record VaultBackupBody(VaultEnvelope Envelope,byte[] Database,byte[] Sha256);
/// <summary>手动备份与结构升级复用相同可恢复容器，禁止复制正在写入的主数据库文件。</summary>
internal static class VaultBackupFiles
{
    internal static void Write(VaultSession active,VaultEnvelope envelope,string path,CancellationToken cancellation=default,Action<CancellationToken>? afterStep=null)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,active.Lifetime);
        cancellation=linked.Token;
        active.Use(()=>
        {
            cancellation.ThrowIfCancellationRequested();
            // 页数包含 WAL 中的逻辑增长；超限时在创建在线快照前拒绝，不只检查主文件大小。
            using (var source = active.Open())
                if (source.ExecuteScalar<long>("PRAGMA page_count") * source.ExecuteScalar<long>("PRAGMA page_size") > ChunkedVaultBackup.MaxDatabaseBytes)
                    throw new CrsException("数据库超过 2 GiB 备份上限，请先处理容量；原库未修改。");
            active.Verify();
            var snapshot=Path.Combine(active.DirectoryPath,".backup-"+Guid.NewGuid().ToString("N")+".db");
            try
            {
                using(var source=active.Open()) using(var destination=active.Open(snapshot,true)) CancellableDatabaseBackup.Copy(source,destination,cancellation,afterStep);
                using(var c=active.Open(snapshot))
                    CancellableDatabaseBackup.Verify(c,cancellation);
                using var key=active.CloneKey();
                using var database = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                VaultFiles.AtomicWrite(Path.GetFullPath(path), destination =>
                {
                    ChunkedVaultBackup.Write(database, destination, envelope, key,cancellation);
                    // 对实际写出的容器完整回读认证，不能只证明写入前的内存密文可解密。
                    destination.Flush(true); destination.Position = 0;
                    var header = ChunkedVaultBackup.ReadHeader(destination);
                    ChunkedVaultBackup.Extract(destination, Stream.Null, ChunkedVaultBackup.Authenticate(header, key), key,cancellation);
                },cancellation:cancellation);
            }
            finally
            {
                foreach(var suffix in new[]{"","-wal","-shm","-journal"}) if(File.Exists(snapshot+suffix)) File.Delete(snapshot+suffix);
            }
        });
    }
}
