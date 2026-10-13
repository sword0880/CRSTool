namespace CRS.Application.Abstractions;

/// <summary>由桌面组合根提供可信 Windows 验证；不能用界面布尔值代替验证结果。</summary>
public interface IWindowsHelloVerifier
{
    string UserIdentity { get; }
    Task VerifyAsync(CancellationToken cancellation = default);
}
