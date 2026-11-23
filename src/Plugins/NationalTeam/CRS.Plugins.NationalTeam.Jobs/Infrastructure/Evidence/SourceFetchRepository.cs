// 来源取得记录的聚合转发入口，原件身份、解析版本和失败状态由来源存储统一保存。
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public string RecordFetch(string hash,string sourceUrl,string? originalHash=null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1")
        =>database.RecordFetch(hash,sourceUrl,originalHash,provider,parserVersion);
    public void MarkFailed(string fetchId)=>database.MarkFetchFailed(fetchId);
}
