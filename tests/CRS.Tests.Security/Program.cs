using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using CRS.Security.Recovery;
using CRS.Security.Authentication;

// 使用独立安全程序集验证认证和恢复，不引入 SQLCipher、业务模型或文件适配。
const string password="Synthetic-Strong-Password-2026";
var watch=Stopwatch.StartNew(); var created=VaultKeyManager.Create(password); watch.Stop();
using var key=created.Key; using var unlocked=VaultKeyManager.Unlock(created.Envelope,password);
using var recovered=RecoveryKeyManager.Recover(created.Envelope,created.RecoveryKey);
var original=key.Copy(); var comparison=unlocked.Copy(); var recovery=recovered.Copy();
try {if(!CryptographicOperations.FixedTimeEquals(original,comparison) || !CryptographicOperations.FixedTimeEquals(original,recovery)) throw new Exception("密码和恢复解封结果不一致。");}
finally {CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(comparison); CryptographicOperations.ZeroMemory(recovery);}
void Reject(Action action) {try {action();} catch(CryptographicException) {return;} throw new Exception("预期认证失败却被接受。");}
Reject(()=>VaultKeyManager.Unlock(created.Envelope,"incorrect-password"));
Reject(()=>RecoveryKeyManager.Recover(created.Envelope,new string('0',64)));
Reject(()=>VaultKeyManager.Unlock(created.Envelope with {VaultId=Guid.NewGuid()},password));
Reject(()=>VaultKeyManager.Unlock(created.Envelope with {Version=2},password));
Reject(()=>VaultKeyManager.Unlock(created.Envelope with {Password=created.Envelope.Password with {Parameters=new(999999999)}},password));
var changed=VaultKeyManager.ChangePassword(created.Envelope,key,"New-Synthetic-Password-2026");
Reject(()=>VaultKeyManager.Unlock(changed,password)); using var newKey=VaultKeyManager.Unlock(changed,"New-Synthetic-Password-2026");
using var stillRecovered=RecoveryKeyManager.Recover(changed,created.RecoveryKey);
var payload=AuthenticatedEncryption.Seal(Encoding.UTF8.GetBytes("synthetic-private-data"),key,Guid.NewGuid());
var plain=AuthenticatedEncryption.Open(payload,newKey); if(Encoding.UTF8.GetString(plain)!="synthetic-private-data") throw new Exception("加密容器未保持正文。"); CryptographicOperations.ZeroMemory(plain);
payload.Ciphertext[0]^=1; Reject(()=>AuthenticatedEncryption.Open(payload,key));
if(JsonSerializer.Serialize(created.Envelope).Contains(password) || JsonSerializer.Serialize(created.Envelope).Contains(created.RecoveryKey)) throw new Exception("元数据泄露口令或恢复秘密。");
key.Dispose(); try {key.Copy(); throw new Exception("释放后仍可取密钥。");} catch(ObjectDisposedException){}
if(typeof(VaultKeyManager).Assembly.GetReferencedAssemblies().Any(a=>a.Name?.StartsWith("CRS.")==true)) throw new Exception("安全核心引用了业务或 UI。");
Console.WriteLine($"Security 验证通过：主密码、恢复、错误凭证、元数据篡改、KDF 上限、改密、容器篡改、释放及项目独立性（12 项）；创建含首次派生 {watch.ElapsedMilliseconds} ms，Argon2id 65536 KiB / 3 / 2。");
// RFC6238 附录 B 的全部 SHA1 标准向量（八位）；生产固定使用相同算法的六位配置。
var rfcSecret=Encoding.ASCII.GetBytes("12345678901234567890");
foreach(var (time,expected) in new (long,string)[]{(59,"94287082"),(1111111109,"07081804"),(1111111111,"14050471"),(1234567890,"89005924"),(2000000000,"69279037"),(20000000000,"65353130")})
    if(Totp.Compute(rfcSecret,time/30,8)!=expected) throw new Exception("RFC6238 SHA1 向量失败。");
var zero=Totp.Compute(rfcSecret,1111111109/30); if(zero!="081804") throw new Exception("六位前导零丢失。");
if(Totp.Match(rfcSecret,zero,1111111109,1111111109/30) is not null || Totp.Match(rfcSecret,zero,1111111109+60,null) is not null
    || Totp.Match(rfcSecret,zero,1111111109+30,null) is null || Totp.Match(rfcSecret,"０８１８０４",1111111109,null) is not null) throw new Exception("验证码窗口、防重放或 ASCII 限制失败。");
var uri=Totp.EnrollmentUri(rfcSecret,Guid.NewGuid()); if(!uri.StartsWith("otpauth://totp/CRS%20Desktop%3A") || !uri.Contains("secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ") || !uri.EndsWith("algorithm=SHA1&digits=6&period=30")) throw new Exception("二维码绑定 URI 参数错误。");
Console.WriteLine("TOTP 核心通过：RFC6238 SHA1 六组向量、六位前导零、窗口、已消费拒绝、非 ASCII 拒绝、Base32 与 URI 参数。");
