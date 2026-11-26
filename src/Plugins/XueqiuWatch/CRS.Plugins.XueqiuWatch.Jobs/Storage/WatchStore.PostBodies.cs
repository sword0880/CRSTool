using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializePostBodies()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE post_bodies(
                account TEXT NOT NULL REFERENCES authors(account),
                url TEXT NOT NULL, revision INTEGER NOT NULL, published INTEGER NOT NULL,
                sha256 TEXT NOT NULL, body BLOB NOT NULL,
                PRIMARY KEY(account,url,revision)
            );
            PRAGMA user_version=9;
            """, transaction);
        transaction.Commit();
    }

    private readonly record struct PostKey(string Account, string Url);

    private static PostKey ParsePost(PostBodyRequest? request)
    {
        if (request is null || request.Revision < 0) throw new ArgumentException("原帖或正文版本无效。");
        var account = WatchInput.AccountId(request.Account);
        var url = WatchInput.SourceLink(request.Url);
        if (!url.StartsWith($"https://xueqiu.com/{account}/", StringComparison.Ordinal))
            throw new ArgumentException("原帖链接与作者账号不一致。");
        return new(account, url);
    }

    private PostBodyInfo? ReadPostBodyInfo(PostBodyRequest? request, SqliteTransaction? transaction = null) =>
        ReadPostBodyInfo(ParsePost(request), request!.Revision, transaction);

    private PostBodyInfo? ReadPostBodyInfo(PostKey post, long revision, SqliteTransaction? transaction)
    {
        using var command = Command("""
            SELECT revision,length(body),sha256,published,origin FROM post_bodies
            WHERE account=$account AND url=$url AND ($revision=0 OR revision=$revision)
            ORDER BY revision DESC LIMIT 1
            """, transaction, ("$account", post.Account), ("$url", post.Url), ("$revision", revision));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), row.GetInt32(1), row.GetString(2),
            BeijingTime(row.GetInt64(3)), (PostBodySource)row.GetInt32(4)) : null;
    }

    private WatchReply ReadPostBodyChunk(PostBodyRequest? request)
    {
        var post = ParsePost(request);
        if (request!.Revision <= 0 || request.Offset < 0 || request.Offset >= PostBody.MaximumBytes)
            throw new ArgumentException("正文读取位置或版本无效。");
        if (Scalar("""
            SELECT substr(body,$offset+1,$size) FROM post_bodies
            WHERE account=$account AND url=$url AND revision=$revision AND length(body)>$offset
            """, null, ("$account", post.Account), ("$url", post.Url), ("$revision", request.Revision),
            ("$offset", request.Offset), ("$size", PostBody.ReadChunkBytes)) is not byte[] bytes)
            throw new ArgumentException("找不到正文版本或读取位置。");
        return new() { BodyBytes = bytes };
    }

    private void WritePostBody(PostKey post, long revision, long published, string hash, byte[] body,
        PostBodySource origin, SqliteTransaction transaction) => Execute("""
            INSERT INTO post_bodies(account,url,revision,published,sha256,body,origin)
            VALUES($account,$url,$revision,$published,$sha256,$body,$origin)
            """, transaction, ("$account", post.Account), ("$url", post.Url), ("$revision", revision),
            ("$published", published), ("$sha256", hash), ("$body", body), ("$origin", (int)origin));

}
