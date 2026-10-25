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
        => new(data, raw, encryptionKey: TestMarketData.Key, timeProvider: clock, approvalWriteAuthority: authority);

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
            original = Approval("sse-market-stats", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage |
                MarketCycleSourceUse.HistoricalBackfill | MarketCycleSourceUse.Redistribution,
                validUntil: clock.At.AddMinutes(4));
            originalCommand = Command(original, clock.At);
            firstReceipt = registry.Apply(originalCommand);

            Check(firstReceipt.Sequence == 1 && !firstReceipt.Replayed, "首次审批追加单调审计事件");
            Check(registry.VerifyAuditChain() && registry.ReadAudit().Count == 1, "加密库中的 HMAC 审计链可核验");
            Check(registry.Find(original.DatasetId) == original, "重启前可按 DatasetId 读取当前审批");
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry);
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.HistoricalBackfill, clock.At, registry);
            MarketCycleSourceAdmission.Require(original, MarketCycleSourceOperation.Redistribution, clock.At, registry);
            Check(true, "四类用途全量获批时仅按独立门禁逐项放行");

            var tamperedSource = original with { SourceId = "SZSE" };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedSource, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改内存对象的 SourceId 不能覆盖注册表");
            var tamperedUri = original with { SourceUri = new Uri("https://other.example/market/statistics") };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedUri, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改来源 URI 不能覆盖注册表");
            var tamperedDataset = original with { DatasetId = "other-dataset" };
            RejectData(() => MarketCycleSourceAdmission.Require(tamperedDataset, MarketCycleSourceOperation.AutomatedPolling, clock.At, registry),
                "修改 DatasetId 不能覆盖注册表");
            var tamperedUses = original with { ApprovedUses = MarketCycleSourceUse.None };
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

            var badTerms = original with { TermsEvidenceHash = Hash(Encoding.UTF8.GetBytes("different terms not archived")) };
            RejectData(() => registry.Apply(Command(badTerms, clock.At)), "条款哈希未在原件存证中找到时不能批准");

            var proofBoundMutation = Command(original with { ApprovedUses = MarketCycleSourceUse.None }, clock.At,
                proofOverride: originalCommand.AuthorizationProof);
            RejectUnauthorized(() => registry.Apply(proofBoundMutation), "审批内容变化但复用旧授权证明时拒绝");

            var sourceRebind = Approval(original.DatasetId, termsHash, clock.At.AddSeconds(1),
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage,
                sourceId: "SZSE", sourceUri: "https://www.szse.cn/market/statistics");
            clock.Advance(TimeSpan.FromSeconds(1));
            RejectData(() => registry.Apply(Command(sourceRebind, clock.At)), "现有 DatasetId 不能重新绑定到另一个 SourceId/URI");

            var duplicateCommand = registry.Apply(originalCommand);
            Check(duplicateCommand.Replayed && duplicateCommand.Sequence == firstReceipt.Sequence && registry.ReadAudit().Count == 3,
                "同一命令的精确重放返回原回执而不追加事件");
            var nonceReplay = Command(Approval("sse-nonce-replay", termsHash, clock.At,
                MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage), clock.At,
                nonce: originalCommand.Nonce);
            RejectData(() => registry.Apply(nonceReplay), "新命令不能重放已消费的 nonce");

            clock.Advance(TimeSpan.FromMinutes(3));
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

            Check(registry.ReadAudit().Count == 5 && registry.VerifyAuditChain(), "审计链包含新增、撤销及过期审批并保持连续");
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
            Password = Convert.ToHexString(TestMarketData.Key),
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

        Console.WriteLine("市场周期审批注册表检查完成。");
    }
}
