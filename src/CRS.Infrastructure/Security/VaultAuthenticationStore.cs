using System.Security.Cryptography;
using CRS.Security.Authentication;
using CRS.Security.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CRS.Infrastructure;

/// <summary>仅在受限加密连接中访问认证表；验证与消费在同一个写事务内完成。</summary>
internal static class VaultAuthenticationStore
{
    internal sealed class State
    {
        public string VaultId {get;set;}="";
        public int EnvelopeVersion {get;set;}
        public int SecurityVersion {get;set;}
        public string MfaStatus {get;set;}="";
        public byte[]? TotpSecret {get;set;}
        public byte[]? PendingTotpSecret {get;set;}
        public long? LastAcceptedStep {get;set;}
        public int FailedAttempts {get;set;}
        public long? LockedUntilUtc {get;set;}
        public int InitializationComplete {get;set;}
    }
    internal static void Ensure(VaultSession session,VaultEnvelope envelope)
    {
        using var c=session.Open(); using var tx=c.BeginTransaction();
        if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SecuritySettings'",transaction:tx)==0)
        {
            // 只对已经验证密钥与完整性的加密库补建认证表；旧明文永远不会进入此路径。
            c.Execute("""
                CREATE TABLE SecuritySettings(Id INTEGER PRIMARY KEY CHECK(Id=1),VaultId TEXT NOT NULL,EnvelopeVersion INTEGER NOT NULL,
                SecurityVersion INTEGER NOT NULL,MfaStatus TEXT NOT NULL,TotpSecret BLOB,PendingTotpSecret BLOB,LastAcceptedStep INTEGER,
                FailedAttempts INTEGER NOT NULL DEFAULT 0,LockedUntilUtc INTEGER,InitializationComplete INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE RecoveryCodes(Id TEXT PRIMARY KEY,CodeSalt BLOB NOT NULL,CodeHash BLOB NOT NULL,CreatedAtUtc INTEGER NOT NULL,UsedAtUtc INTEGER);
                CREATE TABLE SecurityEvents(Id TEXT PRIMARY KEY,VaultId TEXT NOT NULL,EventType TEXT NOT NULL,CreatedAtUtc INTEGER NOT NULL,Result TEXT NOT NULL);
                INSERT INTO SecuritySettings(Id,VaultId,EnvelopeVersion,SecurityVersion,MfaStatus) VALUES(1,@VaultId,@Version,1,'Pending');
                """,new {VaultId=envelope.VaultId.ToString("D"),envelope.Version},tx);
        }
        if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('RecoveryCodes','SecurityEvents')",transaction:tx)!=2
            || c.ExecuteScalar<int>("SELECT COUNT(*) FROM SecuritySettings",transaction:tx)!=1) throw new CrsException("保险库认证表不完整。");
        var state=Read(c,tx,envelope); Clear(state); tx.Commit();
    }
    internal static State Read(SqliteConnection c,SqliteTransaction? tx,VaultEnvelope envelope)
    {
        var s=c.QuerySingle<State>("SELECT * FROM SecuritySettings WHERE Id=1",transaction:tx);
        if(s.VaultId!=envelope.VaultId.ToString("D") || s.EnvelopeVersion!=envelope.Version || s.SecurityVersion!=1
            || s.MfaStatus is not ("Pending" or "Enabled" or "RecoveryOnly")
            || s.InitializationComplete is not (0 or 1)
            || s.FailedAttempts is <0 or >5 || (s.PendingTotpSecret is not null && s.PendingTotpSecret.Length!=20)
            || (s.MfaStatus=="Enabled" && s.TotpSecret?.Length!=20)) throw new CrsException("保险库认证状态或身份不一致，禁止访问。");
        return s;
    }
    internal static bool NeedsEnrollment(State state)=>state.MfaStatus!="Enabled" || state.InitializationComplete!=1;
    internal static void Clear(State s) {if(s.TotpSecret is not null) CryptographicOperations.ZeroMemory(s.TotpSecret); if(s.PendingTotpSecret is not null) CryptographicOperations.ZeroMemory(s.PendingTotpSecret);}
    internal static void Event(SqliteConnection c,SqliteTransaction tx,VaultEnvelope envelope,string type,string result,long now)=>
        c.Execute("INSERT INTO SecurityEvents VALUES(@Id,@VaultId,@type,@now,@result)",new {Id=Guid.NewGuid().ToString("N"),VaultId=envelope.VaultId.ToString("D"),type,now,result},tx);
    internal static void CheckCooldown(State s,long now)
    {
        if(s.LockedUntilUtc>now) throw new CrsException("验证码尝试已锁定，请在 15 分钟冷却结束后重试。");
    }
    internal static void Failure(SqliteConnection c,SqliteTransaction tx,VaultEnvelope envelope,State s,long now)
    {
        var attempts=s.LockedUntilUtc is not null && s.LockedUntilUtc<=now?1:s.FailedAttempts+1;
        c.Execute("UPDATE SecuritySettings SET FailedAttempts=@attempts,LockedUntilUtc=@until WHERE Id=1",new {attempts,until=attempts>=5?(long?)(now+900):null},tx);
        Event(c,tx,envelope,"MfaRejected","Failure",now); tx.Commit();
        throw new CrsException(attempts>=5?"验证码连续错误 5 次，已锁定 15 分钟。":"验证码无效、已使用或超出时间窗口，请等待新的验证码。");
    }
    internal static void Consume(VaultSession session,VaultEnvelope envelope,string code,long now)
    {
        using var c=session.Open(); using var tx=c.BeginTransaction(); var s=Read(c,tx,envelope);
        try
        {
            CheckCooldown(s,now);
            if(NeedsEnrollment(s)) throw new CrsException("请先完成手机绑定。");
            var step=Totp.Match(s.TotpSecret!,code,now,s.LastAcceptedStep);
            if(step is null) Failure(c,tx,envelope,s,now);
            if(c.Execute("UPDATE SecuritySettings SET LastAcceptedStep=@step,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1 AND MfaStatus='Enabled' AND InitializationComplete=1 AND (LastAcceptedStep IS NULL OR LastAcceptedStep<@step)",new {step},tx)!=1) throw new CrsException("验证码已使用。");
            Event(c,tx,envelope,"TotpConsumed","Success",now); tx.Commit();
        }
        finally {Clear(s);}
    }
}
