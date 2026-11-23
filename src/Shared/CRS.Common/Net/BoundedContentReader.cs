using System.Net.Http;

namespace CRS.Common.Net;

/// <summary>按调用方容量上限读取响应正文；来源许可、重试和超时均由调用方负责。</summary>
public static class BoundedContentReader
{
    public static async Task<byte[]> ReadAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Response too large.");

        // 长度头可能缺失或小于真实正文，必须逐块检查，且正文沿用调用方的同一个期限。
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[Math.Min(8192, maximumBytes)];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Response too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }
}
