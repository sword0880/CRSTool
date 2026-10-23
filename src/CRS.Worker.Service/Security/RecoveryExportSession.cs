using System.Security.Cryptography;
using CRS.Worker.Ipc;

namespace CRS.Worker.Service;

/// <summary>
/// Stage 2a only. One session per frozen snapshot, supplied by a future trusted maintenance coordinator.
/// No production IPC/CLI registration. BackgroundCaller must come from verified server-side token identity.
/// This state machine is not an OS security boundary or proof that a snapshot is quiescent/complete.
/// </summary>
internal sealed class RecoveryExportSession : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object gate = new();
    private readonly BackgroundCaller owner;
    private readonly Guid prepareRequestId;
    private readonly Guid bundleId;
    private readonly byte[] manifestSha256;
    private readonly byte[] challenge;
    private readonly Func<byte[]> readExistingKeyCopy;
    private readonly Action<RecoveryExportAudit> audit;
    private readonly TimeProvider clock;
    private readonly long preparedAt;
    private bool consumed;

    internal RecoveryExportSession(BackgroundCaller caller, Guid prepareRequestId, Guid bundleId,
        ReadOnlySpan<byte> manifestSha256, Func<byte[]> readExistingKeyCopy,
        Action<RecoveryExportAudit> audit, TimeProvider? clock = null)
    {
        if (!caller.CanManage || string.IsNullOrWhiteSpace(caller.Sid) || caller.Sid.Length > 184)
            throw Refused(RecoveryExportFailure.Forbidden);
        if (prepareRequestId == Guid.Empty || bundleId == Guid.Empty || manifestSha256.Length != 32)
            throw Refused(RecoveryExportFailure.InvalidRequest);
        ArgumentNullException.ThrowIfNull(readExistingKeyCopy);
        ArgumentNullException.ThrowIfNull(audit);
        owner = caller; this.prepareRequestId = prepareRequestId; this.bundleId = bundleId;
        this.manifestSha256 = manifestSha256.ToArray();
        this.readExistingKeyCopy = readExistingKeyCopy; this.audit = audit;
        this.clock = clock ?? TimeProvider.System;
        challenge = RandomNumberGenerator.GetBytes(32);
        preparedAt = this.clock.GetTimestamp();
        try { Record(prepareRequestId, RecoveryExportPhase.Prepared); }
        catch { Dispose(); throw; }
    }

    // An ephemeral confirmation challenge, not the independent recovery credential. Never persist/log it.
    internal byte[] CopyChallenge()
    {
        lock (gate)
        {
            if (consumed) throw Refused(RecoveryExportFailure.Consumed);
            return challenge.ToArray();
        }
    }

    internal byte[] Confirm(BackgroundCaller caller, Guid confirmationRequestId, Guid expectedBundleId,
        ReadOnlySpan<byte> expectedManifestSha256, ReadOnlySpan<byte> confirmationChallenge,
        ReadOnlySpan<byte> recoverySecret, bool explicitlyConfirmed, CancellationToken token = default)
    {
        lock (gate)
        {
            if (consumed) throw Refused(RecoveryExportFailure.Consumed);
            consumed = true; // Burn before any audit/key callback: concurrent/replayed calls cannot retry.
            byte[]? key = null;
            byte[]? envelope = null;
            try
            {
                if (!caller.CanManage || !string.Equals(caller.Sid, owner.Sid, StringComparison.Ordinal))
                    throw Refused(RecoveryExportFailure.Forbidden);
                if (confirmationRequestId == Guid.Empty || confirmationRequestId == prepareRequestId || recoverySecret.Length != 32)
                    throw Refused(RecoveryExportFailure.InvalidRequest);
                if (!explicitlyConfirmed) throw Refused(RecoveryExportFailure.ConfirmationRequired);
                if (expectedBundleId != bundleId || expectedManifestSha256.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(expectedManifestSha256, manifestSha256) ||
                    confirmationChallenge.Length != 32 || !CryptographicOperations.FixedTimeEquals(confirmationChallenge, challenge))
                    throw Refused(RecoveryExportFailure.BindingMismatch);
                CheckDeadline(token);
                Record(confirmationRequestId, RecoveryExportPhase.Authorized);
                CheckDeadline(token); // A slow audit cannot extend the authorization's lifetime.
                // Ownership transfers to this session: never return the live Worker's shared key array.
                try { key = readExistingKeyCopy(); }
                catch (Exception) { throw Refused(RecoveryExportFailure.SourceKeyUnavailable); }
                if (key is null || key.Length != 32) throw Refused(RecoveryExportFailure.SourceKeyUnavailable);
                CheckDeadline(token);
                envelope = ServiceKeyRecoveryEnvelope.Seal(key, recoverySecret, bundleId, manifestSha256);
                Record(confirmationRequestId, RecoveryExportPhase.Sealed);
                CheckDeadline(token);
                var result = envelope; envelope = null;
                return result; // Only ciphertext, after both durable audit callbacks succeeded.
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
                CryptographicOperations.ZeroMemory(challenge);
                CryptographicOperations.ZeroMemory(manifestSha256);
            }
        }
    }

    private void CheckDeadline(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw Refused(RecoveryExportFailure.Cancelled);
        var elapsed = clock.GetElapsedTime(preparedAt);
        if (elapsed < TimeSpan.Zero || elapsed >= Lifetime) throw Refused(RecoveryExportFailure.Expired);
    }

    private void Record(Guid requestId, RecoveryExportPhase phase)
    {
        try { audit(new(owner.Sid, prepareRequestId, requestId, bundleId, phase)); }
        catch (Exception) { throw Refused(RecoveryExportFailure.AuditUnavailable); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            consumed = true;
            CryptographicOperations.ZeroMemory(challenge);
            CryptographicOperations.ZeroMemory(manifestSha256);
        }
    }

    private static RecoveryExportRefusedException Refused(RecoveryExportFailure code) => new(code);
}

// Typed metadata only: no keys, challenge, credential, manifest contents or arbitrary exception text.
internal sealed record RecoveryExportAudit(string CallerSid, Guid PrepareRequestId, Guid RequestId,
    Guid BundleId, RecoveryExportPhase Phase);
internal enum RecoveryExportPhase { Prepared, Authorized, Sealed }
internal enum RecoveryExportFailure
{
    Forbidden, InvalidRequest, ConfirmationRequired, BindingMismatch, Expired, Consumed,
    Cancelled, AuditUnavailable, SourceKeyUnavailable
}
internal sealed class RecoveryExportRefusedException(RecoveryExportFailure code) : InvalidOperationException("Recovery export refused.")
{
    internal RecoveryExportFailure Code { get; } = code;
}
