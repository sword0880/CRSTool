using System.Security.Cryptography;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using CRS.Security.Recovery;
using Microsoft.Data.Sqlite;
using Dapper;

namespace CRS.Infrastructure;

/// <summary>不可重新激活的解锁会话；整个数据库操作持有门闩，锁定返回后旧任务不能再写入。</summary>
internal sealed class VaultSession(string directory,SecretKey key) : IDisposable
{
    private readonly object gate=new();
    private bool closed;
    internal string DirectoryPath {get;}=Path.GetFullPath(directory);
    internal IDisposable Enter()
    {
        Monitor.Enter(gate);
        if (closed) { Monitor.Exit(gate); throw new CrsException("保险库已锁定，请重新解锁。"); }
        return new Lease(gate);
    }
    private sealed class Lease(object gate) : IDisposable { public void Dispose()=>Monitor.Exit(gate); }
    internal T Use<T>(Func<T> action) { lock(gate) { if(closed) throw new CrsException("保险库已锁定，请重新解锁。"); return action(); } }
    internal void Use(Action action)=>Use(()=> { action(); return true; });
    internal byte[] CopyKey()=>Use(key.Copy);
    internal SecretKey CloneKey()=>Use(key.Clone);
    internal SqliteConnection Open(string? path=null,bool create=false)
    {
        return Use(()=>
        {
            var database=path??Path.Combine(DirectoryPath,"crs.db");
            if (!create) VaultFiles.RejectPlaintext(database);
            var c=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=database,Pooling=false,
                Mode=create?SqliteOpenMode.ReadWriteCreate:SqliteOpenMode.ReadWrite }.ToString());
            var raw=key.Copy(); var encoded=new byte[67];
            try
            {
                c.Open();
                // SQLCipher 必须在任何业务 SQL 前设置随机二进制 DEK，连接字符串从不包含密钥。
                // SQLCipher 的二进制 API 仍将普通字节视为口令；按官方 raw-key 格式编码，避免每次连接重复 PBKDF2。
                encoded[0]=(byte)'x'; encoded[1]=(byte)'\''; encoded[66]=(byte)'\'';
                const string hex="0123456789ABCDEF";
                for(var i=0;i<raw.Length;i++) {encoded[2+i*2]=(byte)hex[raw[i]>>4]; encoded[3+i*2]=(byte)hex[raw[i]&15];}
                if (SQLitePCL.raw.sqlite3_key(c.Handle,encoded)!=SQLitePCL.raw.SQLITE_OK) throw new CrsException("数据库密钥设置失败。");
                if (string.IsNullOrWhiteSpace(c.ExecuteScalar<string>("PRAGMA cipher_version"))) throw new CrsException("SQLCipher 原生提供器不可用，禁止回退明文数据库。");
                c.Execute("PRAGMA cipher_compatibility=4; PRAGMA cipher_memory_security=ON; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON;");
                c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master");
                return c;
            }
            catch { c.Dispose(); throw new CrsException("加密数据库无法验证，请检查保险库格式、凭证和完整性。"); }
            finally { CryptographicOperations.ZeroMemory(raw); CryptographicOperations.ZeroMemory(encoded); }
        });
    }
    internal void Verify()
    {
        Use(()=>
        {
            using var c=Open();
            if (c.Query<string>("PRAGMA cipher_integrity_check").Any(s=>s!="ok") || c.ExecuteScalar<string>("PRAGMA integrity_check")!="ok")
                throw new CrsException("加密数据库完整性检查失败。");
        });
    }
    public void Dispose() { lock(gate) { if(closed) return; closed=true; key.Dispose(); } }
}
