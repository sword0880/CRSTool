using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>市场仓储和来源策略作为同一存储族选取，禁止跨提供方静默混搭。</summary>
public interface INationalTeamStorageProvider
{
    IMarketRepository OpenMarket(string directory,RawEvidenceStore raw,byte[] key);
    ISourceRequestPolicy OpenSourcePolicy(string directory);
}
public sealed class NationalTeamStorageProviders
{
    public const string DefaultProvider="sqlite";
    private readonly Dictionary<string,Func<INationalTeamStorageProvider>> providers;
    public NationalTeamStorageProviders(IEnumerable<KeyValuePair<string,Func<INationalTeamStorageProvider>>>? additional=null)
    {
        providers=new(StringComparer.OrdinalIgnoreCase){{DefaultProvider,()=>new SqliteNationalTeamStorage()}};
        foreach(var item in additional??[])
        {
            Validate(item.Key);ArgumentNullException.ThrowIfNull(item.Value);
            if(!providers.TryAdd(item.Key,item.Value))throw new ArgumentException("重复的插件数据库类型。");
        }
    }
    public INationalTeamStorageProvider Create(string provider)
    {
        Validate(provider);
        if(!providers.TryGetValue(provider,out var create))throw new NotSupportedException("插件数据库类型尚未注册。");
        return create()??throw new InvalidOperationException("插件存储适配器为空。");
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
        return root.GetProperty("NationalTeam").GetString()!;
    }
    private static void Validate(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>64||value.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_')))
            throw new ArgumentException("数据库类型标识无效。");
    }
    private sealed class SqliteNationalTeamStorage:INationalTeamStorageProvider
    {
        public IMarketRepository OpenMarket(string directory,RawEvidenceStore raw,byte[] key)=>new MarketRepository(directory,raw,approvalAuditKey:key);
        public ISourceRequestPolicy OpenSourcePolicy(string directory)=>new SourceRequestPolicy(directory);
    }
}
