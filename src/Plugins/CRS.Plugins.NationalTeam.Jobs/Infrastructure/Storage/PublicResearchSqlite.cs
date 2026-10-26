using System.IO;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>国家队公开研究库仅使用普通SQLite；旧加密文件须显式离线导出，禁止静默重建。</summary>
internal static class PublicResearchSqlite
{
    internal static void RequirePlaintextOrMissing(string path)
    {
        if(!File.Exists(path))return;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length==0)return;
        Span<byte> header=stackalloc byte[16];
        if(file.Read(header)!=16||!header.SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("国家队资料库仍为旧加密格式或文件已损坏；请先执行保留原文件的离线迁移，拒绝重建或覆盖。");
    }
    internal static SqliteConnection Open(string path,bool readOnly=false)
    {
        RequirePlaintextOrMissing(path);
        SQLitePCL.Batteries_V2.Init();
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder
            {DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());
        try{connection.Open();return connection;}
        catch{connection.Dispose();throw;}
    }
}
