using System.Net;
using System.Text;
using System.Text.Json;
namespace CRS.Notifications;

/// <summary>有界HTTP提交；固定提供商地址，禁用重定向与自动重试，异常响应不会泄露含密钥的URL。</summary>
public abstract class WebhookNotificationChannel(WebhookSettingsStore settings,HttpClient? client=null):INotificationChannel
{
    private static readonly HttpClient Shared=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
    private readonly SemaphoreSlim sending=new(1);
    private DateTimeOffset nextRequest;
    public abstract NotificationChannelDescriptor Descriptor {get;}
    private protected abstract HttpRequestMessage CreateRequest(string access,string signing,NotificationEnvelope message);
    private protected abstract (bool Accepted,string Code,string? Receipt) ReadResponse(JsonElement root);
    public Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(profileId!="default")return Task.FromResult(new NotificationChannelStatus(Descriptor.Id,profileId,false,false,"ProfileNotFound"));
        var saved=settings.Read(Descriptor.Id);
        return Task.FromResult(new NotificationChannelStatus(Descriptor.Id,profileId,saved.Enabled,saved.HasToken&&(Descriptor.Id!="dingtalk"||saved.HasSigningSecret),"ConfiguredStatus"));
    }
    public async Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token)
    {
        NotificationDeliveryResult Result(NotificationDeliveryState state,string code,string? receipt=null)=>new(Descriptor.Id,state,code,message.MessageId,receipt);
        token.ThrowIfCancellationRequested();
        if(profileId!="default")return Result(NotificationDeliveryState.Unavailable,"ProfileNotFound");
        // 各提供商采用更保守的字节上限；明确拒绝而非截断金融风险提示。
        if(string.IsNullOrWhiteSpace(message.MessageId)||string.IsNullOrWhiteSpace(message.Title)||message.Title.Any(char.IsControl)
            ||message.Title.Length>100||string.IsNullOrEmpty(message.Body)||Encoding.UTF8.GetByteCount(message.Title+"\n"+message.Body)>3500)
            return Result(NotificationDeliveryState.PermanentFailure,"ProviderMessageTooLargeOrInvalid");
        await sending.WaitAsync(token);
        try
        {
            var saved=settings.Open(Descriptor.Id);if(!saved.Settings.Enabled)return Result(NotificationDeliveryState.Disabled,"ChannelNotEnabled");
            if(CRS.Timing.BeijingClock.Now<nextRequest)return Result(NotificationDeliveryState.RetryableFailure,"LocalRateLimited");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request=CreateRequest(saved.Token,saved.SigningSecret,message);
            nextRequest=CRS.Timing.BeijingClock.Now.AddSeconds(3);
            try
            {
                using var response=await (client??Shared).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(response.StatusCode==HttpStatusCode.TooManyRequests)
                {
                    var retry=response.Headers.RetryAfter;
                    var until=retry?.Date??CRS.Timing.BeijingClock.Now.Add(retry?.Delta??TimeSpan.FromMinutes(1));
                    if(until>nextRequest)nextRequest=until;
                    return Result(NotificationDeliveryState.RetryableFailure,"ProviderRateLimited");
                }
                if(!response.IsSuccessStatusCode)return Result(NotificationDeliveryState.OutcomeUnknown,"ProviderHttpFailure");
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes=new byte[16385];var count=0;
                while(count<bytes.Length){var read=await stream.ReadAsync(bytes.AsMemory(count),timeout.Token);if(read==0)break;count+=read;}
                if(count>16384)return Result(NotificationDeliveryState.OutcomeUnknown,"ProviderResponseTooLarge");
                using var json=JsonDocument.Parse(bytes.AsMemory(0,count),new JsonDocumentOptions{MaxDepth=16});
                var parsed=ReadResponse(json.RootElement);
                return Result(parsed.Accepted?NotificationDeliveryState.Accepted:NotificationDeliveryState.PermanentFailure,parsed.Code,parsed.Receipt);
            }
            catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
            {return Result(NotificationDeliveryState.OutcomeUnknown,"ProviderOutcomeUnknown");}
        }
        finally{sending.Release();}
    }
    private protected static int Code(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.TryGetInt32(out var number)?number:throw new JsonException();
}
