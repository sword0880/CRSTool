using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CRS.Worker.Service;

internal static class ServiceKeyRecoveryChecks
{
    internal static void Run()
    {
        // Synthetic memory only: never call ServiceKey.Open, read installed files or touch SCM.
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = RandomNumberGenerator.GetBytes(32);
        var wrongSecret = RandomNumberGenerator.GetBytes(32);
        var digest = SHA256.HashData("synthetic stopped snapshot manifest"u8);
        var id = Guid.NewGuid();
        var count = 0;
        try
        {
            var envelope = ServiceKeyRecoveryEnvelope.Seal(key, secret, id, digest);
            using (var recovered = ServiceKeyRecoveryEnvelope.Open(envelope, secret, id, digest))
            {
                var copy = recovered.Copy();
                try { Check(CryptographicOperations.FixedTimeEquals(key, copy)); }
                finally { CryptographicOperations.ZeroMemory(copy); }
            }
            Check(envelope.Length == ServiceKeyRecoveryEnvelope.Size);
            Check(!envelope.SequenceEqual(ServiceKeyRecoveryEnvelope.Seal(key, secret, id, digest)));
            Reject(() => ServiceKeyRecoveryEnvelope.Open(envelope, wrongSecret, id, digest).Dispose());
            Reject(() => ServiceKeyRecoveryEnvelope.Open(envelope, secret, Guid.NewGuid(), digest).Dispose());
            Reject(() => ServiceKeyRecoveryEnvelope.Open(envelope, secret, id, new byte[32]).Dispose());
            // Every byte, including all metadata, salt, nonce, ciphertext and tag, must be authenticated.
            for (var i = 0; i < envelope.Length; i++)
            {
                var changed = envelope.ToArray(); changed[i] ^= 1;
                Reject(() => ServiceKeyRecoveryEnvelope.Open(changed, secret, id, digest).Dispose());
            }
            // Matching a forged caller-supplied context must still fail the GCM authentication.
            var forged = envelope.ToArray(); forged[12] ^= 1;
            var forgedId = new Guid(forged.AsSpan(12, 16));
            Reject(() => ServiceKeyRecoveryEnvelope.Open(forged, secret, forgedId, digest).Dispose());
            forged = envelope.ToArray(); forged[28] ^= 1;
            var forgedDigest = forged.AsSpan(28, 32).ToArray();
            Reject(() => ServiceKeyRecoveryEnvelope.Open(forged, secret, id, forgedDigest).Dispose());
            for (var length = 0; length < envelope.Length; length++)
            {
                var truncated = envelope[..length];
                Reject(() => ServiceKeyRecoveryEnvelope.Open(truncated, secret, id, digest).Dispose());
            }
            Reject(() => ServiceKeyRecoveryEnvelope.Open([.. envelope, 0], secret, id, digest).Dispose());
            Reject(() => ServiceKeyRecoveryEnvelope.Open(new byte[1024 * 1024], secret, id, digest).Dispose());
            foreach (var length in new[] { 0, 16, 31, 33, 64 })
            {
                var invalid = new byte[length];
                Reject(() => ServiceKeyRecoveryEnvelope.Seal(key, invalid, id, digest));
                Reject(() => ServiceKeyRecoveryEnvelope.Open(envelope, invalid, id, digest).Dispose());
                Reject(() => ServiceKeyRecoveryEnvelope.Seal(invalid, secret, id, digest));
                Reject(() => ServiceKeyRecoveryEnvelope.Seal(key, secret, id, invalid));
            }
            Reject(() => ServiceKeyRecoveryEnvelope.Seal(key, secret, Guid.Empty, digest));
            Reject(() => ServiceKeyRecoveryEnvelope.Open(envelope, secret, Guid.Empty, digest).Dispose());
            using var final = ServiceKeyRecoveryEnvelope.Open(envelope, secret, id, digest);
            final.Dispose();
            try { final.Copy(); throw new InvalidOperationException("Disposed key remained readable."); }
            catch (ObjectDisposedException) { count++; }
            CheckServiceKeyFailureBoundary();
            Console.WriteLine($"ServiceKey recovery envelope: {count} synthetic checks passed; production recovery remains disabled.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(wrongSecret);
        }
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Synthetic recovery assertion failed.");
            count++;
        }
        void CheckServiceKeyFailureBoundary()
        {
            var root = Path.Combine(Path.GetTempPath(), "CRS.SyntheticKeyRecovery", Guid.NewGuid().ToString("N"));
            StorageSecurity.Prepare(root, console: true);
            try
            {
                var keyPath = Path.Combine(root, "service-key.dpapi");
                var syntheticKey = ServiceKey.Open(root);
                CryptographicOperations.ZeroMemory(syntheticKey);
                var original = File.ReadAllBytes(keyPath);
                var parsed = JsonNode.Parse(original)!.AsObject();
                parsed["IdentitySid"] = "S-1-5-21-1-2-3-999";
                File.WriteAllText(keyPath, parsed.ToJsonString());
                UnchangedOnFailure();
                parsed = JsonNode.Parse(original)!.AsObject();
                parsed["ProtectedKey"] = Convert.ToBase64String(new byte[32]);
                File.WriteAllText(keyPath, parsed.ToJsonString());
                UnchangedOnFailure();
                File.WriteAllText(keyPath, "{"); UnchangedOnFailure();
                File.Delete(keyPath);
                var dbPath = Path.Combine(root, "synthetic.db");
                File.WriteAllText(dbPath, "synthetic database sentinel");
                var before = File.ReadAllBytes(dbPath);
                ExpectRefusal();
                Check(!File.Exists(keyPath) && before.SequenceEqual(File.ReadAllBytes(dbPath)));

                void UnchangedOnFailure()
                {
                    var saved = File.ReadAllBytes(keyPath);
                    ExpectRefusal();
                    Check(saved.SequenceEqual(File.ReadAllBytes(keyPath)));
                }
                void ExpectRefusal()
                {
                    try
                    {
                        var unexpected = ServiceKey.Open(root);
                        CryptographicOperations.ZeroMemory(unexpected);
                    }
                    catch (Exception error) when (error is InvalidOperationException or CryptographicException or
                        System.Text.Json.JsonException or InvalidDataException) { count++; return; }
                    throw new InvalidOperationException("ServiceKey failure did not close storage access.");
                }
            }
            finally { Directory.Delete(root, recursive: true); } // Only this test-created GUID directory.
        }

        void Reject(Action action)
        {
            try { action(); }
            catch (CryptographicException error)
            {
                Check(error.Message == "Worker recovery envelope authentication failed."); return;
            }
            throw new InvalidOperationException("Invalid recovery envelope was accepted.");
        }
    }
}
