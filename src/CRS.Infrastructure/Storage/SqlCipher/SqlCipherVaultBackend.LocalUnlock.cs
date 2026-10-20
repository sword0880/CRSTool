using System.Diagnostics;
using CRS.Security.Cryptography;

namespace CRS.Infrastructure;

internal sealed partial class SqlCipherVaultBackend
{
    /// <summary>探测本机槽是否可由当前 Windows 用户解封，不打开业务仓储。</summary>
    public bool HasLocalKey(string directory)
    {
        try
        {
            if(Inspect(directory)!=CRS.Application.Abstractions.VaultState.Encrypted) return false;
            var metadata=VaultFiles.ReadEnvelope(directory); using var key=DeviceKeyStore.Read(directory,metadata.VaultId); return true;
        }
        catch {return false;}
    }
    /// <summary>日常进入不索取主密码，Windows 用户密钥仅用于准备受限 TOTP 验证。</summary>
    public Task BeginLocalUnlockAsync(string directory)=>Task.Run(()=>
    {
        lock(gate)
        {
            if(session is not null) throw new CrsException("当前保险库已经解锁。");
            ClearPending(); VaultFiles.RejectPlaintext(Path.Combine(directory,"crs.db")); var metadata=VaultFiles.ReadEnvelope(directory);
            VaultSession? candidate=null;
            try
            {
                candidate=new VaultSession(directory,DeviceKeyStore.Read(directory,metadata.VaultId)); candidate.Verify();
                // 本机入口只能读取已有认证表，不得把未知库自动升级为免密码的新库。
                using(var c=candidate.Open())
                {
                    var state=VaultAuthenticationStore.Read(c,null,metadata);
                    VaultAuthenticationStore.Clear(state);
                }
                envelope=metadata; pending=candidate; pendingStarted=Stopwatch.GetTimestamp(); candidate=null;
                // 兼容旧的关闭状态，但日常进入仍必须完成手机验证。
            }
            finally {candidate?.Dispose();}
        }
    });
    private void RememberLocalKey(VaultSession candidate)
    {
        using SecretKey key=candidate.CloneKey(); DeviceKeyStore.Save(candidate.DirectoryPath,envelope!.VaultId,key);
    }
}
