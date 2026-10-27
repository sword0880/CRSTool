namespace CRS.Plugin.Abstractions;

/// <summary>官方插件唯一入口；插件自行创建服务对象，不接收宿主根容器。</summary>
public interface ICrsPluginEntry { IPluginSession CreateSession(IPluginHostContext context); }
/// <summary>一次登录工作区对应一个插件会话，停止和释放必须支持重复调用。</summary>
public interface IPluginSession : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
/// <summary>仅暴露已授予的通用能力，不提供税务数据库、密钥或账户服务。</summary>
public interface IPluginHostContext
{
    string PluginId { get; }
    IPluginJobScheduler Jobs { get; }
    IPluginDataPaths DataPaths { get; }
    IPluginHttpGateway Http { get; }
    IPluginDiagnostics Diagnostics { get; }
}
/// <summary>插件私有存储目录配置；这是同进程协作契约，不是操作系统权限沙箱。</summary>
public interface IPluginDataPaths
{
    string Data { get; }
    string Raw { get; }
    string Log { get; }
    string Backup { get; }
}
/// <summary>同一插件任务串行互斥，并随宿主会话取消。</summary>
public interface IPluginJobScheduler
{
    Task RunAsync(string jobId, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}
/// <summary>经过限域与响应大小检查的公开HTTP结果，不携带身份凭据。</summary>
public sealed record PublicHttpResponse(int StatusCode, byte[] Content, string? ContentType);
/// <summary>公开数据网关；使用此能力前仍须完成对应来源的许可和字段验证。</summary>
public interface IPluginHttpGateway
{
    Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken);
}
/// <summary>可选的公开JSON查询能力；调用方仍须按来源约束地址、请求字段及大小，不携带身份凭据。</summary>
public interface IPluginJsonHttpGateway
{
    Task<PublicHttpResponse> PostJsonAsync(Uri uri,byte[] json,CancellationToken cancellationToken);
}
/// <summary>公开证券研究诊断，不得写入私人账户、令牌或认证材料。</summary>
public interface IPluginDiagnostics { void Report(string code, string message); }
