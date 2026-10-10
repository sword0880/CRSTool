using System.Security.Cryptography;
using System.Text.Json;
using CRS.Security.Cryptography;
using CRS.Security.Recovery;

namespace CRS.Security.Vault;

public sealed record KeySlot(string Kind,string Kdf,KdfParameters Parameters,byte[] Salt,byte[] Nonce,byte[] Ciphertext,byte[] Tag);
public sealed record VaultEnvelope(int Version,Guid VaultId,KeySlot Password,KeySlot Recovery);
public sealed record VaultCreation(VaultEnvelope Envelope,SecretKey Key,string RecoveryKey);
/// <summary>编排随机数据库密钥的创建、主密码解锁和改密，禁止访问业务或持久化。</summary>
public static class VaultKeyManager
{
    public static VaultCreation Create(string password)
    {
        KeyDerivation.ValidatePassword(password); var dek=RandomNumberGenerator.GetBytes(32); var id=Guid.NewGuid();
        try
        {
            var recovery=RecoveryKeyManager.CreateSlot(dek,id,out var recoveryKey);
            return new(new(1,id,Wrap(dek,password,id),recovery),new(dek),recoveryKey);
        }
        finally {CryptographicOperations.ZeroMemory(dek);}
    }
    public static SecretKey Unlock(VaultEnvelope envelope,string password)
    {
        try
        {
            Validate(envelope); var slot=envelope.Password; var kek=KeyDerivation.Derive(password,slot.Salt,slot.Parameters);
            try {return KeyWrapping.Unwrap(new(slot.Nonce,slot.Ciphertext,slot.Tag),kek,Authentication(envelope.VaultId,slot));}
            finally {CryptographicOperations.ZeroMemory(kek);}
        }
        catch(Exception ex) when(ex is CryptographicException or ArgumentException or FormatException) {throw new CryptographicException("主密码错误或保险库认证失败。");}
    }
    public static VaultEnvelope ChangePassword(VaultEnvelope envelope,SecretKey key,string password)
    {
        Validate(envelope); KeyDerivation.ValidatePassword(password); var dek=key.Copy();
        try {return envelope with {Password=Wrap(dek,password,envelope.VaultId)};}
        finally {CryptographicOperations.ZeroMemory(dek);}
    }
    private static KeySlot Wrap(byte[] dek,string password,Guid id)
    {
        var slot=new KeySlot("Password","Argon2id",new(),RandomNumberGenerator.GetBytes(16),[],[],[]);
        var kek=KeyDerivation.Derive(password,slot.Salt,slot.Parameters);
        try {var wrapped=KeyWrapping.Wrap(dek,kek,Authentication(id,slot)); return slot with {Nonce=wrapped.Nonce,Ciphertext=wrapped.Ciphertext,Tag=wrapped.Tag};}
        finally {CryptographicOperations.ZeroMemory(kek);}
    }
    internal static byte[] Authentication(Guid id,KeySlot slot)=>JsonSerializer.SerializeToUtf8Bytes(new {schema="CRS.KeySlot.v1",id,slot.Kind,slot.Kdf,slot.Parameters,slot.Salt});
    internal static void Validate(VaultEnvelope envelope)
    {
        if(envelope is null || envelope.Version!=1 || envelope.VaultId==Guid.Empty) throw new CryptographicException();
        foreach(var slot in new[]{envelope.Password,envelope.Recovery})
            if(slot is null || slot.Parameters!=new KdfParameters() || slot.Salt is null || slot.Salt.Length!=16 || slot.Nonce is null || slot.Nonce.Length!=12 || slot.Ciphertext is null || slot.Ciphertext.Length!=32 || slot.Tag is null || slot.Tag.Length!=16) throw new CryptographicException();
        if(envelope.Password.Kind!="Password" || envelope.Password.Kdf!="Argon2id" || envelope.Recovery.Kind!="Recovery" || envelope.Recovery.Kdf!="HKDF-SHA256") throw new CryptographicException();
    }
}
