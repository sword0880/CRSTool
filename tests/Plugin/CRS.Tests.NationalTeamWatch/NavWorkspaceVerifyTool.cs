using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>显式核对用户保存的冻结研究；只做有界读取和数学复算，不打开数据库或联网。</summary>
internal static class NavWorkspaceVerifyTool
{
    internal static void Run(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <1 or >CRS.Serialization.JsonDocumentBlocks.DefaultMaximumBytes)
            throw new InvalidDataException("研究文档为空或超过1MiB。");
        var report=JsonSerializer.Deserialize<EtfNavStudyReport>(stream,new JsonSerializerOptions
            {MaxDepth=32,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true})??throw new InvalidDataException("研究文档为空。");
        var verified=EtfNavStudy.VerifyFrozen(report);
        Console.WriteLine($"冻结NAV研究数学复算通过：版本={verified.AlgorithmVersion}；窗口={verified.Windows.Count}；指纹={verified.Fingerprint}。不代表重新核验原件、机构成交或模型有效性。");
    }
}
