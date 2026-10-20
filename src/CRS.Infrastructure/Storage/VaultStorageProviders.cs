using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>只在装配阶段注册可信实现；注册表不可变，未知类型绝不静默回退。</summary>
public sealed class VaultStorageProviders
{
    public const string DefaultProvider="sqlcipher";
    private readonly Dictionary<string,Func<IWindowsHelloVerifier?,IVaultStorageBackend>> providers;
    public VaultStorageProviders(IEnumerable<KeyValuePair<string,Func<IWindowsHelloVerifier?,IVaultStorageBackend>>>? additional=null)
    {
        providers=new(StringComparer.OrdinalIgnoreCase){{DefaultProvider,hello=>new SqlCipherVaultBackend(hello)}};
        foreach(var item in additional??[])
        {
            Validate(item.Key);ArgumentNullException.ThrowIfNull(item.Value);
            if(!providers.TryAdd(item.Key,item.Value))throw new ArgumentException("重复的保险库数据库类型。");
        }
    }
    public IVaultStorageBackend Create(string provider,IWindowsHelloVerifier? hello=null)
    {
        Validate(provider);
        if(!providers.TryGetValue(provider,out var create))throw new NotSupportedException("保险库数据库类型尚未注册。");
        return create(hello)??throw new InvalidOperationException("保险库存储适配器为空。");
    }
    public static string ConfiguredProvider(string? path=null)
    {
        path??=Path.Combine(AppContext.BaseDirectory,"config","storage-providers.json");
        if(!File.Exists(path))return DefaultProvider;
        var bytes=File.ReadAllBytes(path);
        if(bytes.Length>4096)throw new InvalidDataException("数据库配置过大。");
        using var document=System.Text.Json.JsonDocument.Parse(bytes,new System.Text.Json.JsonDocumentOptions{MaxDepth=4});
        var root=document.RootElement;
        if(root.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new InvalidDataException("数据库配置格式错误。");
        var names=root.EnumerateObject().Select(x=>x.Name).ToArray();
        if(names.Length!=3||names.Distinct(StringComparer.Ordinal).Count()!=3||names.Any(x=>x is not ("Version" or "Vault" or "NationalTeam"))
            ||root.GetProperty("Version").GetInt32()!=1)throw new InvalidDataException("数据库配置版本或字段错误。");
        foreach(var section in new[]{"Vault","NationalTeam"})Validate(root.GetProperty(section).GetString()??"");
        return root.GetProperty("Vault").GetString()!;
    }
    private static void Validate(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>64||value.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_')))
            throw new ArgumentException("数据库类型标识无效。");
    }
}
