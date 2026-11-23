using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Infrastructure;

/// <summary>账户期间声明使用真实凭证摘要；只存名称与规范化事实，不复制凭证或本地路径。</summary>
public static class AccountPeriodFiles
{
    public static IReadOnlyList<AccountPeriodEvidence> Read(string path,CancellationToken cancellation=default)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <=0 or >1024*1024) throw new CrsException("账户期间清单必须非空且不超过 1 MiB。");
        var options=new JsonSerializerOptions {RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,PropertyNameCaseInsensitive=false,MaxDepth=16};
        var bytes=new byte[(int)input.Length]; input.ReadExactly(bytes);
        if(input.ReadByte()!=-1) throw new CrsException("账户期间清单在读取期间变化。");
        // 必须在反序列化前拒绝重复字段，避免后一个确认值覆盖前一个值。
        PersistedResultLimits.Validate(bytes,"账户期间清单");
        Document document;
        try {document=JsonSerializer.Deserialize<Document>(bytes,options)??throw new JsonException();}
        catch(JsonException) {throw new CrsException("账户期间清单格式或必填字段无效。");}
        if(document.Version!=1 || document.Accounts is null || document.Accounts.Count is <1 or >1000 || document.Accounts.Any(p=>p is null))
            throw new CrsException("账户期间版本或数量无效。");
        var parent=Path.GetDirectoryName(Path.GetFullPath(path))!;
        var proofs=new Dictionary<string,(string Name,string Hash)>(StringComparer.OrdinalIgnoreCase);
        long total=0;
        return document.Accounts.Select(p=>
        {
            cancellation.ThrowIfCancellationRequested();
            if(!p.Reviewed || string.IsNullOrWhiteSpace(p.ProofPath)) throw new CrsException("请先核对开户／关闭日期及凭证，不能只凭报告文件名确定期间。");
            var full=Path.GetFullPath(p.ProofPath,parent);
            if(!proofs.TryGetValue(full,out var proof))
            {
                var original=FrozenInput.Read(full,cancellation); total+=original.Length;
                if(total>128*1024*1024) throw new CrsException("期间凭证累计超过 128 MiB。");
                proof=(original.Name,original.Sha256); proofs.Add(full,proof);
            }
            return new AccountPeriodEvidence(p.Account,p.Opened,p.Closed,p.OpeningZeroConfirmed,proof.Name,proof.Hash,p.Reviewed);
        }).ToArray();
    }
    private sealed record Document(int Version,List<Row> Accounts);
    private sealed record Row(string Account,DateOnly Opened,DateOnly? Closed,bool OpeningZeroConfirmed,string ProofPath,bool Reviewed);
}
