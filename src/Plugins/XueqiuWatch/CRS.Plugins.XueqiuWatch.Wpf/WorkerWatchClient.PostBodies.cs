using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Ipc;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed partial class WorkerWatchClient
{
    internal async Task<PostBodyDocument?> ReadPostBodyAsync(PostBodyRequest post, CancellationToken token)
    {
        var info = (await AskAsync(new("post-body-info", Body: post), token)).Body;
        if (info is null) return null;
        if (info.Length is < 1 or > PostBody.MaximumBytes || info.Revision <= 0)
            throw new InvalidDataException("收到无效的正文信息。");

        var bytes = new byte[info.Length];
        for (var offset = 0; offset < bytes.Length;)
        {
            var request = post with { Revision = info.Revision, Offset = offset };
            var chunk = (await AskAsync(new("post-body-read", Body: request), token)).BodyBytes;
            var expected = Math.Min(PostBody.ReadChunkBytes, bytes.Length - offset);
            if (chunk is null || chunk.Length != expected) throw new InvalidDataException("正文读取不完整。");
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }
        if (Convert.ToHexString(SHA256.HashData(bytes)) != info.Sha256)
            throw new InvalidDataException("正文读取校验失败。");
        return new(info, PostBody.Decode(bytes));
    }

    internal async Task SavePostBodyAsync(PostBodyRequest post, string text, DateTimeOffset published, CancellationToken token)
    {
        var bytes = PostBody.Encode(text);
        var transfer = new PostBodyRequest(UploadId: Guid.NewGuid());
        var header = post with
        {
            UploadId = transfer.UploadId, Length = bytes.Length, PublishedAt = published,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
        try
        {
            await AskAsync(new("post-body-begin", Body: header), token);
            for (var offset = 0; offset < bytes.Length; offset += PostBody.UploadChunkBytes)
            {
                var length = Math.Min(PostBody.UploadChunkBytes, bytes.Length - offset);
                var chunk = transfer with { Offset = offset, Bytes = bytes.AsSpan(offset, length).ToArray() };
                await AskAsync(new("post-body-append", Body: chunk), token);
            }
            await AskAsync(new("post-body-commit", ChangeId: Guid.NewGuid(), Body: transfer), token);
        }
        catch
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await AskAsync(new("post-body-cancel", Body: transfer), cancel.Token); }
            catch (Exception error) when (error is IOException or ArgumentException or JsonException or
                BackgroundServiceException or OperationCanceledException) { /* 未提交正文只保留在 Worker 内存中，过期后回收。 */ }
            throw;
        }
    }

}

internal sealed record PostBodyDocument(PostBodyInfo Info, string Text);
