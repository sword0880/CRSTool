using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>不同证券宇宙使用独立页签和模型；选中后各自只读加载，不把指数PB拼接到全行业PE。</summary>
public sealed class IndustryResearchHostPage:UserControl
{
    public IndustryResearchHostPage(IndustryResearchViewModel industry,IndexResearchViewModel indices)
    {
        Content=new TabControl{Items={new TabItem{Header="全行业公布PE",Content=new IndustryResearchPage(industry)},new TabItem{Header="行业样本指数与PB",Content=new IndexResearchPage(indices)}}};
    }
}
