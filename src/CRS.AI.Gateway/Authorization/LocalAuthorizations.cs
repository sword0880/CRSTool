namespace CRS.AI.Gateway.Authorization;

internal sealed class LocalAuthorizations : IDisposable
{
    private readonly AuthorizationFile file;
    private readonly Dictionary<string, CancellationTokenSource> subjects;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LocalAuthorizations(AuthorizationFile file)
    {
        this.file = file;
        subjects = file.Load().ToDictionary(sid => sid, _ => new CancellationTokenSource(), StringComparer.Ordinal);
    }

    public async Task<Lease?> AcquireAsync(string sid, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return subjects.TryGetValue(sid, out var grant)
                ? new Lease(grant, CancellationTokenSource.CreateLinkedTokenSource(token, grant.Token))
                : null;
        }
        finally { gate.Release(); }
    }

    public async Task<string> SetAsync(string? sid, bool allow, CancellationToken token)
    {
        if (sid is null || !AuthorizationFile.IsValidSid(sid)) return "InvalidRequest";
        CancellationTokenSource? revoked = null;
        await gate.WaitAsync(token);
        try
        {
            var exists = subjects.TryGetValue(sid, out var current);
            if (allow == exists) return "Ok";
            if (allow && subjects.Count >= AuthorizationFile.MaxSubjects) return "GrantLimit";
            try
            {
                file.Save(allow ? subjects.Keys.Append(sid) : subjects.Keys.Where(key => key != sid));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "AuthorizationUnavailable";
            }

            if (allow) subjects.Add(sid, new CancellationTokenSource());
            else
            {
                subjects.Remove(sid);
                revoked = current;
            }
        }
        finally { gate.Release(); }

        // 变更已持久化。旧授权的撤销信号独立于管理员客户端是否仍保持连接。
        if (revoked is not null)
        {
            using (revoked)
            {
                try { await revoked.CancelAsync(); }
                catch (AggregateException) { /* 授权已撤销，单个取消回调失败不能恢复授权。 */ }
            }
        }
        return "Ok";
    }

    public async Task ReplyAsync(string sid, Lease lease, Func<bool, Task> write, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            // 释放结果和撤销确认互斥。重新授权会建立新代次，旧请求永远不能借新授权返回。
            await write(subjects.TryGetValue(sid, out var current) && ReferenceEquals(current, lease.Grant));
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var grant in subjects.Values) grant.Dispose();
        gate.Dispose();
    }

    internal sealed class Lease(CancellationTokenSource grant, CancellationTokenSource linked) : IDisposable
    {
        public CancellationTokenSource Grant { get; } = grant;
        public CancellationToken Token => linked.Token;
        public void Dispose() => linked.Dispose();
    }
}
