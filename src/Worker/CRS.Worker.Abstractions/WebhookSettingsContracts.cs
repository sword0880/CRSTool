// 后台Webhook设置合同：读取仅提供凭据存在标志，更新明确区分保留、替换与清除；字符串展示隐藏凭据。
namespace CRS.Worker.Abstractions;
public sealed record WorkerWebhookSettings(string ChannelId,bool Enabled=false,bool HasToken=false,bool HasSigningSecret=false,long Revision=0);
public sealed record WorkerWebhookUpdate(string ChannelId,bool Enabled,long Revision,string? Token=null,string? SigningSecret=null,bool ClearToken=false,bool ClearSigningSecret=false)
{public override string ToString()=>"WorkerWebhookUpdate { Credentials=[已隐藏] }";}
