using System.Security.Cryptography;
using System.Text;
using CRS.Security.Cryptography;

/// <summary>独立导出密码、用途隔离、认证和未知参数拒绝，不依赖文件系统。</summary>
internal static class PasswordExportChecks
{
    public static void Run()
    {
        const string password="Synthetic-Export-Password-2026";
        var data=Encoding.UTF8.GetBytes("SYNTHETIC-PRIVATE-PAYLOAD");
        var payload=PasswordEncryption.Seal(data,password); var plain=PasswordEncryption.Open(payload,password);
        if(!plain.SequenceEqual(data)) throw new Exception("导出密码往返失败。"); CryptographicOperations.ZeroMemory(plain);
        Reject(()=>PasswordEncryption.Open(payload,"Incorrect-Export-Password"));
        Reject(()=>PasswordEncryption.Seal(data,"short"));
        Reject(()=>PasswordEncryption.Open(payload with {Parameters=new(65536,999,2)},password));
        Reject(()=>PasswordEncryption.Open(payload with {Version=2},password));
        // 相同原始密钥也不能将导出密文当作保险库备份解密。
        var raw=KeyDerivation.Derive(password,payload.Salt,payload.Parameters);
        using(var key=new SecretKey(raw)) Reject(()=>AuthenticatedEncryption.Open(payload.Body,key));
        CryptographicOperations.ZeroMemory(raw);
        payload.Body.Tag[0]^=1; Reject(()=>PasswordEncryption.Open(payload,password));
        Console.WriteLine("独立导出加密通过：密码往返、错误密码、短密码、参数上限、版本、用途隔离及认证篡改。");
    }
    private static void Reject(Action action) {try {action();} catch(CryptographicException) {return;} throw new Exception("加密导出未拒绝非法输入。");}
}
