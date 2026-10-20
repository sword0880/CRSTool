namespace CRS.Worker.Ipc;

/// <summary>可安全展示的后台通信失败；响应丢失不能证明写请求未受理，调用方应保留原RequestId核对。</summary>
public sealed class BackgroundServiceException(string code,Guid requestId,bool outcomeUnknown,string message,Exception? inner=null):IOException(message,inner)
{
    public string Code {get;}=code;
    public Guid RequestId {get;}=requestId;
    public bool OutcomeUnknown {get;}=outcomeUnknown;
}
