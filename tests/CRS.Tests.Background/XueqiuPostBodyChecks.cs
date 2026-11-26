using System.Security.Cryptography;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

internal static class XueqiuPostBodyChecks
{
    private const string Evidence = "紫金矿业 $紫金矿业(SH601899)$ 的产量预计增长。";
    private static readonly string LongBody = string.Concat(Enumerable.Repeat("样本段落，保留换行 🐂。\r\n", 1024)) + Evidence;
    private const string Latest = "早期已保存的完整版。\n第二段。";

    internal static void SeedHistory(string directory, byte[] key, string account, string url)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "opinions.db"), Password = Convert.ToHexString(key), Pooling = false
        }.ToString());
        db.Open();
        var published = WatchProtocol.BeijingNow.AddHours(-1).ToUnixTimeSeconds();
        foreach (var (revision, text) in new[] { (1, LongBody), (2, Latest) })
        {
            var content = PostBody.Encode(text);
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO post_bodies(account,url,revision,published,sha256,body,origin)
                VALUES($a,$url,$r,$time,$hash,$body,$origin)
                """;
            command.Parameters.AddWithValue("$a", account);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$r", revision);
            command.Parameters.AddWithValue("$time", published);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(content)));
            command.Parameters.AddWithValue("$body", content);
            command.Parameters.AddWithValue("$origin", (int)PostBodySource.Manual);
            command.ExecuteNonQuery();
        }
    }

    internal static async Task RunAsync(Func<WatchRequest, Task<WatchReply>> ask, string account, string url,
        bool hasHistory = false)
    {
        var source = new PostBodyRequest(account, url);
        Task<WatchReply> Send(string operation, PostBodyRequest request) => ask(new(operation, Body: request));
        Require((await Send("post-body-info", source with { Account = "999999" })).Error is not null,
            "拒绝跨作者访问正文");
        foreach (var uploadOperation in new[] { "begin", "append", "commit", "cancel" })
            Require((await Send($"post-body-{uploadOperation}", source)).Error is not null, "旧上传接口已关闭");
        if (!hasHistory)
        {
            Require((await Send("post-body-info", source)).Body is null, "关闭上传后无新增手工正文");
            return;
        }

        async Task<string> Read(long revision)
        {
            var metadata = (await Send("post-body-info", source with { Revision = revision })).Body!;
            Require(metadata.Source == PostBodySource.Manual, "历史来源仍正确显示");
            using var body = new MemoryStream();
            for (var offset = 0; offset < metadata.Length; offset += PostBody.ReadChunkBytes)
            {
                var chunk = (await Send("post-body-read", source with { Revision = metadata.Revision, Offset = offset })).BodyBytes!;
                await body.WriteAsync(chunk);
            }
            Require(body.Length == metadata.Length && Convert.ToHexString(SHA256.HashData(body.ToArray())) == metadata.Sha256,
                "按已确认版本读取全部分块并通过校验");
            return PostBody.Decode(body.ToArray());
        }

        Require(await Read(1) == LongBody && await Read(0) == Latest, "旧全文及其历史版本仍可读取");
        Require(WatchInput.Candidates(await Read(1), []).Any(stock => stock.Code == "601899"), "原存长文仍可提取候选");
        var stock = new Security("沪市", "601899", "紫金矿业", "有色金属");
        var opinion = new Opinion(Guid.NewGuid(), account, stock, WatchProtocol.BeijingNow.AddHours(-1),
            Stance.Pending, Attribution.Author, "待核对观点", Evidence, url, PostBodyRevision: 1);
        Require((await ask(new("opinion-save", Opinion: opinion, ChangeId: Guid.NewGuid()))).Error is null,
            "允许观点关联既有正文");
        Require((await ask(new("opinion-save", Opinion: opinion with { PostBodyRevision = 999 }, ChangeId: Guid.NewGuid()))).Error is not null,
            "拒绝关联不存在的正文版本");
        var history = (await ask(new("detail", new(Security: stock, Mode: "timeline")))).Detail!;
        Require(history.Rows.Single(x => x.Url == url).PostBodyRevision == 1, "观点证据引用原正文版本");
        Console.WriteLine("PASS 雪球原帖全文：旧手工入口已关闭，已存正文和观点关联可继续使用。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"雪球全文检查失败：{message}");
    }
}
