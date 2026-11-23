// 应用层Webhook设置与编辑草稿：用版本避免覆盖并发修改，凭据更新意图和凭据存在状态分别传递。
namespace CRS.Application.Abstractions;
public sealed record NotificationWebhookSettings(string ChannelId,bool Enabled=false,bool HasToken=false,bool HasSigningSecret=false,long Revision=0);
public sealed record NotificationWebhookEdit(string ChannelId,bool Enabled,long Revision,string? Token=null,string? SigningSecret=null,bool ClearToken=false,bool ClearSigningSecret=false)
{public override string ToString()=>"NotificationWebhookEdit { Credentials=[已隐藏] }";}
