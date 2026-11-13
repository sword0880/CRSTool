using System.Net;
using System.Net.Http;
using CRS.Common.Net;

internal static class BoundedContentChecks
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS 公共正文读取：" + label); }

    public static async Task RunAsync()
    {
        using (var exact = new ByteArrayContent([1, 2, 3]))
            Check((await BoundedContentReader.ReadAsync(exact, 3, default)).SequenceEqual(new byte[] { 1, 2, 3 }), "容量边界保留完整字节");
        using (var empty = new ByteArrayContent([]))
            Check((await BoundedContentReader.ReadAsync(empty, 1, default)).Length == 0, "合法空正文不制造内容");
        foreach (long? declared in new long?[] { null, 1, 4 })
        {
            using var response = new Content(new MemoryStream([1, 2, 3, 4]), declared);
            try { await BoundedContentReader.ReadAsync(response, 3, default); throw new Exception("超限未拒绝"); }
            catch (InvalidDataException) { Check(true, "缺失、低报及超限长度头均不能绕过容量限制：" + declared); }
        }
        var oversized = new Content(new MemoryStream([1]), 4);
        using (oversized)
        {
            try { await BoundedContentReader.ReadAsync(oversized, 3, default); throw new Exception("预检未拒绝"); }
            catch (InvalidDataException) { Check(!oversized.Opened, "超限长度头在打开正文前拒绝"); }
        }
        using var cancellation = new CancellationTokenSource();
        using var stalled = new Content(new StalledStream(cancellation), null);
        try { await BoundedContentReader.ReadAsync(stalled, 10, cancellation.Token); throw new Exception("取消未传播"); }
        catch (OperationCanceledException error)
        { Check(error.CancellationToken == cancellation.Token && stalled.StreamDisposed, "正文取消保留原令牌并释放流"); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var unopened = new Content(new MemoryStream([1]), null);
        try { await BoundedContentReader.ReadAsync(unopened, 1, canceled.Token); throw new Exception("预取消未传播"); }
        catch (OperationCanceledException) { Check(!unopened.Opened, "预取消不打开正文"); }
    }

    private sealed class Content(Stream stream, long? declared) : HttpContent
    {
        public bool Opened { get; private set; }
        public bool StreamDisposed => !stream.CanRead;
        protected override bool TryComputeLength(out long length) { length = declared ?? 0; return declared.HasValue; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync()
        { Opened = true; return Task.FromResult(stream); }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return CreateContentReadStreamAsync(); }
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class StalledStream(CancellationTokenSource source) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            source.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}
