using System.Security.Cryptography;
using System.Text;
using CRS.Worker.Abstractions;
namespace CRS.Worker.Service;

/// <summary>每个插件使用独立私有目录和域分离的密钥副本，主密钥不交给业务实现。</summary>
public sealed class WorkerPluginContext(string id,string root,byte[] masterKey):IBackgroundHostContext
{
    public string PluginId=>id;
    public string DataDirectory=>Path.Combine(root,"plugins",id);
    public byte[] OpenDataKey()=>HKDF.DeriveKey(HashAlgorithmName.SHA256,masterKey,32,
        info:Encoding.UTF8.GetBytes("CRS.Worker.Plugin.Data/v1/"+id));
}
