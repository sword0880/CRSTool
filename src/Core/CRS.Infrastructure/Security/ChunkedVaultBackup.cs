using System.Security.Cryptography;
using System.Text.Json;
using CRS.Security.Cryptography;
using CRS.Security.Vault;

namespace CRS.Infrastructure;

internal sealed record BackupManifest(VaultEnvelope Envelope, long Length, byte[] Sha256, Guid[] Chunks);
internal sealed record BackupHeader(VaultEnvelope Envelope, SealedPayload Manifest);

/// <summary>分块容器复用 Security 的认证加密机制；清单认证全部块标识、顺序、总长度与数据库摘要。</summary>
internal static class ChunkedVaultBackup
{
    internal static ReadOnlySpan<byte> Magic => "CRS.Backup.v2\n"u8;
    internal const int ChunkBytes = 1024 * 1024;
    internal const long MaxDatabaseBytes = 2L * 1024 * 1024 * 1024;
    private const int HeaderBytes = 512 * 1024;
    private const int RecordBytes = 2 * 1024 * 1024;

    internal static void Write(Stream database, Stream destination, VaultEnvelope envelope, SecretKey key,CancellationToken cancellation=default)
    {
        var length = database.Length;
        if (length is <= 0 or > MaxDatabaseBytes) throw new CrsException("当前分块备份支持最大 2 GiB 加密数据库。");
        var chunks = Enumerable.Range(0, checked((int)((length + ChunkBytes - 1) / ChunkBytes)))
            .Select(_ => Guid.NewGuid()).ToArray();
        database.Position = 0;
        var manifest = new BackupManifest(envelope, length, Hash(database,cancellation), chunks);
        database.Position = 0;
        var plain = JsonSerializer.SerializeToUtf8Bytes(manifest);
        try
        {
            destination.Write(Magic);
            WriteRecord(destination, new BackupHeader(envelope, AuthenticatedEncryption.Seal(plain, key, Guid.NewGuid())), HeaderBytes);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var buffer = new byte[ChunkBytes];
        try
        {
            for (var i = 0; i < chunks.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkBytes, length - (long)i * ChunkBytes);
                database.ReadExactly(buffer.AsSpan(0, count));
                WriteRecord(destination, AuthenticatedEncryption.Seal(buffer.AsSpan(0, count), key, chunks[i]), RecordBytes);
            }
            if (database.ReadByte() != -1) throw new CrsException("备份快照发生变化，未提交备份。");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static BackupHeader ReadHeader(Stream input)
    {
        Span<byte> prefix = stackalloc byte[Magic.Length]; input.ReadExactly(prefix);
        if (!prefix.SequenceEqual(Magic)) throw new CrsException("分块备份格式不支持。");
        return ReadRecord<BackupHeader>(input, HeaderBytes);
    }

    internal static BackupManifest Authenticate(BackupHeader header, SecretKey key)
    {
        var plain = AuthenticatedEncryption.Open(header.Manifest, key);
        try
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(plain, VaultFiles.Options) ?? throw new JsonException();
            if (manifest.Length is <= 0 or > MaxDatabaseBytes || manifest.Length % 4096 != 0
                || manifest.Sha256 is not { Length: 32 } || manifest.Chunks is null
                || manifest.Chunks.Length != (manifest.Length + ChunkBytes - 1) / ChunkBytes
                || manifest.Chunks.Any(id => id == Guid.Empty || id == header.Manifest.Id)
                || manifest.Chunks.Distinct().Count() != manifest.Chunks.Length
                || JsonSerializer.Serialize(manifest.Envelope) != JsonSerializer.Serialize(header.Envelope))
                throw new CrsException("分块备份清单的长度、标识或元数据无效。");
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static void Extract(Stream input, Stream destination, BackupManifest manifest, SecretKey key,CancellationToken cancellation=default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < manifest.Chunks.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var payload = ReadRecord<SealedPayload>(input, RecordBytes);
            var count = (int)Math.Min(ChunkBytes, manifest.Length - (long)i * ChunkBytes);
            if (payload.Id != manifest.Chunks[i] || payload.Ciphertext is null || payload.Ciphertext.Length != count)
                throw new CrsException("备份块顺序、归属或长度不一致。");
            var plain = AuthenticatedEncryption.Open(payload, key);
            try { hash.AppendData(plain); destination.Write(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        if (input.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), manifest.Sha256))
            throw new CrsException("备份尾部或数据库摘要不一致，未恢复。");
    }
    // 整库摘要也分段检查取消，避免加密循环可取消但摘要阶段仍长时间阻塞。
    private static byte[] Hash(Stream input,CancellationToken cancellation)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[65536];
        try
        {
            int count;
            while((count=input.Read(buffer))!=0) {cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer,0,count);}
            cancellation.ThrowIfCancellationRequested(); return hash.GetHashAndReset();
        }
        finally {CryptographicOperations.ZeroMemory(buffer);}
    }

    // 每个 JSON 记录先读固定长度前缀，再按独立上限分配；不信任文件声明的任意长度。
    private static T ReadRecord<T>(Stream input, int maximum)
    {
        Span<byte> prefix = stackalloc byte[4]; input.ReadExactly(prefix);
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (count is <= 0 || count > maximum) throw new CrsException("备份记录大小超限。");
        var bytes = new byte[count]; input.ReadExactly(bytes);
        return JsonSerializer.Deserialize<T>(bytes, VaultFiles.Options) ?? throw new JsonException();
    }
    private static void WriteRecord<T>(Stream destination, T value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, VaultFiles.Options);
        if (bytes.Length > maximum) throw new CrsException("备份记录大小超限，未提交。");
        Span<byte> prefix = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        destination.Write(prefix); destination.Write(bytes);
    }
}
