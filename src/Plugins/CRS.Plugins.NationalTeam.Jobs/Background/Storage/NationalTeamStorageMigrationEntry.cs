using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>独立维护入口只负责国家队两库，不建立采集会话或创建任务。</summary>
public sealed class NationalTeamStorageMigrationEntry:IBackgroundStorageMigrationEntry
{
    public const string MigrationId="national-team-plaintext-v1";
    public Task<BackgroundStorageMigrationResult> MigrateAsync(BackgroundStorageMigrationContext context,CancellationToken token)
    {
        if(context.PluginId!="crs.national-team"||context.MigrationId!=MigrationId)throw new InvalidDataException("未知国家队维护操作。");
        var result=new NationalTeamPlaintextMigration().Run(context.DataDirectory,context.SnapshotDirectory,context.OperationId,context.DataKey,token);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result.Files)));
        return Task.FromResult(new BackgroundStorageMigrationResult(MigrationId,result.OperationId,result.State,result.ConvertedDatabases,fingerprint));
    }
}
