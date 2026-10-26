using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EtfStore
{
    public List<EvidenceRow> Evidence(EtfKey? key,DateOnly? from,DateOnly? to,int maximumRows)
    {
        if(maximumRows is <1 or >10000||from>to)throw new ArgumentException("证据查询范围或数量无效。");
        // 多读一行只用于判断超限；先拒绝超量结果，再读取原件和序列化，避免先构造整库快照。
        var rows=ReadEvidence(key,from,to,maximumRows+1);
        if(rows.Count>maximumRows)throw new InvalidDataException("证据结果超过限额，请缩小证券或日期范围。");
        var verified=raw.VerifyMany(rows.SelectMany(x=>new[]{x.Hash,x.OriginalHash}),operationCancellation);
        if(verified.Values.Any(x=>!x))throw new InvalidDataException("Evidence is missing or damaged.");
        return rows;
    }
}
