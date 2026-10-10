using System.Security.Cryptography;
using CRS.Security.Cryptography;
using CRS.Security.Vault;

namespace CRS.Security.Recovery;

/// <summary>独立高熵恢复凭证，不派生自主密码、不绑定 Windows 身份，没有默认或万能恢复密钥。</summary>
public static class RecoveryKeyManager
{
    internal static KeySlot CreateSlot(byte[] dek,Guid id,out string recoveryKey)
    {
        var secret=RandomNumberGenerator.GetBytes(32); var slot=new KeySlot("Recovery","HKDF-SHA256",new(),RandomNumberGenerator.GetBytes(16),[],[],[]);
        var aad=VaultKeyManager.Authentication(id,slot); var kek=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,slot.Salt,aad);
        try {var wrapped=KeyWrapping.Wrap(dek,kek,aad); recoveryKey=Convert.ToHexString(secret); return slot with {Nonce=wrapped.Nonce,Ciphertext=wrapped.Ciphertext,Tag=wrapped.Tag};}
        finally {CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(kek);}
    }
    public static SecretKey Recover(VaultEnvelope envelope,string recoveryKey)
    {
        byte[]? secret=null; byte[]? kek=null;
        try
        {
            VaultKeyManager.Validate(envelope); if(recoveryKey is null || recoveryKey.Length>128) throw new CryptographicException();
            secret=Convert.FromHexString(recoveryKey.Replace("-", "").Replace(" ", "").Trim()); if(secret.Length!=32) throw new CryptographicException();
            var slot=envelope.Recovery; var aad=VaultKeyManager.Authentication(envelope.VaultId,slot); kek=HKDF.DeriveKey(HashAlgorithmName.SHA256,secret,32,slot.Salt,aad);
            return KeyWrapping.Unwrap(new(slot.Nonce,slot.Ciphertext,slot.Tag),kek,aad);
        }
        catch(Exception ex) when(ex is CryptographicException or ArgumentException or FormatException) {throw new CryptographicException("恢复密钥错误或保险库认证失败。");}
        finally {if(secret is not null) CryptographicOperations.ZeroMemory(secret); if(kek is not null) CryptographicOperations.ZeroMemory(kek);}
    }
}
