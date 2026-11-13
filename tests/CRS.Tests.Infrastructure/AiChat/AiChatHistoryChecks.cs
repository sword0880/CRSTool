using System.Text;
using CRS.Application.Abstractions;
using CRS.Infrastructure;

/// <summary>用真实加密保险库验证聊天搜索、追加、隔离、删除及锁定。</summary>
internal static class AiChatHistoryChecks
{
    public static async Task RunAsync(string root)
    {
        using var first = new VaultManager();
        using var second = new VaultManager();
        const string password = "Synthetic-AiChat-Password-2026";
        var firstDirectory = Path.Combine(root, "one");
        var secondDirectory = Path.Combine(root, "two");
        await first.CreateAsync(firstDirectory, password);
        await second.CreateAsync(secondDirectory, password);
        await TestStores.Enroll(first);
        await TestStores.Enroll(second);
        var history = ((LocalStore)first.Repository).CreateAiChatHistory();
        var separate = ((LocalStore)second.Repository).CreateAiChatHistory();
        var id = Guid.NewGuid();
        const string question = "Synthetic secret ETF question";
        Check((await history.SearchAsync("", default)).Count == 0, "初始保险库没有聊天表");
        var firstRevision = await history.AppendAsync(id, "deepseek", "deepseek-flash", 0,
            new(question, "首条答案"), default);
        var photo = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
        await history.AppendAsync(id, "deepseek", "deepseek-flash", firstRevision,
            new("后续问题", "第二条答案", photo), default);
        var chat = await history.ReadAsync(id, default);
        Check(chat is { Revision: 2 } && chat.Entries.Count == 2 && chat.Entries[0].Screenshot is null &&
              chat.Entries[1].Screenshot!.SequenceEqual(photo), "追加保留顺序及明确保存的截图");
        Check((await history.SearchAsync("第二条", default)).Single().Id == id &&
              (await history.SearchAsync("synthetic secret", default)).Single().Id == id, "支持按问题和回答搜索");
        Check(await separate.ReadAsync(id, default) is null, "不同保险库隔离历史");
        Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(firstDirectory, "crs.db"))).Contains(question),
            "数据库文件中没有可见问答明文");
        await history.DeleteAsync(id, default);
        Check(await history.ReadAsync(id, default) is null, "删除会话后问答及附件不可读取");
        try
        {
            await history.AppendAsync(id, "deepseek", "deepseek-flash", 2, new("过期请求", "回答"), default);
            throw new InvalidOperationException("已删除会话被旧版本复活。");
        }
        catch (CRS.Domain.CrsException) { }
        first.Lock();
        try
        {
            await history.SearchAsync("", default);
            throw new InvalidOperationException("锁定后旧历史连接仍可读取。");
        }
        catch (CRS.Domain.CrsException) { }
        Console.WriteLine("PASS 历史锁定与旧请求隔离");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
