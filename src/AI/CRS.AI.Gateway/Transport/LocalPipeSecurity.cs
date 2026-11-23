using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CRS.AI.Gateway.Transport;

internal static class LocalPipeSecurity
{
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.AuthenticatedUserSid, null);

    public static PipeSecurity CreateAcl(bool management)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException();
        var acl = new PipeSecurity();
        acl.SetOwner(owner);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(management ? Admins : Users, LocalGatewayProtocol.ClientRights, AccessControlType.Allow));
        return acl;
    }

    public static string Authenticate(NamedPipeServerStream pipe, bool management)
    {
        string? sid = null;
        // 在短同步回调中只读令牌。退出回调后才允许访问授权库、审计和 Worker。
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            if (identity is null || identity.ImpersonationLevel < TokenImpersonationLevel.Identification ||
                !identity.IsAuthenticated || identity.Groups?.Contains(Network) == true)
                return;
            if (management && !new WindowsPrincipal(identity).IsInRole(Admins)) return;
            sid = identity.User?.Value;
        });
        return sid ?? throw new UnauthorizedAccessException("无法确认本机 Windows 调用者。");
    }
}
