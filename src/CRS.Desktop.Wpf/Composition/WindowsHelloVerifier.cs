using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Views;
using Windows.Security.Credentials.UI;

namespace CRS.DesktopClient.Composition;

/// <summary>真实系统验证只由组合根装配，结果不能由界面输入伪造。</summary>
internal sealed class WindowsHelloVerifier : IWindowsHelloVerifier
{
    public async Task<bool> IsAvailableAsync() => await UserConsentVerifier.CheckAvailabilityAsync()==UserConsentVerifierAvailability.Available;
    public string UserIdentity => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new CrsException("无法获取当前 Windows 用户身份。");

    public async Task VerifyAsync(CancellationToken cancellation = default)
    {
        var app=System.Windows.Application.Current??throw new CrsException("桌面验证环境不可用。");
        await app.Dispatcher.InvokeAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            var owner=app.Windows.OfType<Window>().LastOrDefault(x=>x.IsVisible && x is VaultWindow or SettingsWindow)
                ?? throw new CrsException("请在安全验证窗口中进行 Windows Hello 验证。");
            var availability=await UserConsentVerifier.CheckAvailabilityAsync();
            cancellation.ThrowIfCancellationRequested();
            if(availability!=UserConsentVerifierAvailability.Available)
                throw new CrsException("Windows Hello 不可用，请先在 Windows 设置中配置 PIN、指纹或人脸；本次不会切换认证方式。");
            // WPF 必须使用带窗口句柄的桌面互操作 API；只有 Verified 才能发布会话。
            var hwnd=new WindowInteropHelper(owner).EnsureHandle();
            var result=await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd,CRS.DesktopClient.Services.ProductIdentity.Name+"：验证身份以访问加密数据").AsTask(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if(result==UserConsentVerificationResult.Canceled) throw new OperationCanceledException(cancellation);
            if(result!=UserConsentVerificationResult.Verified) throw new CrsException("Windows Hello 未验证成功，数据保持锁定，请重试。");
        }).Task.Unwrap().ConfigureAwait(false);
    }
}
