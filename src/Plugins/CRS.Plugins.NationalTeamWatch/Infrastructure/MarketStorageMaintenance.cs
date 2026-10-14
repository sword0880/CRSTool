using Microsoft.Data.Sqlite;
using CRS.Plugin.Abstractions;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>离线维护只操作宿主提供的插件库路径，不访问税务连接或密钥。</summary>
public sealed class MarketStorageMaintenance(IPluginDataPaths paths):IPluginStorageMaintenance
{
    public int SupportedSchemaVersion=>1;
    private static SqliteConnection Open(string file,SqliteOpenMode mode)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=mode,Pooling=false}.ToString());
        connection.Open();return connection;
    }
    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="PRAGMA integrity_check;";
        if(command.ExecuteScalar() as string!="ok")throw new InvalidDataException("插件数据库完整性检查失败。");
        command.CommandText="PRAGMA foreign_key_check;";
        using var rows=command.ExecuteReader();if(rows.Read())throw new InvalidDataException("插件数据库存在失效的外键引用。");
    }
    public Task SnapshotAsync(string sourceDatabase,string destinationDatabase,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(File.Exists(destinationDatabase))throw new InvalidDataException("快照或候选库已经存在，拒绝覆盖。");
        using var source=Open(sourceDatabase,SqliteOpenMode.ReadOnly);CheckIntegrity(source);
        using var destination=Open(destinationDatabase,SqliteOpenMode.ReadWriteCreate);
        // 原生Backup API包含WAL中的已提交页，不能只复制主.db文件。
        using var backup=SQLitePCL.raw.sqlite3_backup_init(destination.Handle,"main",source.Handle,"main");
        if(backup.IsInvalid)throw new InvalidDataException("无法创建插件数据库快照。");
        var attempts=0;
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();var result=SQLitePCL.raw.sqlite3_backup_step(backup,128);
            if(result==SQLitePCL.raw.SQLITE_DONE)break;
            if(result==SQLitePCL.raw.SQLITE_OK){attempts=0;continue;}
            if(result is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED && attempts++<20){Thread.Sleep(25);continue;}
            throw new InvalidDataException("插件快照失败，SQLite状态="+result);
        }
        CheckIntegrity(destination);cancellationToken.ThrowIfCancellationRequested();
    },cancellationToken);
    public Task MigrateAsync(string candidateDatabase,int targetSchemaVersion,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(targetSchemaVersion!=SupportedSchemaVersion || Path.GetFileName(candidateDatabase)!="market.db")throw new InvalidDataException("该插件不支持候选Schema或数据库名称。");
        // 仅迁移候选代次，复用插件自身Schema初始化；旧库与旧原件不变。
        using var repository=new MarketRepository(Path.GetDirectoryName(candidateDatabase)!,new RawEvidenceStore(paths.Raw));
        using var connection=Open(candidateDatabase,SqliteOpenMode.ReadWrite);CheckIntegrity(connection);
        using var command=connection.CreateCommand();command.CommandText="BEGIN IMMEDIATE; UPDATE plugin_schema_migration SET plugin_package_version=plugin_package_version; ROLLBACK;";
        command.ExecuteNonQuery();cancellationToken.ThrowIfCancellationRequested();
    },cancellationToken);
    public Task VerifyAsync(string database,int expectedSchemaVersion,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        cancellationToken.ThrowIfCancellationRequested();using var connection=Open(database,SqliteOpenMode.ReadOnly);CheckIntegrity(connection);
        using var command=connection.CreateCommand();command.CommandText="PRAGMA user_version;";
        if(expectedSchemaVersion!=SupportedSchemaVersion || Convert.ToInt32(command.ExecuteScalar())!=expectedSchemaVersion)throw new InvalidDataException("插件代码与数据库Schema不匹配。");
        command.CommandText="SELECT sha256 FROM source_fetch UNION SELECT original_sha256 FROM source_fetch;";
        using var reader=command.ExecuteReader();var raw=new RawEvidenceStore(paths.Raw);
        while(reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!raw.Verify(reader.GetString(0)))throw new InvalidDataException("候选库引用的原始证据缺失或损坏。");
        }
    },cancellationToken);
}
