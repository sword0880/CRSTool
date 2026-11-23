using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;

internal static class XueqiuPostBodyChecks
{
    internal static async Task RunAsync(Func<WatchRequest, Task<WatchReply>> ask, string account, string url,
        Func<Task>? restart = null)
    {
        var source = new PostBodyRequest(account, url);
        var referenceTime = WatchProtocol.BeijingNow.Date.AddHours(-2);
        var published = new DateTimeOffset(referenceTime, TimeSpan.FromHours(8));
        const string evidence = "紫金矿业 $紫金矿业(SH601899)$ 的产量预计增长。";
        var content = string.Concat(Enumerable.Repeat("样本段落，保留换行 🐂。\r\n", 1024)) + "\r\n" + evidence;
        var bytes = PostBody.Encode(content);
        Require(bytes.Length > PostBody.ReadChunkBytes && content.Length > WatchProtocol.MaximumPayload,
            "测试正文超过现有管道单条请求和读取分块上限");

        Task<WatchReply> Send(string operation, PostBodyRequest request, Guid changeId = default) =>
            ask(new(operation, ChangeId: changeId, Body: request));

        async Task<PostBodyRequest> Begin(byte[] body, long expectedRevision)
        {
            var transfer = source with
            {
                Revision = expectedRevision, UploadId = Guid.NewGuid(), PublishedAt = published,
                Length = body.Length, Sha256 = Convert.ToHexString(SHA256.HashData(body))
            };
            Require((await Send("post-body-begin", transfer)).Error is null, "开始正文上传");
            return new(UploadId: transfer.UploadId);
        }

        Task<WatchReply> Append(PostBodyRequest transfer, byte[] data, int offset) =>
            Send("post-body-append", transfer with
            {
                Offset = offset,
                Bytes = data.AsSpan(offset, Math.Min(PostBody.UploadChunkBytes, data.Length - offset)).ToArray()
            });

        async Task<PostBodyRequest> Upload(byte[] body, long expectedRevision)
        {
            var transfer = await Begin(body, expectedRevision);
            for (var offset = 0; offset < body.Length; offset += PostBody.UploadChunkBytes)
                Require((await Append(transfer, body, offset)).Error is null, "上传正文分块");
            return transfer;
        }

        Task<WatchReply> Commit(PostBodyRequest transfer, Guid? changeId = null) =>
            Send("post-body-commit", transfer, changeId ?? Guid.NewGuid());

        async Task<string> Read(long revision)
        {
            var metadata = (await Send("post-body-info", source with { Revision = revision })).Body!;
            using var body = new MemoryStream();
            for (var offset = 0; offset < metadata.Length; offset += PostBody.ReadChunkBytes)
            {
                var chunk = (await Send("post-body-read", source with { Revision = metadata.Revision, Offset = offset })).BodyBytes!;
                await body.WriteAsync(chunk);
            }
            Require(body.Length == metadata.Length, "按已确认版本读取全部分块");
            return PostBody.Decode(body.ToArray());
        }

        Require((await Send("post-body-info", source)).Body is null, "旧线索没有被升级为全文");
        Require((await Send("post-body-info", source with { Account = "999999" })).Error is not null,
            "拒绝跨作者访问正文");
        var oversized = source with
        {
            UploadId = Guid.NewGuid(), Length = PostBody.MaximumBytes + 1, PublishedAt = published,
            Sha256 = new string('A', 64)
        };
        Require((await Send("post-body-begin", oversized)).Error is not null, "超长正文明确拒绝");

        var partial = await Begin(bytes, 0);
        Require((await Append(partial, bytes, 0)).Error is null && (await Append(partial, bytes, 0)).Error is null,
            "同一分块收到重试可防止重复追加");
        Require((await Commit(partial)).Error is not null && (await Send("post-body-info", source)).Body is null,
            "部分上传不能提交或显示为全文");
        await Send("post-body-cancel", partial);
        Require((await Append(partial, bytes, 0)).Error is not null, "取消后释放上传状态");

        var first = await Upload(bytes, 0);
        var firstChange = Guid.NewGuid();
        Require((await Commit(first, firstChange)).Error is null && (await Commit(first, firstChange)).Error is null,
            "完整正文提交与原请求重放");
        if (restart is not null) await restart();
        Require(await Read(1) == content, "长文、中文、换行及补充平面字符完整保留");
        Require(WatchInput.Candidates(await Read(1), []).Any(stock => stock.Code == "601899"),
            "可从长文后部提取个股候选");

        var securities = new[] { new Security("沪市", "601899", "紫金矿业", "有色金属"), new("港股", "00941", "中国移动", "通信") };
        foreach (var stock in securities)
        {
            var opinion = new Opinion(Guid.NewGuid(), account, stock, published,
                Stance.Pending, Attribution.Author, "用户待核对的观点", evidence, url, PostBodyRevision: 1);
            Require((await ask(new("opinion-save", Opinion: opinion, ChangeId: Guid.NewGuid()))).Error is null,
                "不同股票关联同一原帖正文版本");
            Require((await ask(new("opinion-save", Opinion: opinion with { PostBodyRevision = 999 }, ChangeId: Guid.NewGuid()))).Error is not null,
                "拒绝关联不存在的正文版本");
        }

        const string revision = "用户核实了完整版后的新文本。\n第二段。";
        var update = await Upload(PostBody.Encode(revision), 1);
        var conflict = await Upload(PostBody.Encode("旧编辑窗口的修改"), 1);
        Require((await Commit(update)).Error is null && (await Commit(conflict)).Error is not null,
            "过期编辑不会覆盖新正文");
        await Send("post-body-cancel", conflict);
        var identical = await Upload(PostBody.Encode(revision), 2);
        Require((await Commit(identical)).Error is null &&
            (await Send("post-body-info", source)).Body is { Revision: 2 } && await Read(0) == revision && await Read(1) == content,
            "相同内容去重，旧版本可继续查阅");
        var history = (await ask(new("detail", new(Security: securities[0], Mode: "timeline")))).Detail!;
        Require(history.Rows.Single(x => x.Url == url).PostBodyRevision == 1, "个股历史证据仍引用原正文版本");
        Console.WriteLine("PASS 雪球原帖全文：加密库、长文分块、核对后提交、版本与多股票关联。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"雪球全文检查失败：{message}");
    }
}
