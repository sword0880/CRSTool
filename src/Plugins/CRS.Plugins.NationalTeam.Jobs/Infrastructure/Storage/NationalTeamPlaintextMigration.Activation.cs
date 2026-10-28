using System.Security.Cryptography;
using CRS.IO;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
public sealed partial class NationalTeamPlaintextMigration
{
    private void Activate(string root,string operation,NationalTeamMigrationRecord record,CancellationToken token)
    {
        // 先核对整组，避免第二个库在准备后改变时已经切换第一个库。
        foreach(var file in record.Files)
        {
            token.ThrowIfCancellationRequested();
            RejectRollbackJournal(root,file.Relative);
            if(!file.Converted){RequireOriginals(root,file);continue;}
            foreach(var suffix in Sidecars)
            {
                var relative=file.Relative+suffix;var live=PhysicalPathGuard.Child(root,relative);var archive=PhysicalPathGuard.Child(operation,"original/"+relative);
                if(file.Originals.TryGetValue(relative,out var hash))
                {
                    if(File.Exists(archive)){RequireHash(archive,hash);if(File.Exists(live)){if(suffix.Length>0)throw new InvalidDataException("边文件重复出现。");RequireHash(live,file.PlainSha256!);}}
                    else RequireHash(live,hash);
                }
                else if(File.Exists(live)||File.Exists(archive))throw new InvalidDataException("文件集合发生未登记变化。");
            }
            var main=PhysicalPathGuard.Child(root,file.Relative);var oldMain=PhysicalPathGuard.Child(operation,"original/"+file.Relative);
            if(!File.Exists(oldMain)||!File.Exists(main))RequireHash(PhysicalPathGuard.Child(operation,file.PreparedRelative!),file.PlainSha256!);
        }
        foreach(var file in record.Files)
        {
            token.ThrowIfCancellationRequested();
            if(!file.Present){if(File.Exists(PhysicalPathGuard.Child(root,file.Relative)))throw new InvalidDataException("原本不存在的库已出现，拒绝覆盖。");continue;}
            if(!file.Converted){RequireOriginals(root,file);continue;}
            // 先移走旧WAL/SHM再换主文件；每一步中断均由已认证的Prepared记录识别，不重置资料。
            foreach(var suffix in Sidecars)
            {
                var relative=file.Relative+suffix;var live=PhysicalPathGuard.Child(root,relative);
                if(!file.Originals.TryGetValue(relative,out var expected))
                {if(suffix.Length>0&&File.Exists(live))throw new InvalidDataException("出现未登记边文件，保留现场。");continue;}
                var archive=PhysicalPathGuard.Child(operation,"original/"+relative);Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                if(File.Exists(archive))
                {
                    RequireHash(archive,expected);
                    if(suffix.Length>0&&File.Exists(live))throw new InvalidDataException("旧边文件重复出现，拒绝混合恢复。");
                    if(suffix.Length==0&&File.Exists(live))RequireHash(live,file.PlainSha256!);
                }
                else
                {
                    RequireHash(live,expected);File.Move(live,archive);checkpoint?.Invoke("OriginalMoved:"+relative);
                }
            }
            var destination=PhysicalPathGuard.Child(root,file.Relative);
            if(!File.Exists(destination))
            {
                var staged=PhysicalPathGuard.Child(operation,file.PreparedRelative!);RequireHash(staged,file.PlainSha256!);File.Move(staged,destination);checkpoint?.Invoke("PlainPublished:"+file.Relative);
            }
            RequireHash(destination,file.PlainSha256!);
            if(LegacyNationalTeamExport.InspectPlain(destination)!=file.Logical)throw new InvalidDataException("发布后逻辑指纹不符。");
        }
    }
    private static NationalTeamMigrationFile Capture(string root,string snapshot,string relative)
    {
        RejectRollbackJournal(root,relative);RejectRollbackJournal(snapshot,relative);
        var originals=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var suffix in Sidecars)
        {
            var original=PhysicalPathGuard.Child(snapshot,relative+suffix);var live=PhysicalPathGuard.Child(root,relative+suffix);
            if(File.Exists(original)!=File.Exists(live))throw new InvalidDataException("活动资料与停机快照文件集合不符。");
            if(File.Exists(original)){var hash=Hash(original);RequireHash(live,hash);originals.Add(relative+suffix,hash);}
        }
        var present=originals.ContainsKey(relative);if(!present&&originals.Count>0)throw new InvalidDataException("只存在边文件而缺主库。");
        return new(relative,present,false,null,null,originals,null);
    }
    private static void Validate(NationalTeamMigrationRecord record,string root,string snapshot,Guid operationId)
    {
        if(record.Version!=1||record.OperationId!=operationId||!record.PluginRoot.Equals(root,StringComparison.OrdinalIgnoreCase)||!record.SnapshotRoot.Equals(snapshot,StringComparison.OrdinalIgnoreCase)
            ||record.State is not ("Preparing" or "Prepared" or "Completed")||record.Attempts is <0 or >8||record.Files.Count!=2||!record.Files.Select(x=>x.Relative).SequenceEqual(Databases))throw new InvalidDataException("迁移日志身份或阶段不符。");
        foreach(var file in record.Files)
        {
            if(file.Originals.Any(x=>!Sidecars.Any(s=>x.Key==file.Relative+s)||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(x.Value))||file.Present!=file.Originals.ContainsKey(file.Relative)
                ||file.Converted&&(!file.Present||file.PreparedRelative is null||!file.PreparedRelative.StartsWith("prepared/",StringComparison.Ordinal)||Path.GetFileName(file.PreparedRelative)!=Path.GetFileName(file.Relative))
                ||record.State!="Preparing"&&file.Present&&(file.Logical is null||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(file.PlainSha256)))throw new InvalidDataException("迁移文件描述不符。");
        }
    }
    private static void VerifySnapshot(NationalTeamMigrationRecord record)
    {
        foreach(var file in record.Files)foreach(var suffix in Sidecars)
        {
            RejectRollbackJournal(record.SnapshotRoot,file.Relative);
            var relative=file.Relative+suffix;var path=PhysicalPathGuard.Child(record.SnapshotRoot,relative);
            if(file.Originals.TryGetValue(relative,out var hash))RequireHash(path,hash);else if(File.Exists(path))throw new InvalidDataException("停机快照增加了未登记文件。");
        }
    }
    private static void RequireOriginals(string root,NationalTeamMigrationFile file)
    {
        RejectRollbackJournal(root,file.Relative);
        foreach(var suffix in Sidecars)
        {
            var path=PhysicalPathGuard.Child(root,file.Relative+suffix);
            if(file.Originals.TryGetValue(file.Relative+suffix,out var hash))RequireHash(path,hash);else if(File.Exists(path))throw new InvalidDataException("活动文件集合与快照不符。");
        }
    }
    private static bool IsPlain(string path)
    {using var file=File.OpenRead(path);Span<byte> bytes=stackalloc byte[16];return file.Read(bytes)==16&&bytes.SequenceEqual("SQLite format 3\0"u8);}
    private static string Hash(string path){PhysicalPathGuard.RejectLinks(path);using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static void RequireHash(string path,string expected){if(!File.Exists(path)||!Hash(path).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("迁移文件缺失或摘要不符，保留现场。");}
    private static void RejectRollbackJournal(string root,string relative)
    {
        var path=PhysicalPathGuard.Child(root,relative);PhysicalPathGuard.RejectLinks(path+"-journal");
        if(File.Exists(path+"-journal")||Directory.Exists(Path.GetDirectoryName(path)!)&&Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+"-mj*").Any())throw new InvalidDataException("存在未支持的回滚日志，不忽略、不自动清理。");
    }
}
