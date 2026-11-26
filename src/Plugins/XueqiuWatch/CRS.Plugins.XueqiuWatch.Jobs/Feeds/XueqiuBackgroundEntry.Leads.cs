using System.Net;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.XueqiuWatch.Jobs;

public sealed partial class XueqiuBackgroundEntry
{
    private sealed partial class Session
    {
        public Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken token) =>
            ExecuteCoreAsync(jobId, null, token);

        public Task<BackgroundJobResult> ExecuteViaProxyAsync(string jobId, IWebProxy? proxy, CancellationToken token) =>
            ExecuteCoreAsync(jobId, proxy, token);

        private async Task<BackgroundJobResult> ExecuteCoreAsync(string jobId, IWebProxy? proxy, CancellationToken token)
        {
            if (jobId != WatchProtocol.LeadJobId) return new("Rejected");

            (Author Author, DateTimeOffset? LastComplete, AuthorBackfill? Backfill)[] sources;
            await gate.WaitAsync(token);
            try
            {
                var current = store ?? throw new InvalidOperationException("观点追踪尚未启动。");
                var authors = current.LeadAuthors();
                if (authors.Length == 0) return BackgroundJobResult.Success("观察名单里没有启用的作者。");
                sources = authors.Select(author => (author, current.TimelineCompleted(author.Account),
                    current.PendingBackfill(author.Account))).ToArray();
            }
            finally { gate.Release(); }

            await using var api = openOriginalPages?.Invoke(proxy);
            if (api is not IAuthorTimelineReader reader)
                return new("Rejected", "当前 Worker 未提供雪球作者动态 API。");

            var timeline = await DiscoverTimelinesAsync(reader, sources, token);
            var originals = await ReadOriginalPagesAsync(api, token);
            var retries = new[] { timeline.RetryAfter, originals.RetryAfter }
                .OfType<TimeSpan>().ToArray();
            var checkpoint = timeline.Note + (originals.Note ?? "");
            if (retries.Length > 0)
                return new("RetryDeferred", checkpoint, retries.Max());
            if (timeline.Completed == 0 && timeline.PartiallyRead == 0)
                return new("Failed", checkpoint + " 本轮未取得完整或部分可用的作者动态，已保留历史线索。");
            return BackgroundJobResult.Success(checkpoint);
        }
    }
}