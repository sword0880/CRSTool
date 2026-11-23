using System.Globalization;
using CRS.Application.Abstractions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CRS.Infrastructure;

/// <summary>问答使用保险库现有 SQLCipher 连接。首次保存时事务内建立独立聊天表。</summary>
internal sealed class AiChatHistoryStore(VaultSession session) : IAiChatHistory
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS ai_chat_sessions(
            id TEXT PRIMARY KEY NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
            title TEXT NOT NULL, updated TEXT NOT NULL, revision INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS ai_chat_entries(
            session_id TEXT NOT NULL REFERENCES ai_chat_sessions(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL, question TEXT NOT NULL, answer TEXT NOT NULL, screenshot BLOB,
            PRIMARY KEY(session_id, ordinal));
        """;

    public Task<IReadOnlyList<AiChatSummary>> SearchAsync(string query, CancellationToken token) =>
        Run<IReadOnlyList<AiChatSummary>>((c, tx) =>
        {
            if (!Exists(c, tx)) return [];
            return c.Query("""
                SELECT s.id,s.title,s.updated,s.model FROM ai_chat_sessions s
                WHERE @Query='' OR EXISTS (SELECT 1 FROM ai_chat_entries e WHERE e.session_id=s.id
                    AND (instr(lower(e.question),lower(@Query))>0 OR instr(lower(e.answer),lower(@Query))>0))
                ORDER BY s.updated DESC,s.id DESC LIMIT 100
                """, new { Query = query.Trim() }, tx).Select(row => new AiChatSummary(
                    Guid.Parse((string)row.id), (string)row.title, ParseDate((string)row.updated), (string)row.model)).ToArray();
        }, token);

    public Task<AiChatConversation?> ReadAsync(Guid id, CancellationToken token) =>
        Run<AiChatConversation?>((c, tx) =>
        {
            if (!Exists(c, tx)) return null;
            var args = new { Id = id.ToString("N") };
            var row = c.QuerySingleOrDefault("SELECT provider,model,revision FROM ai_chat_sessions WHERE id=@Id", args, tx);
            if (row is null) return null;
            var entries = c.Query("SELECT question,answer,screenshot FROM ai_chat_entries WHERE session_id=@Id ORDER BY ordinal",
                args, tx).Select(entry => new AiChatEntry((string)entry.question, (string)entry.answer, (byte[]?)entry.screenshot)).ToArray();
            return new(id, (string)row.provider, (string)row.model, (long)row.revision, entries);
        }, token);

    public Task<long> AppendAsync(Guid id, string provider, string model, long revision, AiChatEntry entry, CancellationToken token) =>
        Run((c, tx) =>
        {
            if (id == Guid.Empty || revision is < 0 or >= 100 || entry.Question.Length is < 1 or > 4000 ||
                entry.Answer.Length is < 1 or > 65536 || entry.Screenshot?.Length > 1024 * 1024)
                throw new CrsException("本次聊天已达到保存上限，请新建会话。");
            c.Execute(Schema, transaction: tx);
            var text = string.Join(" ", entry.Question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var args = new
            {
                Id = id.ToString("N"), Provider = provider, Model = model, Revision = revision,
                Title = text[..Math.Min(text.Length, 60)], Updated = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Ordinal = revision + 1, entry.Question, entry.Answer, entry.Screenshot
            };
            // 使用原版本做条件更新，防止并行继续会话时覆盖记录或复活已删除会话。
            var changed = revision == 0 ? c.Execute("""
                INSERT INTO ai_chat_sessions(id,provider,model,title,updated,revision)
                VALUES(@Id,@Provider,@Model,@Title,@Updated,@Ordinal) ON CONFLICT(id) DO NOTHING
                """, args, tx) : c.Execute("""
                UPDATE ai_chat_sessions SET updated=@Updated,revision=@Ordinal
                WHERE id=@Id AND revision=@Revision AND provider=@Provider AND model=@Model
                """, args, tx);
            if (changed != 1) throw new CrsException("聊天记录已在其他窗口修改，请重新打开。");
            // 限制单会话的截图总量，使历史恢复不会一次分配大量图片。
            if (entry.Screenshot is { } image && c.ExecuteScalar<long>("""
                SELECT COALESCE(sum(length(screenshot)),0) FROM ai_chat_entries WHERE session_id=@Id
                """, args, tx) + image.Length > 12 * 1024 * 1024)
                throw new CrsException("此会话保留的截图已达上限，请新建会话。");
            c.Execute("""
                INSERT INTO ai_chat_entries(session_id,ordinal,question,answer,screenshot)
                VALUES(@Id,@Ordinal,@Question,@Answer,@Screenshot)
                """, args, tx);
            if (c.ExecuteScalar<long>("PRAGMA page_count", transaction: tx) *
                c.ExecuteScalar<long>("PRAGMA page_size", transaction: tx) > ChunkedVaultBackup.MaxDatabaseBytes)
                throw new CrsException("保险库已达到备份容量上限，未保存本次聊天。");
            return revision + 1;
        }, token, write: true);

    public Task DeleteAsync(Guid id, CancellationToken token) => Run((c, tx) =>
    {
        if (Exists(c, tx))
            c.Execute("DELETE FROM ai_chat_sessions WHERE id=@Id", new { Id = id.ToString("N") }, tx);
        return true;
    }, token, write: true);

    private Task<T> Run<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken token, bool write = false) =>
        Task.Run(() =>
        {
            using var lease = session.Enter();
            token.ThrowIfCancellationRequested();
            using var connection = session.Open();
            using var transaction = connection.BeginTransaction(deferred: !write);
            var result = action(connection, transaction);
            token.ThrowIfCancellationRequested();
            session.Lifetime.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }, token);

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ai_chat_sessions'",
            transaction: transaction) == 1;

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
