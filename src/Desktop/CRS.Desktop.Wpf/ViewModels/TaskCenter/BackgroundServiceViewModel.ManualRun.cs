using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

public partial class BackgroundServiceViewModel
{
    private sealed record ManualRun(Guid RequestId, string PluginId, string JobId, string Name);

    private ManualRun? manualRun;
    private bool checkingManualRun;
    [ObservableProperty] private string manualRunStatus = "";

    private async Task SubmitManualRunAsync(BackgroundRequest request, ManualRun run)
    {
        UncertainRequest = request;
        ManualRunStatus = $"{run.Name}：正在提交立即执行请求……";
        try
        {
            var response = await client.SendAsync(request, lifetime);
            lifetime.ThrowIfCancellationRequested();
            UncertainRequest = null;
            if (response.Receipt is { } receipt &&
                (response.Code == "Accepted" || receipt.State == "Completed" && receipt.ExecutionId is not null))
            {
                await ShowManualRunReceiptAsync(run, receipt);
                return;
            }

            manualRun = null;
            var message = response.Code switch
            {
                "Accepted" => "后台已受理。可在「当前任务」或「执行记录」查看。",
                "Forbidden" => "需要 Windows 管理员权限。请以管理员身份运行小财牛后重试。",
                "AlreadyRunning" => "该任务已有请求，正在执行或排队。可在「当前任务」查看。",
                "PluginBusy" => "该插件的等待队列已满，请稍后重试。",
                "JobUnavailable" => "后台未加载此任务，请检查「服务状态」。",
                "Maintenance" => "后台正在维护，请稍后重试。",
                "RetryDeferred" => "本次请求等待重试。可在「当前任务」查看。",
                _ => ExecutionLogViewModel.ResultLabel(response.Code)
            };
            ManualRunStatus = $"{run.Name}：{message}";
        }
        catch (BackgroundServiceException error)
        {
            if (!error.OutcomeUnknown)
            {
                UncertainRequest = null;
                manualRun = null;
            }
            ManualRunStatus = $"{run.Name}：{error.Message}";
        }
        catch (System.IO.InvalidDataException)
        {
            ManualRunStatus = $"{run.Name}：后台回执无法核对，请使用「重新确认请求」。";
        }
    }

    internal async Task RefreshManualRunAsync()
    {
        if (manualRun is not { } run || UncertainRequest is not null || checkingManualRun ||
            Busy || lifetime.IsCancellationRequested) return;

        checkingManualRun = true;
        try
        {
            var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetCommand",
                RelatedRequestId: run.RequestId), lifetime);
            if (manualRun != run || lifetime.IsCancellationRequested) return;
            if (response.Code == "Ok" && response.Receipt is { } receipt)
                await ShowManualRunReceiptAsync(run, receipt);
            else if (response.Code == "UnknownRequest")
            {
                manualRun = null;
                ManualRunStatus = $"{run.Name}：后台已受理，回执暂不可查询。请在「执行记录」核对。";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is BackgroundServiceException or System.IO.InvalidDataException)
        {
            if (manualRun == run)
                ManualRunStatus = $"{run.Name}：后台已受理，暂时无法刷新进度。";
        }
        finally { checkingManualRun = false; }
    }

    private async Task ShowManualRunReceiptAsync(ManualRun run, CommandReceipt receipt)
    {
        if (receipt.State != "Completed")
        {
            var message = receipt.State == "Running" ? "正在执行。" :
                receipt.Code == "RetryDeferred" ? "等待重试。可在「当前任务」查看。" : "后台已受理，等待开始。";
            ManualRunStatus = $"{run.Name}：{message}";
            return;
        }

        var result = $"{run.Name}：{ExecutionLogViewModel.ResultLabel(receipt.Code)}。";
        if (receipt.ExecutionId is { } executionId)
        {
            try
            {
                var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetExecution",
                    ExecutionId: executionId), lifetime);
                if (response is { Code: "Ok", Execution.Checkpoint: { Length: > 0 } checkpoint })
                    result += " " + ExecutionLogViewModel.Describe(checkpoint);
            }
            catch (Exception error) when (error is BackgroundServiceException or System.IO.InvalidDataException)
            {
                result += " 可在「执行记录」查看执行摘要。";
            }
        }
        if (manualRun != run || lifetime.IsCancellationRequested) return;
        ManualRunStatus = result;
        manualRun = null;
    }
}
