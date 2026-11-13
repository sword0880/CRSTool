using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class MarketCycleApprovalRegistryChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset At { get; set; } = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => At;
        public void Advance(TimeSpan amount) => At = At.Add(amount);
    }

    // Test-only authority: hashes the entire command to prove mutation/replay checks are wired.
    // This is intentionally NOT an authentication implementation and must never be used by production.
    private sealed class FixtureAuthority : IMarketCycleApprovalWriteAuthority
    {
        private const string Actor = "fixture-admin";

        public bool TryAuthorize(MarketCycleSourceApprovalWriteCommand command, DateTimeOffset now, out string authenticatedActorId)
        {
            authenticatedActorId = Actor;
            return command.AuthorizationProof == Proof(command.CommandId, command.Nonce, command.IssuedAt, command.Approval) &&
                command.Approval.Reviewer == Actor &&
                command.IssuedAt <= now.AddSeconds(30) &&
                now - command.IssuedAt <= TimeSpan.FromMinutes(5);
        }

        public static string Proof(Guid commandId, string nonce, DateTimeOffset issuedAt, MarketCycleSourceApproval approval)
        {
            var canonical = JsonSerializer.Serialize(new { commandId, nonce, issuedAt, approval });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS 市场周期审批注册表：" + name);
    }

    private static void RejectData(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Check(true, name); return; }
        throw new Exception("未拒绝：" + name);
    }

    private static void RejectOperation(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, name); return; }
        throw new Exception("未拒绝：" + name);
    }

    private static void RejectUnauthorized(Action action, string name)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { Check(true, name); return; }
        throw new Exception("未拒绝：" + name);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static MarketCycleSourceApproval Approval(
        string dataset,
        string termsHash,
        DateTimeOffset now,
        MarketCycleSourceUse uses,
        MarketCycleSourceApprovalStatus status = MarketCycleSourceApprovalStatus.Approved,
        DateTimeOffset? validUntil = null,
        string sourceId = "SSE",
        string sourceUri = "https://www.sse.com.cn/market/statistics")
        => new(sourceId, new Uri(sourceUri), dataset, "contract-v1", "parser-v1", status, uses,
            termsHash, "fixture-admin", now, "policy-v1", validUntil);

    private static MarketCycleSourceApprovalWriteCommand Command(
        MarketCycleSourceApproval approval,
        DateTimeOffset now,
        Guid? commandId = null,
        string? nonce = null,
        string? proofOverride = null)
    {
        var id = commandId ?? Guid.NewGuid();
        var actualNonce = nonce ?? Guid.NewGuid().ToString("N");
        var proof = FixtureAuthority.Proof(id, actualNonce, now, approval);
        return new(id, actualNonce, now, approval, proofOverride ?? proof);
    }

    private static MarketRepository Open(
        string data,
        RawEvidenceStore raw,
        Clock clock,
        IMarketCycleApprovalWriteAuthority? authority)
        => new(data, raw, approvalAuditKey: TestMarketData.Key, timeProvider: clock, approvalWriteAuthority: authority);

    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var raw = new RawEvidenceStore(Path.Combine(root, "raw"));
        var termsBytes = Encoding.UTF8.GetBytes("synthetic license terms evidence; test-only; not a real source approval");
        var termsHash = raw.Save(termsBytes);
        var clock = new Clock();
        var data = Path.Combine(root, "data");
        var authority = new FixtureAuthority();
        MarketCycleSourceApproval original;
        MarketCycleSourceApprovalWriteCommand originalCommand;
        MarketCycleSourceApprovalWriteReceipt firstReceipt;

        using (var repository = Open(data, raw, clock, authority))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)repository;
            Check(registry.Find("never-approved") is null, "不存在的来源审批默认返回空，不会自动批准");
            var orphanRevocation = Approval("orphan-revocation", termsHash, clock.At,
                MarketCycleSourceUse.None, MarketCycleSourceApprovalStatus.Revoked);
            RejectOperation(() => registry.Apply(Command(orphanRevocation, clock.At)),
                "不存在先前批准记录的来源不能直接登记为已撤销");

            var invalidExpiry = Approval("invalid-expiry", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                validUntil: clock.At.AddSeconds(10)) with { ReviewedAt = clock.At.AddSeconds(20) };
            RejectData(() => registry.Apply(Command(invalidExpiry, clock.At)),
                "审批有效期不能早于审核生效时间");

            original = Approval("sse-market-stats", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage |
                MarketCycleSourceUse.HistoricalBackfill | MarketCycleSourceUse.Redistribution,
                validUntil: clock.At.AddHours(1));
            originalCommand = Command(original, clock.At);
            firstReceipt = registry.Apply(originalCommand);

            Check(firstReceipt.Sequence == 1 && !firstReceipt.Replayed, "首次审批追加单调审计事件");
            Check(registry.VerifyAuditChain() && registry.ReadAudit().Count == 1, "加密库中的 HMAC 审计链可核验");
            Check(registry.Find(original.DatasetId) == original, "重启前可按 DatasetId 读取当前审批");
            var snapshotApproval = repository.Read(
                reader => ((IMarketCycleSourceApprovalRegistry)reader).Find(original.DatasetId),
                System.Threading.CancellationToken.None);
            Check(snapshotApproval == original, "独立只读快照可从加密连接派生相同审计密钥并读取审批");
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry);
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.HistoricalBackfill, clock.At, registry);
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.Redistribution, clock.At, registry);
            Check(true, "四类用途全量获批时仅按独立门禁逐项放行");
            MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn/market/statistics/daily?tradeDate=2026-10-25"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry);
            Check(true, "获批基址下的目标路径与查询参数可通过目标绑定门禁");

            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, "SZSE", original.DatasetId,
                new Uri("https://www.sse.com.cn/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "实际请求 SourceId 与审批身份不一致时拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, "other-dataset",
                new Uri("https://www.sse.com.cn/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "实际请求 DatasetId 与审批身份不一致时拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://attacker.example/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "同路径不同主机不能冒充已批准来源");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn/market/statistics-evil"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "路径前缀相似但不在获批路径边界内时拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn.attacker.example/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "合法主机名后缀拼接攻击必须拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn:8443/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "非默认 HTTPS 端口必须拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("http://www.sse.com.cn/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "HTTP 降级请求必须拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn@attacker.example/market/statistics/daily"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "URI 用户信息伪装官方主机必须拒绝");

            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn/market/statistics/%252e%252e/admin"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "双重编码的路径回退段必须拒绝");
            RejectData(() => MarketCycleSourceAdmission.RequireTarget(
                original, original.SourceId, original.DatasetId,
                new Uri("https://www.sse.com.cn/market/statistics/%252f..%252fadmin"),
                MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "双重编码的路径分隔符必须拒绝");

            var unsafeBase = Approval("unsafe-base-path", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                sourceUri: "https://www.sse.com.cn/market/%252e%252e/admin");
            RejectData(() => registry.Apply(Command(unsafeBase, clock.At)),
                "含编码路径回退段的来源基址不能进入审批注册表");

            var tamperedSource = original with { SourceId = "SZSE" };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedSource, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改内存对象的 SourceId 不能覆盖注册表");
            var tamperedUri = original with { SourceUri = new Uri("https://other.example/market/statistics") };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedUri, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改来源 URI 不能覆盖注册表");
            var tamperedDataset = original with { DatasetId = "other-dataset" };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedDataset, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改 DatasetId 不能覆盖注册表");
            var tamperedUses = original with { ApprovedUses = MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedUses, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "内存中的用途权限变化与注册表不一致时拒绝");

            var restricted = Approval("sse-restricted-stats", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage);
            registry.Apply(Command(restricted, clock.At));
            MarketCycleSourceAdmission.Require(restricted, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry);
            RejectOperation(() => MarketCycleSourceAdmission.Require(restricted, MarketCycleSourceOperation.HistoricalBackfill, clock.At, registry),
                "自动采集许可不能替代历史回填许可");
            RejectOperation(() => MarketCycleSourceAdmission.Require(restricted, MarketCycleSourceOperation.Redistribution, clock.At, registry),
                "本地持久化许可不能替代再分发许可");

            var noStorage = Approval("sse-no-storage", termsHash, clock.At, MarketCycleSourceUse.AutomatedCollection);
            registry.Apply(Command(noStorage, clock.At));
            RejectOperation(() => MarketCycleSourceAdmission.Require(noStorage, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "仅批准自动采集但未批准持久化仍被拒绝");

            clock.Advance(TimeSpan.FromSeconds(1));
            var laterApproval = Approval("sse-later-event", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage);
            registry.Apply(Command(laterApproval, clock.At));

            var badTerms = original with { TermsEvidenceHash = Hash(Encoding.UTF8.GetBytes("different terms not archived")) };
            RejectData(() => registry.Apply(Command(badTerms, clock.At)), "条款哈希未在原件存证中找到时不能批准");

            var proofBoundMutation = Command(original with { ApprovedUses = MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage }, clock.At,
                proofOverride: originalCommand.AuthorizationProof);
            RejectUnauthorized(() => registry.Apply(proofBoundMutation), "审批内容变化但复用旧授权证明时拒绝");

            var sourceRebind = Approval(original.DatasetId, termsHash, clock.At.AddSeconds(1),
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                sourceId: "SZSE", sourceUri: "https://www.szse.cn/market/statistics");
            clock.Advance(TimeSpan.FromSeconds(1));
            RejectData(() => registry.Apply(Command(sourceRebind, clock.At)), "现有 DatasetId 不能重新绑定到另一个 SourceId/URI");

            var uriRebind = Approval("sse-new-dataset-uri-rebind", termsHash, clock.At.AddSeconds(1),
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                sourceId: "SSE", sourceUri: "https://different.sse.example/market/statistics");
            clock.Advance(TimeSpan.FromSeconds(1));
            RejectData(() => registry.Apply(Command(uriRebind, clock.At)), "同一 SourceId 不能改绑到新的来源基址");

            var duplicateCommand = registry.Apply(originalCommand);
            Check(duplicateCommand.Replayed && duplicateCommand.Sequence == firstReceipt.Sequence && registry.ReadAudit().Count == 4,
                "同一命令的精确重放返回原回执而不追加事件");
            var nonceReplay = Command(Approval("sse-nonce-replay", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage), clock.At,
                nonce: originalCommand.Nonce);
            RejectData(() => registry.Apply(nonceReplay), "新命令不能重放已消费的 nonce");

            clock.Advance(TimeSpan.FromMinutes(6));
            RejectData(() => registry.Apply(originalCommand), "超过授权新鲜期的旧审批命令不能再次提交");
            Check(registry.VerifyAuditChain(), "拒绝重放后原审计链仍完整");

            clock.Advance(TimeSpan.FromSeconds(1));
            var revoked = Approval(original.DatasetId, termsHash, clock.At, MarketCycleSourceUse.None,
                MarketCycleSourceApprovalStatus.Revoked);
            registry.Apply(Command(revoked, clock.At));
            var current = registry.Find(original.DatasetId)!;
            Check(current.Status == MarketCycleSourceApprovalStatus.Revoked, "撤销状态作为新事件保留，不覆盖历史批准记录");
            RejectOperation(() => MarketCycleSourceAdmission.Require(current, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "已撤销来源不能继续通过准入门禁");

            clock.Advance(TimeSpan.FromSeconds(1));
            var expiring = Approval("sse-expiring", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                validUntil: clock.At.AddSeconds(30));
            registry.Apply(Command(expiring, clock.At));
            clock.Advance(TimeSpan.FromSeconds(31));
            RejectOperation(() => MarketCycleSourceAdmission.Require(registry.Find(expiring.DatasetId)!, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "审批到期后即使记录仍在也拒绝执行");

            Check(registry.ReadAudit().Count == 6 && registry.VerifyAuditChain(), "审计链包含新增、撤销及过期审批并保持连续");
        }

        using (var readOnly = Open(data, raw, clock, authority: null))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)readOnly;
            Check(registry.Find(original.DatasetId)?.Status == MarketCycleSourceApprovalStatus.Revoked,
                "重启后仍读取到最新撤销状态");
            RejectUnauthorized(() => registry.Apply(Command(original, clock.At)),
                "未注入可信授权器时审批写入默认关闭");
        }

        var tamperData = Path.Combine(root, "tamper");
        var tamperRaw = new RawEvidenceStore(Path.Combine(root, "tamper-raw"));
        var tamperHash = tamperRaw.Save(Encoding.UTF8.GetBytes("tamper fixture terms"));
        var tamperClock = new Clock();
        using (var repository = Open(tamperData, tamperRaw, tamperClock, new FixtureAuthority()))
        {
            var approval = Approval("tamper-dataset", tamperHash, tamperClock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage);
            ((IMarketCycleSourceApprovalRegistry)repository).Apply(Command(approval, tamperClock.At));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(tamperData, "market.db"),
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE market_cycle_source_approval_event SET event_hash=$hash WHERE sequence=1";
            command.Parameters.AddWithValue("$hash", new string('A', 64));
            command.ExecuteNonQuery();
        }
        using (var reopened = Open(tamperData, tamperRaw, tamperClock, authority: null))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)reopened;
            Check(!registry.VerifyAuditChain(), "数据库中的审计事件被篡改后 HMAC 链核验失败");
            RejectData(() => registry.Find("tamper-dataset"), "审计链损坏时拒绝返回来源审批");
        }

        var truncateData = Path.Combine(root, "truncate");
        var truncateClock = new Clock();
        using (var repository = Open(truncateData, raw, truncateClock, authority))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)repository;
            var approved = Approval("truncate-dataset", termsHash, truncateClock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                validUntil: truncateClock.At.AddHours(1));
            registry.Apply(Command(approved, truncateClock.At));
            truncateClock.Advance(TimeSpan.FromSeconds(1));
            var revoked = Approval("truncate-dataset", termsHash, truncateClock.At,
                MarketCycleSourceUse.None, MarketCycleSourceApprovalStatus.Revoked);
            registry.Apply(Command(revoked, truncateClock.At));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(truncateData, "market.db"),
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM market_cycle_source_approval_event WHERE sequence=2";
            command.ExecuteNonQuery();
        }
        using (var reopened = Open(truncateData, raw, truncateClock, authority: null))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)reopened;
            Check(!registry.VerifyAuditChain(), "尾部撤销事件被删除时链头锚点核验失败");
            RejectData(() => registry.Find("truncate-dataset"), "尾部截断不能回退为旧的 Approved 审批");
        }

        var anchorTamperData = Path.Combine(root, "anchor-tamper");
        var anchorTamperClock = new Clock();
        using (var repository = Open(anchorTamperData, raw, anchorTamperClock, new FixtureAuthority()))
        {
            var approval = Approval("anchor-tamper-dataset", termsHash, anchorTamperClock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage);
            ((IMarketCycleSourceApprovalRegistry)repository).Apply(Command(approval, anchorTamperClock.At));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(anchorTamperData, "market.db"),
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE market_cycle_source_approval_anchor SET anchor_mac='not-hex' WHERE id=1";
            command.ExecuteNonQuery();
        }
        using (var reopened = Open(anchorTamperData, raw, anchorTamperClock, authority: null))
        {
            var registry = (IMarketCycleSourceApprovalRegistry)reopened;
            Check(!registry.VerifyAuditChain(), "损坏的审计链头锚点 MAC 应返回核验失败而非向外抛出存储转换异常");
            RejectData(() => registry.Find("anchor-tamper-dataset"), "链头锚点 MAC 损坏时拒绝返回审批");
        }

        Console.WriteLine("市场周期审批注册表检查完成。");
    }
}
