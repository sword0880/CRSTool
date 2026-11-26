using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        private async Task<(string? Note, TimeSpan? RetryAfter)> ReadOriginalPagesAsync(IOriginalPageReader reader,
            CancellationToken token)
        {
            // 在存储锁内取出待查线索，释放锁再请求 API，避免阻塞界面查询和补录。
            OriginalPageLead[] queue;
            await gate.WaitAsync(token);
            try
            {
                var now = WatchProtocol.BeijingNow;
                if (store?.ApiPause(now) is { } pause)
                    return ($" 雪球原帖 API（HTTP {pause.Status}）暂停读取至 {pause.Until:yyyy-MM-dd HH:mm}。",
                        pause.Status == 429 ? pause.Until - now : null);
                queue = store?.OriginalPageQueue(now, 20) ?? [];
            }
            finally { gate.Release(); }
            if (queue.Length == 0) return default;

            var read = 0;
            var saved = 0;
            var incomplete = 0;
            var unavailable = 0;
            OriginalPageRead? restricted = null;
            var consecutiveFailures = 0;
            TimeSpan? retry = null;
            foreach (var post in queue)
            {
                // 首条立即处理，后续逐条等待 1 到 4 秒。取消任务时立即结束等待。
                if (read > 0) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 5)), token);
                var result = await reader.ReadAsync(post, token);
                read++;
                await gate.WaitAsync(token);
                try
                {
                    if (store?.SaveOriginalPage(post, result, WatchProtocol.BeijingNow, token) == true) saved++;
                }
                finally { gate.Release(); }
                if (result.State == "BodyUnavailable") incomplete++;
                if (result.State == "Unavailable") unavailable++;
                // 每条结果单独落库。后续请求限流、访问受限或连续连接失败时，已取得的结果仍可查询。
                retry = result.RetryAfter;
                restricted = result.State == "AccessRestricted" ? result : null;
                consecutiveFailures = result.State == "Unavailable" ? consecutiveFailures + 1 : 0;
                if (retry is not null || restricted is not null || consecutiveFailures >= 3) break;
            }
            var note = $" API 核对原帖 {read} 条，取得全文 {saved} 条，正文不完整 {incomplete} 条，读取失败 {unavailable} 条。";
            if (retry is not null) note += " 雪球原帖 API 限流（HTTP 429），本轮停止读取原帖。";
            if (restricted?.HttpStatus == 567)
                note += " 雪球原帖 API 访问受限（HTTP 567），暂停原帖读取 6 小时。";
            else if (restricted is not null) note += " 雪球原帖 API 访问受限，本轮停止读取原帖。";
            if (consecutiveFailures >= 3) note += " 原帖连续连接失败，本轮停止读取原帖。";
            return (note, retry);
        }
    }
}
