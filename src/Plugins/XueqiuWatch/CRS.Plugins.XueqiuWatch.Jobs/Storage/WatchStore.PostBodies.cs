using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private readonly Dictionary<Guid, BodyUpload> bodyUploads = [];

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
            SELECT revision,length(body),sha256,published FROM post_bodies
            WHERE account=$account AND url=$url AND ($revision=0 OR revision=$revision)
            ORDER BY revision DESC LIMIT 1
            """, transaction, ("$account", post.Account), ("$url", post.Url), ("$revision", revision));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), row.GetInt32(1), row.GetString(2), BeijingTime(row.GetInt64(3))) : null;
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

    private WatchReply BeginPostBody(PostBodyRequest? request)
    {
        var post = ParsePost(request);
        if (request!.UploadId == Guid.Empty || request.Length is < 1 or > PostBody.MaximumBytes ||
            request.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
            request.PublishedAt is not { } published || published.Year < 2005 ||
            published > WatchProtocol.BeijingNow.AddMinutes(5))
            throw new ArgumentException("正文长度、指纹或原帖时间无效。");

        RequireAuthor(post.Account);
        var now = DateTimeOffset.UtcNow;
        foreach (var id in bodyUploads.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            bodyUploads.Remove(id);

        var header = new UploadHeader(post, request.Revision, published.ToUnixTimeSeconds(), request.Length, hash.ToUpperInvariant());
        if (bodyUploads.TryGetValue(request.UploadId, out var existing))
        {
            if (existing.Header != header) throw new ArgumentException("正文上传参数已改变，请重新保存。");
            return new();
        }
        if (bodyUploads.Count >= 4) throw new ArgumentException("正在接收其他正文，请等候或取消后再试。");
        var latest = ReadPostBodyInfo(post, 0, null);
        if ((latest?.Revision ?? 0) != header.ExpectedRevision)
            throw new ArgumentException("正文已有更新，请重新打开后保存。");
        bodyUploads.Add(request.UploadId, new(header, now.AddMinutes(10)));
        return new();
    }

    private WatchReply AppendPostBody(PostBodyRequest? request)
    {
        var upload = RequireBodyUpload(request);
        if (request!.Bytes is not { Length: > 0 and <= PostBody.UploadChunkBytes } bytes ||
            request.Offset < 0 || request.Offset > upload.Received || bytes.Length > upload.Buffer.Length - request.Offset)
            throw new ArgumentException("正文分块或位置无效。");
        if (request.Offset < upload.Received)
        {
            if (bytes.Length > upload.Received - request.Offset ||
                !upload.Buffer.AsSpan(request.Offset, bytes.Length).SequenceEqual(bytes))
                throw new ArgumentException("重复分块的内容不同，请重新上传。");
            return new();
        }
        bytes.CopyTo(upload.Buffer, request.Offset);
        upload.Received += bytes.Length;
        return new();
    }

    private void CommitPostBody(PostBodyRequest? request, SqliteTransaction transaction)
    {
        var upload = RequireBodyUpload(request);
        var header = upload.Header;
        if (upload.Received != header.Length ||
            Convert.ToHexString(SHA256.HashData(upload.Buffer)) != header.Sha256)
            throw new ArgumentException("正文上传未完成或校验失败。");
        PostBody.Decode(upload.Buffer);

        var latest = ReadPostBodyInfo(header.Post, 0, transaction);
        if (latest is not null && latest.Sha256 == header.Sha256 &&
            latest.PublishedAt.ToUnixTimeSeconds() == header.Published)
            return;
        var revision = latest?.Revision ?? 0;
        if (revision != header.ExpectedRevision) throw new ArgumentException("正文已有更新，请重新打开后保存。");
        Execute("""
            INSERT INTO post_bodies(account,url,revision,published,sha256,body)
            VALUES($account,$url,$revision,$published,$sha256,$body)
            """, transaction, ("$account", header.Post.Account), ("$url", header.Post.Url),
            ("$revision", revision + 1), ("$published", header.Published), ("$sha256", header.Sha256), ("$body", upload.Buffer));
    }

    private WatchReply CancelPostBody(PostBodyRequest? request)
    {
        if (request is not null) bodyUploads.Remove(request.UploadId);
        return new();
    }

    private BodyUpload RequireBodyUpload(PostBodyRequest? request)
    {
        if (request is null || !bodyUploads.TryGetValue(request.UploadId, out var upload) ||
            upload.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("正文上传已失效，请重新保存。");
        return upload;
    }

    private sealed record UploadHeader(PostKey Post, long ExpectedRevision, long Published, int Length, string Sha256);

    private sealed class BodyUpload(UploadHeader header, DateTimeOffset expiresAt)
    {
        internal UploadHeader Header { get; } = header;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal byte[] Buffer { get; } = new byte[header.Length];
        internal int Received { get; set; }
    }
}
