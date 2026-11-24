using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public sealed partial class VpnNodeChoices : ObservableObject
{
    public sealed record Choice(string Node, string Label);
    private readonly BackgroundPipeClient client = new();
    private static readonly Choice Direct = new("", "直连（不使用 VPN）");
    [ObservableProperty] private IReadOnlyList<Choice> options = [Direct];
    [ObservableProperty] private Choice? selected = Direct;
    [ObservableProperty] private string status = "";

    internal string Node => Selected?.Node ?? "";

    internal void Restore(string? node)
    {
        if (string.IsNullOrEmpty(node)) { Selected = Direct; return; }
        if (!Options.Any(choice => choice.Node == node))
            Options = [..Options, new(node, node + "（已保存）")];
        Selected = Options.First(choice => choice.Node == node);
    }

    internal async Task LoadAsync(bool refresh, CancellationToken token)
    {
        try
        {
            var result = refresh ? await WorkerVpnClient.RefreshAsync(client, token) :
                await WorkerVpnClient.ReadAsync(client, token);
            token.ThrowIfCancellationRequested();
            var node = Node;
            Options = [Direct, ..result.Nodes.Where(name => VpnSubscriptionDetail.Parse(name) is null)
                .Select(name => new Choice(name, result.NodeLabel(name)))];
            Restore(node);
            Status = !result.Configured ? "请先在系统设置中配置 VPN 订阅。" :
                !result.EngineAvailable ? "后台服务未安装 Mihomo。" :
                result.Nodes.Length == 0 ? "点击“刷新节点”读取订阅。" : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is BackgroundServiceException or InvalidOperationException)
        { Status = error.Message; }
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken token) => LoadAsync(true, token);
}
