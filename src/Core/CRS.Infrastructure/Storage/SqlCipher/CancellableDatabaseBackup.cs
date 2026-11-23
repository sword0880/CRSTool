using Dapper;
using Microsoft.Data.Sqlite;
using static SQLitePCL.raw;

namespace CRS.Infrastructure;

/// <summary>原生在线备份分步执行；忙锁等待有期限，完整性扫描也能收到锁定中断。</summary>
internal static class CancellableDatabaseBackup
{
    internal static void Copy(SqliteConnection source,SqliteConnection target,CancellationToken cancellation,Action<CancellationToken>? afterStep=null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var backup=sqlite3_backup_init(target.Handle,"main",source.Handle,"main");
        if(backup.IsInvalid) throw new CrsException("无法建立加密在线备份。");
        var waiting=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            var rc=sqlite3_backup_step(backup,128);
            afterStep?.Invoke(cancellation);
            if(rc==SQLITE_DONE) break;
            if(rc==SQLITE_OK) {waiting.Restart(); continue;}
            if(rc is not (SQLITE_BUSY or SQLITE_LOCKED)) throw new CrsException("加密在线备份失败，原库未修改。");
            if(waiting.Elapsed>TimeSpan.FromSeconds(30)) throw new CrsException("数据库持续忙碌，未提交备份，请稍后重试。");
            // 取消会提前结束短等待，不通过无限重试延迟锁定。
            cancellation.WaitHandle.WaitOne(25);
        }
        cancellation.ThrowIfCancellationRequested();
    }
    internal static void Verify(SqliteConnection connection,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        sqlite3_progress_handler(connection.Handle,1000,_=>cancellation.IsCancellationRequested?1:0,null);
        try
        {
            if(connection.Query<string>("PRAGMA cipher_integrity_check").Any(s=>s!="ok")
                || connection.ExecuteScalar<string>("PRAGMA integrity_check")!="ok")
                throw new CrsException("加密数据库完整性检查失败。");
            cancellation.ThrowIfCancellationRequested();
        }
        catch(Exception) when(cancellation.IsCancellationRequested) {throw new OperationCanceledException(cancellation);}
        finally {sqlite3_progress_handler(connection.Handle,0,null,null);}
    }
}
