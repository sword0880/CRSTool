using CRS.Worker.Ipc;
using Microsoft.Data.Sqlite;

namespace CRS.Worker.Service;

/// <summary>
/// 将一次性会话绑定持久授权索引；不是生产 IPC，也不证明快照停写、业务打包或目标恢复已完成。
/// 调用方必须在维护租约内传入已认证身份、已核验的冻结清单，以及只读原钥副本回调。
/// </summary>
internal sealed class DurableRecoveryExportCoordinator : IDisposable
{
    private readonly object gate=new();
    private readonly RecoveryExportJournal journal;
    private readonly Action storageFault;
    private readonly TimeProvider clock;
    private readonly Dictionary<Guid,(RecoveryExportSession Session,long Created)> sessions=[];
    private bool faulted,disposed;

    internal DurableRecoveryExportCoordinator(string directory,ReadOnlySpan<byte> auditKey,Action storageFault,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(storageFault);
        this.storageFault=storageFault;this.clock=clock??TimeProvider.System;
        journal=new(directory,auditKey);
    }

    internal byte[] Prepare(BackgroundCaller caller,Guid prepare,Guid bundle,ReadOnlySpan<byte> digest,Func<byte[]> readExistingKey)
    {
        lock(gate)
        {
            CheckAvailable();
            // 先验证请求形状和管理身份，未授权调用不能消耗持久请求索引。
            if(!caller.CanManage||string.IsNullOrWhiteSpace(caller.Sid)||caller.Sid.Length>184)
                throw new RecoveryExportRefusedException(RecoveryExportFailure.Forbidden);
            if(prepare==Guid.Empty||bundle==Guid.Empty||digest.Length!=32)
                throw new RecoveryExportRefusedException(RecoveryExportFailure.InvalidRequest);
            ArgumentNullException.ThrowIfNull(readExistingKey);
            try
            {
                Expire();journal.Reserve(caller.Sid,prepare,bundle,digest);
                var session=new RecoveryExportSession(caller,prepare,bundle,digest,readExistingKey,journal.Record,clock);
                sessions.Add(prepare,(session,clock.GetTimestamp()));return session.CopyChallenge();
            }
            catch(Exception error) when(StorageError(error)){TripFault();throw;}
        }
    }

    internal byte[] Confirm(BackgroundCaller caller,Guid prepare,Guid confirmation,Guid bundle,ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> challenge,ReadOnlySpan<byte> secret,bool confirmed,CancellationToken token=default)
    {
        lock(gate)
        {
            CheckAvailable();
            if(!sessions.Remove(prepare,out var pending))throw new RecoveryExportRefusedException(RecoveryExportFailure.Consumed);
            using var session=pending.Session;
            try
            {
                if(!journal.Consume(prepare,confirmation))throw new RecoveryExportRefusedException(RecoveryExportFailure.Consumed);
                return session.Confirm(caller,confirmation,bundle,digest,challenge,secret,confirmed,token);
            }
            catch(RecoveryExportRefusedException error)
            {
                // 拒绝也必须落盘；存储失败优先传播，绝不返回已生成但未审计的封装。
                try{journal.Reject(prepare,confirmation,error.Code);}
                catch(Exception failure) when(StorageError(failure)){TripFault();throw;}
                if(error.Code==RecoveryExportFailure.AuditUnavailable)TripFault();
                throw;
            }
            catch(Exception error) when(StorageError(error)){TripFault();throw;}
        }
    }

    private void Expire()
    {
        foreach(var entry in sessions.ToArray())
        {
            var elapsed=clock.GetElapsedTime(entry.Value.Created);
            if(elapsed>=TimeSpan.Zero&&elapsed<RecoveryExportSession.Lifetime)continue;
            sessions.Remove(entry.Key);entry.Value.Session.Dispose();
            journal.Reject(entry.Key,Guid.Empty,RecoveryExportFailure.Expired);
        }
    }
    private void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(faulted)throw new InvalidDataException("恢复审计存储已故障，必须停止新任务并重启核验。");
    }
    private void TripFault()
    {
        if(faulted)return;faulted=true;
        foreach(var pending in sessions.Values)pending.Session.Dispose();sessions.Clear();storageFault();
    }
    private static bool StorageError(Exception error)=>error is SqliteException or IOException or UnauthorizedAccessException;
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            try
            {
                foreach(var entry in sessions)
                {
                    entry.Value.Session.Dispose();
                    if(!faulted)journal.Reject(entry.Key,Guid.Empty,RecoveryExportFailure.Cancelled);
                }
            }
            catch(Exception error) when(StorageError(error)){TripFault();throw;}
            finally{sessions.Clear();journal.Dispose();} // 清理也必须释放独占租约，持久请求索引始终保留。
        }
    }
}
