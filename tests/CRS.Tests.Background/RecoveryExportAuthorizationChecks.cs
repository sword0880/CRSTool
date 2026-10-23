using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

internal static class RecoveryExportAuthorizationChecks
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        var admin = new BackgroundCaller("S-1-5-21-1-2-3-1000", true);
        var secret = RandomNumberGenerator.GetBytes(32);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using (var valid = new Fixture(admin, key))
            {
                Check(valid.Reads == 0 && valid.Audits.Select(x => x.Phase).SequenceEqual([RecoveryExportPhase.Prepared]));
                var sealedKey = valid.Confirm(secret);
                Check(valid.Reads == 1 && valid.LastRead!.All(x => x == 0));
                using var opened = ServiceKeyRecoveryEnvelope.Open(sealedKey, secret, valid.BundleId, valid.Digest);
                var copy = opened.Copy();
                try { Check(CryptographicOperations.FixedTimeEquals(copy, key)); }
                finally { CryptographicOperations.ZeroMemory(copy); }
                Check(valid.Audits.Select(x => x.Phase).SequenceEqual([RecoveryExportPhase.Prepared, RecoveryExportPhase.Authorized, RecoveryExportPhase.Sealed]));
                Check(valid.Audits.All(x => x.CallerSid == admin.Sid && x.PrepareRequestId == valid.PrepareId && x.BundleId == valid.BundleId));
                Refuses(() => valid.Confirm(secret), RecoveryExportFailure.Consumed);
                Refuses(() => valid.Session.CopyChallenge(), RecoveryExportFailure.Consumed);
                Check(valid.Reads == 1);
            }

            foreach (var caller in new[] { admin with { CanManage = false }, admin with { Sid = "" }, admin with { Sid = new string('x', 185) } })
                Refuses(() => new Fixture(caller, key).Dispose(), RecoveryExportFailure.Forbidden);
            foreach (var invalid in new[] { (Guid.Empty, Guid.NewGuid(), new byte[32]), (Guid.NewGuid(), Guid.Empty, new byte[32]), (Guid.NewGuid(), Guid.NewGuid(), new byte[31]) })
                Refuses(() => new RecoveryExportSession(admin, invalid.Item1, invalid.Item2, invalid.Item3,
                    () => throw new IOException("must not read"), _ => throw new IOException("must not audit")), RecoveryExportFailure.InvalidRequest);

            RejectBeforeRead(f => f.Confirm(secret, caller: admin with { CanManage = false }), RecoveryExportFailure.Forbidden);
            RejectBeforeRead(f => f.Confirm(secret, caller: admin with { Sid = "S-1-5-21-1-2-3-1001" }), RecoveryExportFailure.Forbidden);
            RejectBeforeRead(f => f.Confirm(secret, requestId: Guid.Empty), RecoveryExportFailure.InvalidRequest);
            RejectBeforeRead(f => f.Confirm(secret, requestId: f.PrepareId), RecoveryExportFailure.InvalidRequest);
            RejectBeforeRead(f => f.Confirm(new byte[31]), RecoveryExportFailure.InvalidRequest);
            RejectBeforeRead(f => f.Confirm(secret, confirmed: false), RecoveryExportFailure.ConfirmationRequired);
            RejectBeforeRead(f => f.Confirm(secret, bundleId: Guid.NewGuid()), RecoveryExportFailure.BindingMismatch);
            RejectBeforeRead(f => f.Confirm(secret, digest: new byte[32]), RecoveryExportFailure.BindingMismatch);
            RejectBeforeRead(f => f.Confirm(secret, digest: new byte[31]), RecoveryExportFailure.BindingMismatch);
            RejectBeforeRead(f => f.Confirm(secret, challenge: new byte[32]), RecoveryExportFailure.BindingMismatch);
            RejectBeforeRead(f => f.Confirm(secret, challenge: new byte[31]), RecoveryExportFailure.BindingMismatch);
            RejectBeforeRead(f => { f.Clock.Timestamp += RecoveryExportSession.Lifetime.Ticks; return f.Confirm(secret); }, RecoveryExportFailure.Expired);
            RejectBeforeRead(f => { f.Clock.Timestamp--; return f.Confirm(secret); }, RecoveryExportFailure.Expired);
            RejectBeforeRead(f => f.Confirm(secret, token: new CancellationToken(true)), RecoveryExportFailure.Cancelled);
            RejectBeforeRead(f => { f.Session.Dispose(); return f.Confirm(secret); }, RecoveryExportFailure.Consumed);
            RejectBeforeRead(f => { f.FailAudit = RecoveryExportPhase.Authorized; return f.Confirm(secret); }, RecoveryExportFailure.AuditUnavailable);
            RejectBeforeRead(f => { f.OnAudit = _ => f.Clock.Timestamp += RecoveryExportSession.Lifetime.Ticks; return f.Confirm(secret); }, RecoveryExportFailure.Expired);

            Refuses(() => new RecoveryExportSession(admin, Guid.NewGuid(), Guid.NewGuid(), new byte[32],
                () => throw new InvalidOperationException("must not read"), _ => throw new IOException("synthetic confidential audit detail")),
                RecoveryExportFailure.AuditUnavailable);

            foreach (var phase in new[] { "source", "length", "sealed-audit", "cancel-after-read", "expire-after-read", "cancel-after-seal" })
            {
                using var failed = new Fixture(admin, key);
                var code = RecoveryExportFailure.SourceKeyUnavailable;
                using var cancelled = new CancellationTokenSource();
                if (phase == "source") failed.ThrowSource = true;
                if (phase == "length") failed.SourceLength = 31;
                if (phase == "sealed-audit") { failed.FailAudit = RecoveryExportPhase.Sealed; code = RecoveryExportFailure.AuditUnavailable; }
                if (phase == "cancel-after-read") { failed.OnRead = cancelled.Cancel; code = RecoveryExportFailure.Cancelled; }
                if (phase == "expire-after-read") { failed.OnRead = () => failed.Clock.Timestamp += RecoveryExportSession.Lifetime.Ticks; code = RecoveryExportFailure.Expired; }
                if (phase == "cancel-after-seal") { failed.OnAudit = p => { if (p == RecoveryExportPhase.Sealed) cancelled.Cancel(); }; code = RecoveryExportFailure.Cancelled; }
                Refuses(() => failed.Confirm(secret, token: cancelled.Token), code);
                Check(failed.Reads == 1);
                Check(failed.LastRead is null || failed.LastRead.All(x => x == 0));
                Refuses(() => failed.Confirm(secret), RecoveryExportFailure.Consumed);
                Check(failed.Reads == 1);
            }

            using (var first = new Fixture(admin, key))
            using (var second = new Fixture(admin, key))
            {
                Check(!first.Challenge.SequenceEqual(second.Challenge));
                Refuses(() => second.Confirm(secret, challenge: first.Challenge), RecoveryExportFailure.BindingMismatch);
                Check(second.Reads == 0);
                first.Clock.Timestamp += RecoveryExportSession.Lifetime.Ticks - 1;
                _ = first.Confirm(secret); // Just before the deadline succeeds without using wall time.
                Check(first.Reads == 1);
            }

            using (var concurrent = new Fixture(admin, key))
            {
                var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                {
                    try { concurrent.Confirm(secret); return true; }
                    catch (RecoveryExportRefusedException error) when (error.Code == RecoveryExportFailure.Consumed) { return false; }
                })));
                Check(results.Count(x => x) == 1 && concurrent.Reads == 1);
                Check(concurrent.LastRead!.All(x => x == 0));
            }
            using (var binding = new Fixture(admin, key))
            {
                var original = binding.Digest.ToArray(); binding.Digest[0] ^= 1;
                var sealedKey = binding.Confirm(secret, digest: original);
                using var opened = ServiceKeyRecoveryEnvelope.Open(sealedKey, secret, binding.BundleId, original);
                Check(binding.Reads == 1); // The session owns a clone, not the caller's mutable digest.
            }
            CheckExistingReadOnlyPath();
            Console.WriteLine($"Recovery export authorization: {checks} synthetic checks passed; production export is not registered.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(key); }

        void RejectBeforeRead(Func<Fixture, byte[]> operation, RecoveryExportFailure code)
        {
            using var fixture = new Fixture(admin, key);
            Refuses(() => operation(fixture), code);
            Check(fixture.Reads == 0);
            Refuses(() => fixture.Confirm(secret), RecoveryExportFailure.Consumed);
            Check(fixture.Reads == 0);
        }
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Synthetic export authorization assertion failed.");
            checks++;
        }
        void Refuses(Action action, RecoveryExportFailure code)
        {
            try { action(); }
            catch (RecoveryExportRefusedException error)
            {
                Check(error.Code == code && error.Message == "Recovery export refused." && error.InnerException is null); return;
            }
            throw new InvalidOperationException("Synthetic export refusal was expected.");
        }
        void CheckExistingReadOnlyPath()
        {
            var root = Path.Combine(Path.GetTempPath(), "CRS.SyntheticExportAuthorization", Guid.NewGuid().ToString("N"));
            StorageSecurity.Prepare(root, console: true);
            try
            {
                var path = Path.Combine(root, "service-key.dpapi");
                try { var unexpected = ServiceKey.OpenExisting(root); CryptographicOperations.ZeroMemory(unexpected); throw new InvalidOperationException("Missing key accepted."); }
                catch (FileNotFoundException) { checks++; }
                Check(!File.Exists(path)); // Even an empty directory cannot initialize from the export read path.
                var created = ServiceKey.Open(root);
                try
                {
                    var before = File.ReadAllBytes(path);
                    var existing = ServiceKey.OpenExisting(root);
                    try { Check(CryptographicOperations.FixedTimeEquals(created, existing)); }
                    finally { CryptographicOperations.ZeroMemory(existing); }
                    Check(before.SequenceEqual(File.ReadAllBytes(path)));
                    var envelope = JsonNode.Parse(before)!.AsObject(); envelope["IdentitySid"] = "S-1-5-21-1-2-3-999";
                    File.WriteAllText(path, envelope.ToJsonString());
                    var changed = File.ReadAllBytes(path);
                    try { var unexpected = ServiceKey.OpenExisting(root); CryptographicOperations.ZeroMemory(unexpected); throw new IOException("Foreign SID accepted."); }
                    catch (InvalidOperationException) { checks++; }
                    Check(changed.SequenceEqual(File.ReadAllBytes(path)));
                }
                finally { CryptographicOperations.ZeroMemory(created); }
            }
            finally { Directory.Delete(root, recursive: true); } // Test-owned GUID directory only.
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Guid PrepareId = Guid.NewGuid(), BundleId = Guid.NewGuid();
        internal readonly byte[] Digest = SHA256.HashData("synthetic frozen manifest"u8);
        internal readonly List<RecoveryExportAudit> Audits = [];
        internal readonly ManualClock Clock = new();
        internal readonly RecoveryExportSession Session;
        internal readonly byte[] Challenge;
        internal readonly BackgroundCaller Caller;
        internal int Reads, SourceLength = 32;
        internal byte[]? LastRead;
        internal bool ThrowSource;
        internal RecoveryExportPhase? FailAudit;
        internal Action? OnRead;
        internal Action<RecoveryExportPhase>? OnAudit;
        internal Fixture(BackgroundCaller caller, byte[] key)
        {
            Caller = caller;
            Session = new(caller, PrepareId, BundleId, Digest, () =>
            {
                Reads++;
                if (ThrowSource) throw new IOException("synthetic confidential source detail");
                LastRead = key[..SourceLength]; OnRead?.Invoke(); return LastRead;
            }, item =>
            {
                if (FailAudit == item.Phase) throw new IOException("synthetic confidential audit detail");
                Audits.Add(item); OnAudit?.Invoke(item.Phase);
            }, Clock);
            Challenge = Session.CopyChallenge();
        }
        internal byte[] Confirm(byte[] secret, BackgroundCaller? caller = null, Guid? requestId = null,
            Guid? bundleId = null, byte[]? digest = null, byte[]? challenge = null, bool confirmed = true,
            CancellationToken token = default) => Session.Confirm(caller ?? Caller, requestId ?? Guid.NewGuid(),
                bundleId ?? BundleId, digest ?? Digest, challenge ?? Challenge, secret, confirmed, token);
        public void Dispose() { Session.Dispose(); CryptographicOperations.ZeroMemory(Challenge); }
    }
    private sealed class ManualClock : TimeProvider
    {
        internal long Timestamp = 100;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
