using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>跨功能只读投影：只从同一会话已核验的事实取输入，不下载或写入研究数据。</summary>
internal sealed class EtfNavStudyStore(IMarketRepositorySession session,NavStore nav,EtfStore etf,EtfEvidenceStore holdings)
{
    internal EtfNavStudyRequest Freeze(EtfKey key,DateOnly from,DateOnly through)
    {
        var cutoff=session.ReadSnapshotCutoff??session.Clock.GetUtcNow();
        if(!session.IsReadSnapshot)throw new InvalidOperationException("NAV份额研究必须使用独立一致性快照。");
        if(from>through||from.DayNumber<90||through.DayNumber-from.DayNumber>366||through>DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("NAV份额研究区间须不超过366日，不能包含未来日期。");
        // 预热只读取已登记日历；若90自然日仍不足20个有效交易日，算法继续报告预热不足。
        var warm=from.AddDays(-90);var coverage=etf.Coverage(key,warm,through);
        var points=etf.Points(key).Where(x=>x.Date>=warm&&x.Date<=through).ToArray();
        var evidence=etf.Evidence(key,warm,through,10000);
        var snapshot=nav.ResearchSnapshot(key,warm,through,cutoff);
        var metadata=holdings.MetadataForNavStudy(key);
        return new(key,from,through,cutoff,snapshot.Dataset,coverage,points,evidence,metadata,snapshot.Revisions);
    }
}
