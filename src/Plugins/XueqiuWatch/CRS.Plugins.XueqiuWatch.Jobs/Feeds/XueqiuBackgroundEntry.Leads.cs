using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        public async Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken token)
        {
            if (jobId != WatchProtocol.LeadJobId) return new("Rejected");
            (string? Url, string? Scope) subscription;
            await gate.WaitAsync(token);
            try
            {
                var current = store ?? throw new InvalidOperationException("观点追踪尚未启动。");
                if (current.LeadAuthors().Length == 0) return BackgroundJobResult.Success("观察名单里没有启用的作者。");
                subscription = current.LeadFeed();
                if (subscription.Url is null) return new("Rejected", "请先在「发言线索」连接 Google Alerts。");
            }
            finally { gate.Release(); }

            // 外部请求不占数据库门槛，提交前再次核对订阅、使用最新启用作者名单。
            var result = await feed.ReadAsync(subscription.Url, token);
            (int Authors, int Added) counts;
            await gate.WaitAsync(token);
            try
            {
                if (store is not { } current || current.LeadFeed() != subscription)
                    return new("Rejected", "RSS 订阅已变更。");
                counts = current.SaveLeadScan(result, subscription.Scope, WatchProtocol.BeijingNow, token);
            }
            finally { gate.Release(); }

            return result.State switch
            {
                "Read" => BackgroundJobResult.Success($"已检查 {counts.Authors} 位启用作者，新增 {counts.Added} 条待核验线索。原帖覆盖未知。"),
                "RateLimited" => new("RetryDeferred", "订阅来源限流，当前覆盖未知。", result.RetryAfter),
                "AccessRequired" => new("AccessBlocked", "订阅暂不可访问，请核对链接和来源状态。"),
                "TimedOut" => new("Failed", "读取订阅超时，当前覆盖未知。"),
                "InvalidFeed" => new("Failed", "订阅未返回可读取的 RSS 或 Atom 内容。"),
                _ => new("Failed", "订阅来源暂不可用，当前覆盖未知。")
            };
        }
    }
}
