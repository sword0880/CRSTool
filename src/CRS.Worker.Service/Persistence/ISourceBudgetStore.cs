using CRS.Worker.Abstractions;

namespace CRS.Worker.Service;

/// <summary>
/// 来源预算的持久化端口，不暴露连接、SQL或数据库类型。
/// 注册表串行调用并接管生命周期；构造失败及关闭时负责释放。
/// Save必须原子、持久提交后才返回；IO故障抛IOException，权限故障抛UnauthorizedAccessException，
/// 损坏状态抛InvalidDataException或FormatException；提供方异常在适配器内转换。
/// 不能以空状态替代读取失败。仅支持一个Worker写入；多宿主部署需另行设计原子预留。
/// </summary>
public interface ISourceBudgetStore : IDisposable
{
    SourceBudgetState Read(string source);
    void Save(string source, SourceBudgetState state);
}
