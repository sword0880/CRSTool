namespace CRS.Notifications;

/// <summary>公开快照只返回秘密是否存在；钉钉access_token与加签密钥、微信Token均不回传。</summary>
public sealed record WebhookSettings(string ChannelId,bool Enabled=false,bool HasToken=false,bool HasSigningSecret=false,long Revision=0);
public sealed record WebhookSettingsUpdate(string ChannelId,bool Enabled,long Revision,string? Token=null,string? SigningSecret=null,bool ClearToken=false,bool ClearSigningSecret=false)
{
    public override string ToString()=>"WebhookSettingsUpdate { Credentials=[已隐藏] }";
}
