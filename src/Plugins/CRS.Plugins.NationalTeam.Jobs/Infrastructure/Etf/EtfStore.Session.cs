using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EtfStore : IAlertMarketFacts
{
    private readonly IMarketRepositorySession session;
    private readonly Action markPending;
    private readonly MarketCalendarStore calendarStore;
    private readonly MarketActivityStore activityStore;
    public EtfStore(IMarketRepositorySession session,Action markPending,MarketCalendarStore calendarStore,MarketActivityStore activityStore)
    {this.session=session;this.markPending=markPending;this.calendarStore=calendarStore;this.activityStore=activityStore;}
    private SqliteConnection db=>session.Connection;
    private RawEvidenceStore raw=>session.RawEvidence;
    private TimeProvider clock=>session.Clock;
    private Action? beforeWrite=>session.BeforeWrite;
    private CancellationToken operationCancellation=>session.OperationCancellation;
    private bool readSnapshot=>session.IsReadSnapshot;
    private string Now=>session.Now;
    private SqliteCommand Command(string sql,params (string,object?)[] args)=>session.Command(sql,args);
    private void Execute(string sql,params (string,object?)[] args)=>session.Execute(sql,args);
    private object? Scalar(string sql,params (string,object?)[] args)=>session.Scalar(sql,args);
    private static string Decimal(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    private void MarkAlertPending()=>markPending();
    private bool Committed(Guid? id)=>session.Committed(id);
    private void RecordCommand(Guid? id,string operation)=>session.RecordCommand(id,operation);
    private void CheckReadLimit(int count)=>session.CheckReadLimit(count);
    private IReadOnlyDictionary<string,bool> ValidateEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
        =>session.ValidateEvidence(shareKey,includeCalendar,activityOnly,dashboardOnly,includeAttempts);
    private bool? IsReviewedTradingDay(string market,DateOnly day)=>calendarStore.IsReviewedTradingDay(market,day);
    private List<MarketActivityPoint> ReadMarketActivity()=>activityStore.ReadMarketActivity();
    List<EvidenceRow> IAlertMarketFacts.Evidence()=>ReadEvidence();
    List<(DateOnly Date,bool Valid)> IAlertMarketFacts.Calendar(string market)=>ReadCalendar(market);
    List<SharePoint> IAlertMarketFacts.Points(EtfKey key)=>ReadPoints(key);
    bool? IAlertMarketFacts.IsReviewedTradingDay(string market,DateOnly day)=>IsReviewedTradingDay(market,day);
}
