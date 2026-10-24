using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>单连接操作与取消边界；实现必须显式处理取消。</summary>
public interface IMarketOperations
{
    T ExecuteOperation<T>(Func<T> work,CancellationToken token);
}
