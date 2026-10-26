namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>持久来源策略端口；故障不能返回空状态或恢复到内存预算。</summary>
public interface ISourceRequestPolicy : IDisposable
{
    (DateTimeOffset Next,DateTimeOffset Blocked,int Failures) Read(string host);
    void Reserve(string host,DateTimeOffset now);
    void Success(string host);
    void Failure(string host,DateTimeOffset now,int status,DateTimeOffset? retryAfter=null);
}
