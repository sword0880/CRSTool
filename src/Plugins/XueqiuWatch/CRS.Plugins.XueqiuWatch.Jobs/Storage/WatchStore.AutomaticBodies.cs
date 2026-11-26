using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    // 雪球原帖 API 正文只为已发现且尚无正文版本的原帖写入，避免覆盖人工核验结果。
    private bool TrySaveAutomaticBody(string account, string url, DateTimeOffset published, string body,
        PostBodySource source, SqliteTransaction transaction)
    {
        byte[] bytes;
        try { bytes = PostBody.Encode(body); }
        catch (ArgumentException) { return false; }

        if (Scalar("""
            SELECT 1 FROM post_leads p WHERE p.account=$account AND p.url=$url
            AND NOT EXISTS(SELECT 1 FROM post_bodies b WHERE b.account=p.account AND b.url=p.url)
            """, transaction, ("$account", account), ("$url", url)) is null)
            return false;

        WritePostBody(new(account, url), 1, published.ToUnixTimeSeconds(),
            Convert.ToHexString(SHA256.HashData(bytes)), bytes, source, transaction);
        RefreshStockMentions(account, url, transaction);
        return true;
    }
}