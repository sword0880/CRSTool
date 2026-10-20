using CRS.Security.Cryptography;
using System.Security.Cryptography;
using Dapper;

namespace CRS.Infrastructure;

internal sealed partial class SqlCipherVaultBackend
{
    public string DocumentEncryptionPassword
    {
        get {lock(gate)
        {
            using var c=Required().Open();
            if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='document_export_settings'")==0)return "";
            return c.ExecuteScalar<string>("SELECT password FROM document_export_settings WHERE id=1")??"";
        }}
    }
    public Task SetDocumentEncryptionPasswordAsync(string password)
    {
        VaultSession expected;lock(gate)expected=Required();
        return Task.Run(()=> {lock(gate)
        {
            if(!ReferenceEquals(session,expected)) throw new CrsException("原安全会话已失效。");
            try {PasswordEncryption.ValidatePassword(password);} catch(CryptographicException) {throw new CrsException("文档加密密码需为 12—1024 个字符。");}
            using var c=Required().Open();using var tx=c.BeginTransaction();
            c.Execute("CREATE TABLE IF NOT EXISTS document_export_settings(id INTEGER PRIMARY KEY CHECK(id=1),password TEXT NOT NULL CHECK(length(password) BETWEEN 1 AND 1024)); INSERT INTO document_export_settings(id,password) VALUES(1,@password) ON CONFLICT(id) DO UPDATE SET password=excluded.password",new{password},tx);
            VaultAuthenticationStore.Event(c,tx,envelope!,"DocumentEncryptionPasswordChanged","Success",Now());tx.Commit();
        }});
    }
}
