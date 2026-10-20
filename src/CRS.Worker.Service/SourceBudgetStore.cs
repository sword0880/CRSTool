using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

namespace CRS.Worker.Service;

/// <summary>仅由共享注册表在互斥区内使用；损坏或未知版本失败关闭，不重建来源状态。</summary>
internal sealed class SourceBudgetStore : IDisposable
{
    private readonly SqliteConnection db;
    private bool failed;
    public SourceBudgetStore(string directory, byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("服务密钥长度不合法。");
        Directory.CreateDirectory(directory);
        SQLitePCL.Batteries_V2.Init();
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32,
            info: Encoding.UTF8.GetBytes("CRS.Worker.SourceBudget/v1"));
        try { db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "source-budgets.db"), Password = Convert.ToHexString(key), Pooling = false }.ToString()); }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version=Convert.ToInt32(command.ExecuteScalar());
            if (version is not (0 or 1)) throw new InvalidDataException("来源预算库版本不受支持。");
            if(version==1)
            {
                command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='source_budget'";
                if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new InvalidDataException("已初始化的来源预算库缺少状态表。");
            }
            command.CommandText = """
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS source_budget(source TEXT PRIMARY KEY,next_utc TEXT NOT NULL,blocked_utc TEXT NOT NULL,failures INTEGER NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        catch { db.Dispose(); throw; }
    }
    private T Guard<T>(Func<T> operation)
    {
        if(failed)throw new InvalidDataException("来源预算存储已故障，须重启核验后恢复。");
        try{return operation();}
        catch(Exception error) when(error is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {failed=true;throw;}
    }
    public SourceBudgetState Read(string source)=>Guard(()=>ReadCore(source));
    private SourceBudgetState ReadCore(string source)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT next_utc,blocked_utc,failures FROM source_budget WHERE source=$s";
        command.Parameters.AddWithValue("$s", source);
        using var reader = command.ExecuteReader();
        if(!reader.Read())return new(DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0);
        var failures=reader.GetInt64(2);
        if(failures is <0 or >16)throw new InvalidDataException("来源预算失败计数损坏。");
        var state=new SourceBudgetState(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), (int)failures);
        return state;
    }
    public void Save(string source, SourceBudgetState state)=>Guard(()=>{SaveCore(source,state);return 0;});
    private void SaveCore(string source, SourceBudgetState state)
    {
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO source_budget VALUES($s,$n,$b,$f) ON CONFLICT(source) DO UPDATE SET next_utc=$n,blocked_utc=$b,failures=$f";
        command.Parameters.AddWithValue("$s", source);
        command.Parameters.AddWithValue("$n", state.NextUtc.ToString("O"));
        command.Parameters.AddWithValue("$b", state.BlockedUtc.ToString("O"));
        command.Parameters.AddWithValue("$f", state.Failures);
        command.ExecuteNonQuery();
    }
    public void Dispose() => db.Dispose();
}
