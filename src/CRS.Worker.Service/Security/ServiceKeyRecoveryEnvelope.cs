// 服务密钥恢复封装原语：把密钥绑定到包身份与清单摘要；恢复凭据是独立随机秘密，实际授权、停机快照和目标激活由上层协调。
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CRS.Worker.Service;

/// <summary>
/// Stage 1 primitive only: no file access, DPAPI, authorization, installation or scheduler activation.
/// A future authorized coordinator must bind a stopped, verified snapshot and retain the release gate.
/// Recovery credentials are independently generated 32-byte random secrets, never passwords.
/// </summary>
internal static class ServiceKeyRecoveryEnvelope
{
    // Fixed binary format: magic(8), version(4), bundle ID(16), manifest SHA256(32),
    // salt(32), nonce(12), encrypted ServiceKey(32), GCM tag(16). Entire header is AAD.
    internal const int Size = 152;
    private const int HeaderSize = 104;
    private static ReadOnlySpan<byte> Magic => "CRSWKR01"u8;
    private static ReadOnlySpan<byte> Domain => "CRS.Worker.Service/recovery-key/v1"u8;

    internal static byte[] Seal(ReadOnlySpan<byte> serviceKey, ReadOnlySpan<byte> recoverySecret,
        Guid bundleId, ReadOnlySpan<byte> manifestSha256)
    {
        ValidateContext(recoverySecret, bundleId, manifestSha256);
        if (serviceKey.Length != 32) throw Failure();
        var envelope = new byte[Size];
        Magic.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8), 1);
        bundleId.TryWriteBytes(envelope.AsSpan(12, 16));
        manifestSha256.CopyTo(envelope.AsSpan(28, 32));
        RandomNumberGenerator.Fill(envelope.AsSpan(60, 32));
        RandomNumberGenerator.Fill(envelope.AsSpan(92, 12));
        Span<byte> kek = stackalloc byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, recoverySecret, kek, envelope.AsSpan(60, 32), Domain);
            using var aes = new AesGcm(kek, 16);
            aes.Encrypt(envelope.AsSpan(92, 12), serviceKey, envelope.AsSpan(HeaderSize, 32),
                envelope.AsSpan(136, 16), envelope.AsSpan(0, HeaderSize));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    internal static RecoveredKey Open(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> recoverySecret,
        Guid expectedBundleId, ReadOnlySpan<byte> expectedManifestSha256)
    {
        ValidateContext(recoverySecret, expectedBundleId, expectedManifestSha256);
        // Validate the exact bound before slicing/deriving. No attacker-controlled allocations or KDF cost.
        if (envelope.Length != Size || !envelope[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(envelope.Slice(8, 4)) != 1 ||
            new Guid(envelope.Slice(12, 16)) != expectedBundleId ||
            !CryptographicOperations.FixedTimeEquals(envelope.Slice(28, 32), expectedManifestSha256))
            throw Failure();
        Span<byte> kek = stackalloc byte[32];
        Span<byte> plaintext = stackalloc byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, recoverySecret, kek, envelope.Slice(60, 32), Domain);
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(envelope.Slice(92, 12), envelope.Slice(HeaderSize, 32), envelope.Slice(136, 16),
                plaintext, envelope[..HeaderSize]);
            return new RecoveredKey(plaintext);
        }
        catch (CryptographicException) { throw Failure(); }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateContext(ReadOnlySpan<byte> secret, Guid bundleId, ReadOnlySpan<byte> digest)
    {
        if (secret.Length != 32 || bundleId == Guid.Empty || digest.Length != 32) throw Failure();
    }

    // Never propagate provider details, credentials, keys, paths or bundle data into diagnostics.
    private static CryptographicException Failure() => new("Worker recovery envelope authentication failed.");

    internal sealed class RecoveredKey : IDisposable
    {
        private byte[]? bytes;
        internal RecoveredKey(ReadOnlySpan<byte> value) { bytes = value.ToArray(); }
        internal byte[] Copy() => bytes?.ToArray() ?? throw new ObjectDisposedException(nameof(RecoveredKey));
        public void Dispose()
        {
            if (bytes is { } value) CryptographicOperations.ZeroMemory(value);
            bytes = null;
        }
    }
}
