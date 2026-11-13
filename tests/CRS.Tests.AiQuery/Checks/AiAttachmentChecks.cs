using System.Net;
using System.Text;
using System.Text.Json;
using CRS.AI.Models.Attachments;

namespace CRS.Tests.AiQuery.Checks;

/// <summary>附件白名单、解析边界、内存会话、调用者隔离与清理回归。</summary>
internal static class AiAttachmentChecks
{
    public static async Task RunAsync()
    {
        using var text = AiAttachmentParser.Parse("notes.txt", "text/plain", Encoding.UTF8.GetBytes("第一行\n第二行"));
        Check(text.FileName == "notes.txt" && text.CharacterCount == 7 && text.ReadText().Contains("第二行", StringComparison.Ordinal),
            "UTF-8 文本解析和预览");
        var combinedPrompt = AiAttachmentPrompt.Compose("请分析这个文件", [text]);
        using var attachmentJson = JsonDocument.Parse(AiAttachmentPrompt.FormatAttachments([text]));
        Check(combinedPrompt.StartsWith("请分析这个文件", StringComparison.Ordinal) &&
              attachmentJson.RootElement[0].GetProperty("content").GetString() == text.ReadText(),
            "问题与附件正文合并并以 JSON 安全编码");
        using var json = AiAttachmentParser.Parse("data.json", "application/json", Encoding.UTF8.GetBytes("{\"value\":1}"));
        Check(json.MediaType == "application/json", "JSON 格式验证");

        ExpectInvalid(() => AiAttachmentParser.Parse("payload.exe", null, [1, 2, 3]), "拒绝不支持扩展名");
        ExpectInvalid(() => AiAttachmentParser.Parse("note.txt", "application/json", Encoding.UTF8.GetBytes("{}")), "拒绝 MIME 与扩展名不匹配");
        ExpectInvalid(() => AiAttachmentParser.Parse("bad.txt", null, [0xc3, 0x28]), "拒绝无效 UTF-8");
        ExpectInvalid(() => AiAttachmentParser.Parse("bad.txt", null, [65, 0, 66]), "拒绝 NUL 控制字符");
        ExpectInvalid(() => AiAttachmentParser.Parse("bad.json", null, Encoding.UTF8.GetBytes("{bad")), "拒绝损坏 JSON");
        ExpectInvalid(() => AiAttachmentParser.Parse("../escape.txt", null, Encoding.UTF8.GetBytes("x")), "拒绝路径穿越文件名");
        ExpectInvalid(() => AiAttachmentParser.Parse("large.txt", null, new byte[AiAttachmentParser.MaxFileBytes + 1]), "拒绝超大单文件");

        var six = Enumerable.Range(0, AiAttachmentParser.MaxFiles + 1)
            .Select(index => AiAttachmentParser.Parse($"file{index}.txt", null, Encoding.UTF8.GetBytes($"内容{index}"))).ToArray();
        ExpectInvalid(() => AiAttachmentParser.ValidateCollection(six), "拒绝超过 5 个附件");
        foreach (var item in six) item.Dispose();

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(8)));
        using var sessions = new AiAttachmentConversationStore(clock, startCleanupTimer: false);
        var owner = new string('A', 64);
        var otherOwner = new string('B', 64);
        var sessionId = sessions.Create(owner);
        var stored = AiAttachmentParser.Parse("session.txt", "text/plain", Encoding.UTF8.GetBytes("会话附件内容"));
        sessions.Add(sessionId, owner, [stored]);
        Check(sessions.Get(sessionId, owner).Count == 1, "会话内存保存附件解析文本");
        ExpectUnauthorized(() => sessions.Get(sessionId, otherOwner), "会话句柄绑定调用者身份");
        Check(sessions.Remove(sessionId, owner, stored.Id) && stored.CharacterCount == 0,
            "逐个移除附件并尽力清零字符缓冲");

        var expiring = AiAttachmentParser.Parse("expiry.txt", null, Encoding.UTF8.GetBytes("短时内容"));
        sessions.Add(sessionId, owner, [expiring]);
        clock.Advance(AiAttachmentConversationStore.IdleLifetime + TimeSpan.FromSeconds(1));
        Check(sessions.CleanupExpired() == 1 && expiring.CharacterCount == 0, "空闲 TTL 清理会话附件");
        ExpectMissing(() => sessions.Get(sessionId, owner), "过期会话句柄失效");

        var endId = sessions.Create(owner);
        var ending = AiAttachmentParser.Parse("end.txt", null, Encoding.UTF8.GetBytes("结束内容"));
        sessions.Add(endId, owner, [ending]);
        sessions.End(endId, owner);
        Check(ending.CharacterCount == 0, "显式结束会话清理附件");
        ExpectMissing(() => sessions.Get(endId, owner), "结束会话后不能恢复附件");

        using var handlerClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.Found));
        using var downloader = new OpenAiAttachmentFileDownloader(handlerClient);
        await ExpectInvalidAsync(() => downloader.DownloadAsync(
            new("http://127.0.0.1/private", "file_test", "text/plain", "x.txt"), AiAttachmentParser.MaxFileBytes, CancellationToken.None),
            "拒绝非 HTTPS 和本机下载地址");
        await ExpectInvalidAsync(() => downloader.DownloadAsync(
            new("https://127.0.0.1/private", "file_test", "text/plain", "x.txt"), AiAttachmentParser.MaxFileBytes, CancellationToken.None),
            "拒绝 HTTPS IP 字面量地址");
        await ExpectInvalidAsync(() => downloader.DownloadAsync(
            new("https://evil.example/private", "file_test", "text/plain", "x.txt"), AiAttachmentParser.MaxFileBytes, CancellationToken.None),
            "拒绝非 OpenAI 下载域名");
        await ExpectInvalidAsync(() => downloader.DownloadAsync(
            new("https://files.oaiusercontent.com/test", "file_test", "text/plain", "x.txt"), AiAttachmentParser.MaxFileBytes, CancellationToken.None),
            "拒绝 ChatGPT 文件下载重定向");

        Console.WriteLine("PASS AI 附件：文件校验、会话隔离、TTL、逐个移除与结束清理");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private static void ExpectInvalid(Action action, string label)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(label);
    }

    private static async Task ExpectInvalidAsync(Func<Task<byte[]>> action, string label)
    {
        try { _ = await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(label);
    }

    private static void ExpectUnauthorized(Action action, string label)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(label);
    }

    private static void ExpectMissing(Action action, string label)
    {
        try { action(); }
        catch (KeyNotFoundException) { return; }
        throw new InvalidOperationException(label);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }

    private sealed class StaticResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
