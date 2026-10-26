using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>仅装配共享会话与按功能模块，不拥有连接、密钥或业务SQL。</summary>
internal sealed class NationalTeamStores
{
    public readonly IndustryPeStore Industry;
    public readonly EquityStore Equity;
    public readonly MarketCalendarStore Calendar;
    public readonly IndexSeriesStore Index;
    public readonly MarketActivityStore Activity;
    public readonly NavStore Nav;
    public readonly AlertStore Alerts;
    public readonly EvidenceIntegrityStore Integrity;
    public readonly EtfStore Etf;
    public readonly EtfNavStudyStore NavStudy;
    public NationalTeamStores(MarketDatabaseSession session)
    {
        Calendar=new(session);Activity=new(session,Calendar);
        var pending=new AlertPendingStore(session);
        Etf=new(session,pending.Mark,Calendar,Activity);
        Alerts=new(session,Etf,pending);
        Integrity=new(session,pending.Mark,Etf.RecordQueryStage);
        session.AttachIntegrity(Integrity);
        Industry=new(session);Equity=new(session);Index=new(session);Nav=new(session);
        NavStudy=new(session,Nav,Etf,Alerts);
    }
}
