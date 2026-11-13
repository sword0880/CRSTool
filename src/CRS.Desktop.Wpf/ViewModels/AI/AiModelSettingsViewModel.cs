using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.AI.Models;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>编辑供应商与默认模型，分别显示模型连接和 Gateway 查询结果。</summary>
public sealed partial class AiModelSettingsViewModel(IAiModelService? service) : ObservableObject
{
    private AiModelSettings settings = new(null, new Dictionary<string, AiProviderSetting>());
    private CancellationTokenSource? operation;
    public IReadOnlyList<AiProviderPreset> Providers => AiModelSettings.Presets;

    [ObservableProperty] private AiProviderPreset? selectedProvider = AiModelSettings.Presets[0];
    [ObservableProperty] private string? selectedModel = AiModelSettings.Presets[0].Models[0];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool busy;
    [ObservableProperty] private string saveStatus = "";
    [ObservableProperty] private string modelStatus = "未测试";
    [ObservableProperty] private string gatewayStatus = "未测试";

    public IReadOnlyList<string> Models => SelectedProvider?.Models ?? [];
    public bool CanEdit => service is not null && !Busy;
    public bool HasSavedKey => SelectedProvider is { } provider && settings.Providers.ContainsKey(provider.Id);
    public string KeyStatus => HasSavedKey ? "已配置，留空继续使用已保存的密钥。" : "尚未配置 API Key。";
    internal (string Provider, string Model)? DefaultSelection =>
        settings.DefaultProvider is { } id && settings.Providers.TryGetValue(id, out var profile) ? (id, profile.Model) : null;
    public string DefaultLabel => DefaultSelection is { } selected
        ? $"{Providers.FirstOrDefault(provider => provider.Id == selected.Provider)?.DisplayName ?? selected.Provider} · {selected.Model}"
        : "未配置";

    partial void OnSelectedProviderChanged(AiProviderPreset? value)
    {
        OnPropertyChanged(nameof(Models));
        ApplySavedModel(value);
        NotifyConfiguration();
        SaveStatus = "";
    }

    partial void OnSelectedModelChanged(string? value) => ModelStatus = "未测试";

    /// <summary>打开窗口时读取安全的配置摘要，密钥保留在存储层。</summary>
    public void Reload()
    {
        if (service is null) return;
        try
        {
            settings = service.LoadSettings();
            SelectedProvider = Providers.FirstOrDefault(item => item.Id == settings.DefaultProvider) ?? Providers[0];
            ApplySavedModel(SelectedProvider);
            NotifyConfiguration();
            SaveStatus = "";
        }
        catch (AiModelException error) { SaveStatus = AiModelMessages.Explain(error); }
        ModelStatus = GatewayStatus = "未测试";
    }

    public async Task<bool> TestAndSaveAsync(string? newKey)
    {
        if (!CanEdit || SelectedProvider is not { } provider || SelectedModel is not { } model) return false;
        return await RunAsync(async token =>
        {
            SaveStatus = "";
            ModelStatus = GatewayStatus = "测试中…";
            // 两条链路互相独立，模型连接成功即可保存默认配置。
            var gateway = TestGatewayCoreAsync(token);
            try
            {
                await service!.TestModelAsync(provider.Id, model, newKey, token);
                token.ThrowIfCancellationRequested();
                ModelStatus = "连接成功";
                settings = await service.SaveDefaultAsync(provider.Id, model, newKey, token);
                token.ThrowIfCancellationRequested();
                NotifyConfiguration();
                SaveStatus = "已设为默认模型。";
                return true;
            }
            catch (AiModelException error) when (!token.IsCancellationRequested)
            {
                if (ModelStatus == "测试中…") ModelStatus = AiModelMessages.Explain(error);
                else SaveStatus = AiModelMessages.Explain(error);
                return false;
            }
            finally { await gateway; }
        });
    }

    [RelayCommand]
    private Task TestGatewayAsync() => RunAsync(async token =>
    {
        GatewayStatus = "测试中…";
        await TestGatewayCoreAsync(token);
        return true;
    });

    [RelayCommand]
    private Task RemoveKeyAsync() => RunAsync(async token =>
    {
        if (SelectedProvider is not { } provider) return false;
        try
        {
            settings = await service!.RemoveKeyAsync(provider.Id, token);
            token.ThrowIfCancellationRequested();
            NotifyConfiguration();
            ModelStatus = "未测试";
            SaveStatus = "已清除该供应商的本机配置。";
        }
        catch (AiModelException error) when (!token.IsCancellationRequested) { SaveStatus = AiModelMessages.Explain(error); }
        return true;
    });

    public void CancelPending() => operation?.Cancel();

    private async Task TestGatewayCoreAsync(CancellationToken token)
    {
        try
        {
            await service!.TestGatewayAsync(token);
            token.ThrowIfCancellationRequested();
            GatewayStatus = "ETF 只读查询通过";
        }
        catch (AiModelException error) when (!token.IsCancellationRequested) { GatewayStatus = AiModelMessages.Explain(error); }
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> run)
    {
        if (!CanEdit) return false;
        using var cancel = new CancellationTokenSource();
        operation = cancel;
        Busy = true;
        try { return await run(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return false; }
        catch (AiModelException) when (cancel.IsCancellationRequested) { return false; }
        finally { operation = null; Busy = false; }
    }

    private void ApplySavedModel(AiProviderPreset? provider)
    {
        var saved = provider is null ? null : settings.Providers.GetValueOrDefault(provider.Id)?.Model;
        SelectedModel = provider?.Models.Contains(saved, StringComparer.Ordinal) == true ? saved : provider?.Models.FirstOrDefault();
    }

    private void NotifyConfiguration()
    {
        OnPropertyChanged(nameof(DefaultLabel));
        OnPropertyChanged(nameof(HasSavedKey));
        OnPropertyChanged(nameof(KeyStatus));
    }
}
