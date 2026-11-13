using System.Security.Cryptography;
using CRS.IO;
using CRS.Worker.Abstractions;

namespace CRS.Worker.FixturePlugin;

public sealed class GoodStorageMigrationEntry:IBackgroundStorageMigrationEntry
{public Task<BackgroundStorageMigrationResult> MigrateAsync(BackgroundStorageMigrationContext context,CancellationToken token)=>StorageMigrationFixture.Run(context,"Good",token);}
public sealed class FailedStorageMigrationEntry:IBackgroundStorageMigrationEntry
{public Task<BackgroundStorageMigrationResult> MigrateAsync(BackgroundStorageMigrationContext context,CancellationToken token)=>StorageMigrationFixture.Run(context,"Failed",token);}
public sealed class HangingStorageMigrationEntry:IBackgroundStorageMigrationEntry
{public Task<BackgroundStorageMigrationResult> MigrateAsync(BackgroundStorageMigrationContext context,CancellationToken token)=>StorageMigrationFixture.Run(context,"Hanging",token);}
public sealed record StorageMigrationMarker(Guid OperationId,string KeyProof,int RunnerPid);

internal static class StorageMigrationFixture
{
    public static async Task<BackgroundStorageMigrationResult> Run(BackgroundStorageMigrationContext context,string mode,CancellationToken token)
    {
        if(context.MigrationId!="synthetic-migration-v1")throw new InvalidDataException("仅合成维护标识。");
        using var lease=new FileStream(Path.Combine(context.DataDirectory,"runner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        File.WriteAllText(Path.Combine(context.DataDirectory,"maintenance-pid.txt"),Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(mode=="Hanging")await Task.Delay(Timeout.InfiniteTimeSpan); // 故意不响应取消，验证父进程终止。
        if(mode=="Failed")throw new InvalidOperationException("仅合成迁移失败。");
        token.ThrowIfCancellationRequested();
        if(File.ReadAllText(Path.Combine(context.SnapshotDirectory,"before.raw"))!="synthetic-preserved-before")throw new InvalidDataException("合成快照不符。");
        var path=Path.Combine(context.DataDirectory,"synthetic-migration.json");var existed=File.Exists(path);
        if(existed)
        {
            if(AuthenticatedJsonFile.Read<StorageMigrationMarker>(path,context.DataKey,"Synthetic.StorageMigration/v1").OperationId!=context.OperationId)throw new InvalidDataException("合成代次不符。");
        }
        else
        {
            var proof=Convert.ToHexString(HMACSHA256.HashData(context.DataKey,"synthetic-plugin-key-proof"u8));
            AuthenticatedJsonFile.Write(path,context.DataKey,"Synthetic.StorageMigration/v1",new StorageMigrationMarker(context.OperationId,proof,Environment.ProcessId));
        }
        return new(context.MigrationId,context.OperationId,existed?"AlreadyCompleted":"Completed",1,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
