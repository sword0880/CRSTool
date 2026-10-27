using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>外层组合根选择PDF适配器；应用与各功能用例只接受解析端口。</summary>
public static class NationalTeamApplicationComposition
{
    public static NationalTeamApplication Create(IPluginHostContext context,Func<IMarketRepository> factory,IRawEvidenceStore raw)
        =>new(context,factory,raw,new OfficialEtfHolderCandidateParser());
}
