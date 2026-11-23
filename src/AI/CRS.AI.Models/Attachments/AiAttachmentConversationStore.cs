using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CRS.AI.Models.Attachments;

/// <summary>仅在内存中保留 MCP 对话附件；会话由高熵句柄和调用者凭证摘要共同保护。</summary>
public sealed class AiAttachmentConversationStore : IDisposable
{
    public const int MaxActiveSessions = 64;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(2);

    private sealed class Entry(string owner, DateTimeOffset createdAt)
    {
        public readonly object Gate = new();
        public readonly string Owner = owner;
        public readonly DateTimeOffset CreatedAt = createdAt;
        public DateTimeOffset LastAccessAt = createdAt;
        public readonly List<AiAttachmentDocument> Attachments = [];
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object creationGate = new();
    private readonly TimeProvider clock;
    private readonly Timer? cleanupTimer;
    private volatile bool disposed;

    public AiAttachmentConversationStore(TimeProvider? timeProvider = null, bool startCleanupTimer = true)
    {
        clock = timeProvider ?? TimeProvider.System;
        if (startCleanupTimer)
            cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string Create(string owner)
    {
        ThrowIfDisposed();
        ValidateOwner(owner);
        lock (creationGate)
        {
            ThrowIfDisposed();
            CleanupExpired();
            if (entries.Count >= MaxActiveSessions) throw new InvalidOperationException("当前 MCP 附件会话已达到上限。");

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (entries.TryAdd(id, new(owner, clock.GetUtcNow()))) return id;
            }
            throw new InvalidOperationException("无法创建附件会话，请稍后重试。");
        }
    }

    public IReadOnlyList<AiAttachmentDocument> Get(string sessionId, string owner)
    {
        var entry = GetOwned(sessionId, owner);
        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            Touch(entry);
            return entry.Attachments.ToArray();
        }
    }

    public DateTimeOffset GetExpiresAt(string sessionId, string owner)
    {
        var entry = GetOwned(sessionId, owner);
        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            Touch(entry);
            return ExpiresAt(entry);
        }
    }

    /// <summary>整批加入，先验证合并后的总量，再一次性改变会话状态。</summary>
    public IReadOnlyList<AiAttachmentDocument> Add(string sessionId, string owner, IReadOnlyList<AiAttachmentDocument> additions)
    {
        var entry = GetOwned(sessionId, owner);
        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            var combined = entry.Attachments.Concat(additions).ToArray();
            AiAttachmentParser.ValidateCollection(combined);
            if (combined.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count() != combined.Length)
                throw new InvalidDataException("当前会话已包含相同内容的附件。");
            entry.Attachments.AddRange(additions);
            Touch(entry);
            return entry.Attachments.ToArray();
        }
    }

    public bool Remove(string sessionId, string owner, Guid attachmentId)
    {
        var entry = GetOwned(sessionId, owner);
        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            var index = entry.Attachments.FindIndex(item => item.Id == attachmentId);
            if (index < 0) return false;
            var removed = entry.Attachments[index];
            entry.Attachments.RemoveAt(index);
            removed.Dispose();
            Touch(entry);
            return true;
        }
    }

    public void End(string sessionId, string owner)
    {
        var entry = GetOwned(sessionId, owner);
        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            if (!entries.TryRemove(sessionId, out _)) throw new InvalidOperationException("附件会话已经结束。");
            Clear(entry);
        }
    }

    /// <summary>供定时清理器和隔离测试调用；只清除空闲或超过绝对寿命的会话。</summary>
    public int CleanupExpired()
    {
        if (disposed) return 0;
        var now = clock.GetUtcNow();
        var removedCount = 0;
        foreach (var pair in entries)
        {
            lock (pair.Value.Gate)
            {
                if (!IsExpired(pair.Value, now)) continue;
                if (!entries.TryRemove(pair.Key, out _)) continue;
                Clear(pair.Value);
                removedCount++;
            }
        }
        return removedCount;
    }

    public void Dispose()
    {
        lock (creationGate)
        {
            if (disposed) return;
            disposed = true;
            cleanupTimer?.Dispose();
            foreach (var pair in entries)
            {
                lock (pair.Value.Gate)
                {
                    if (entries.TryRemove(pair.Key, out _)) Clear(pair.Value);
                }
            }
        }
    }

    private Entry GetOwned(string sessionId, string owner)
    {
        ThrowIfDisposed();
        ValidateOwner(owner);
        if (sessionId is not { Length: 43 } ||
            sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            !entries.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("附件会话不存在或已经过期。");

        lock (entry.Gate)
        {
            EnsureCurrent(sessionId, entry);
            if (!FixedEquals(entry.Owner, owner)) throw new UnauthorizedAccessException("附件会话不属于当前调用者。");
            if (IsExpired(entry, clock.GetUtcNow()))
            {
                if (entries.TryRemove(sessionId, out _)) Clear(entry);
                throw new KeyNotFoundException("附件会话不存在或已经过期。");
            }
            return entry;
        }
    }

    private void EnsureCurrent(string sessionId, Entry entry)
    {
        if (!entries.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, entry))
            throw new KeyNotFoundException("附件会话不存在或已经过期。");
        if (IsExpired(entry, clock.GetUtcNow()))
        {
            if (entries.TryRemove(sessionId, out _)) Clear(entry);
            throw new KeyNotFoundException("附件会话不存在或已经过期。");
        }
    }

    private void Touch(Entry entry) => entry.LastAccessAt = clock.GetUtcNow();

    private static DateTimeOffset ExpiresAt(Entry entry) =>
        Min(entry.LastAccessAt + IdleLifetime, entry.CreatedAt + AbsoluteLifetime);

    private static bool IsExpired(Entry entry, DateTimeOffset now) =>
        now >= entry.LastAccessAt + IdleLifetime || now >= entry.CreatedAt + AbsoluteLifetime;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static void Clear(Entry entry)
    {
        foreach (var attachment in entry.Attachments) attachment.Dispose();
        entry.Attachments.Clear();
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static void ValidateOwner(string owner)
    {
        if (owner is not { Length: 64 } || owner.Any(character => !Uri.IsHexDigit(character)))
            throw new UnauthorizedAccessException("MCP 调用者身份无效。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
