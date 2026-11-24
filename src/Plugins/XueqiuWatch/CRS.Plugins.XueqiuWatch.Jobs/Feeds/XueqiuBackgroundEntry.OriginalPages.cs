using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        private async Task<(string? Note, TimeSpan? RetryAfter)> ReadOriginalPagesAsync(System.Net.IWebProxy? proxy,
            CancellationToken token)
        {
            OriginalPageLead[] queue;
            await gate.WaitAsync(token);
            try { queue = store?.OriginalPageQueue(WatchProtocol.BeijingNow, 20) ?? []; }
            finally { gate.Release(); }
            if (queue.Length == 0) return default;

            IOriginalPageReader reader;
            try { reader = await openOriginalPages!(proxy, token); }
            catch (Exception error) when (error is PlaywrightException or IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or TimeoutException)
            {
                token.ThrowIfCancellationRequested();
                return (" 原帖浏览器未启动，请核对 Worker 主机的 Chrome 安装和网络。", null);
            }

            await using (reader)
            {
                var read = 0;
                var saved = 0;
                var restricted = false;
                var consecutiveFailures = 0;
                TimeSpan? retry = null;
                foreach (var post in queue)
                {
                    if (read > 0) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 5)), token);
                    var result = await reader.ReadAsync(post, token);
                    read++;
                    await gate.WaitAsync(token);
                    try
                    {
                        if (store?.SaveOriginalPage(post, result, WatchProtocol.BeijingNow, token) == true) saved++;
                    }
                    finally { gate.Release(); }
                    retry = result.RetryAfter;
                    restricted = result.State == "AccessRestricted";
                    consecutiveFailures = result.State == "Unavailable" ? consecutiveFailures + 1 : 0;
                    if (retry is not null || restricted || consecutiveFailures >= 3) break;
                }
                var note = $" 核对原帖 {read} 条，取得全文 {saved} 条。";
                if (restricted) note += " 雪球原帖访问受限，本轮停止读取原帖。";
                if (consecutiveFailures >= 3) note += " 原帖连续连接失败，本轮停止读取原帖。";
                return (note, retry);
            }
        }
    }
}
