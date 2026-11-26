using System.IO;
using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;

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
}

internal sealed record PostBodyDocument(PostBodyInfo Info, string Text);
