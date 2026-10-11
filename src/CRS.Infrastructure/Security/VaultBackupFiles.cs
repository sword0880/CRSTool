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
    internal static void Write(VaultSession active,VaultEnvelope envelope,string path)
    {
        active.Use(()=>
        {
            active.Verify();
            var snapshot=Path.Combine(active.DirectoryPath,".backup-"+Guid.NewGuid().ToString("N")+".db");
            byte[]? bytes=null; byte[]? body=null;
            try
            {
                using(var source=active.Open()) using(var destination=active.Open(snapshot,true)) source.BackupDatabase(destination);
                using(var c=active.Open(snapshot))
                    if(c.ExecuteScalar<string>("PRAGMA integrity_check")!="ok" || c.Query<string>("PRAGMA cipher_integrity_check").Any(s=>s!="ok"))
                        throw new CrsException("备份快照完整性校验失败。");
                // 在分配正文前限定快照大小；在线快照关闭后不再有写入者。
                if(new FileInfo(snapshot).Length>128*1024*1024) throw new CrsException("当前备份支持最大 128 MiB 加密数据库。");
                bytes=File.ReadAllBytes(snapshot);
                body=JsonSerializer.SerializeToUtf8Bytes(new VaultBackupBody(envelope,bytes,SHA256.HashData(bytes)));
                using var key=active.CloneKey(); var sealedBody=AuthenticatedEncryption.Seal(body,key,Guid.NewGuid());
                var verify=AuthenticatedEncryption.Open(sealedBody,key); CryptographicOperations.ZeroMemory(verify);
                VaultFiles.AtomicWrite(Path.GetFullPath(path),JsonSerializer.SerializeToUtf8Bytes(new VaultBackupFile("CRS.Backup.v1",envelope,sealedBody)));
            }
            finally
            {
                if(bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if(body is not null) CryptographicOperations.ZeroMemory(body);
                foreach(var suffix in new[]{"","-wal","-shm","-journal"}) if(File.Exists(snapshot+suffix)) File.Delete(snapshot+suffix);
            }
        });
    }
}
