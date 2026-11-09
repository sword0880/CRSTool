using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Application;

public sealed partial class DesktopWorkflow
{
    private sealed record HistoricalCarryRequirement(bool Required, string? SourceId = null, string? Fingerprint = null);

    public Task<HistoricalRecalculationPlan> PrepareHistoricalRecalculationAsync(string id, CancellationToken token)
        => Task.Run(() =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var source = operations.Repository.Load(id);
            var originals = HistoricalOriginals(source);
            var archived = operations.ArchivedOriginalHashes(originals.Select(o => o.Sha256).Distinct().ToArray(), linked.Token);
            var carry = CarryRequirement(source);
            var hasVaultCarry = FindVaultCarry(carry,linked.Token) is not null;
            return new HistoricalRecalculationPlan(id, source.Broker, source.Year, source.OpeningZero, source.ScopeConfirmed,
                carry.Required && !hasVaultCarry, source.AccountPeriods is { Count: > 0 },
                originals.Select(o => o with { InVault = archived.Contains(o.Sha256) }).ToArray()) { HasVaultCarry = hasVaultCarry };
        }, token);

    public Task<DesktopCalculationOutcome> RecalculateHistoryAsync(HistoricalRecalculationRequest request, CancellationToken token)
    {
        // 固定文件选择，等待汇率或后台线程时不接受界面修改。
        var paths = new Dictionary<string,string>(request.LocalFiles, StringComparer.OrdinalIgnoreCase);
        return Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var cancellation = linked.Token;
            cancellation.ThrowIfCancellationRequested();
            var source = operations.Repository.Load(request.SourceId);
            var originals = HistoricalOriginals(source);
            if (paths.Keys.Any(name => !originals.Any(o => name.Equals(o.Name, StringComparison.OrdinalIgnoreCase))))
                throw new CrsException("补选的文件不属于这条历史任务。");
            var requirement = CarryRequirement(source);
            var carry = request.CarryPath is { } path ? operations.ReadCarry(path) : FindVaultCarry(requirement,cancellation);
            if (requirement.Required && carry is null)
                throw new CrsException("原任务使用了上年结转，请补选期初结转原件。");
            if (carry is not null && (!requirement.Required || requirement.Fingerprint is { } expected
                && !MatchesCarry(carry,expected)))
                throw new CrsException("所选期初结转与原任务不一致，请核对原件。");

            var rates = await operations.LoadRatesAsync(source.Year, cancellation);
            using var calculation = operations.BeginHistoricalCalculation(rates, source.Broker, source.Year,
                originals.Select(o => new HistoricalOriginalInput(o.Name, o.Sha256, paths.GetValueOrDefault(o.Name))).ToArray());
            var result = calculation.Calculator.Calculate(originals.Where(o => o.Kind != HistoricalOriginalKind.Opening).Select(o => o.Name).ToArray(),
                source.Year, source.OpeningZero, source.ScopeConfirmed,
                originals.SingleOrDefault(o => o.Kind == HistoricalOriginalKind.Opening)?.Name,
                carry, cancellation, request.SaveCanonicalSnapshot, source.AccountPeriods, source.SnapshotId);
            cancellation.ThrowIfCancellationRequested();
            calculation.Commit(result,cancellation);
            return new DesktopCalculationOutcome(result, rates.Fingerprint);
        }, token);
    }

    public Task<IReadOnlyList<ImportHistoryComparison>> CompareImportHistoryAsync(string id, CancellationToken token)
        => Task.Run(() =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            return operations.Repository.CompareImportHistory(id, linked.Token);
        }, token);

    private static HistoricalOriginal[] HistoricalOriginals(CalculationResult source)
    {
        if (source.IsAnnualAggregate || source.ForeignCreditAssessment is not null || source.Broker is not ("IBKR" or "FUTU"))
            throw new CrsException("请选择原始券商核算任务。年度汇总和抵免复核请从它们的来源任务发起新算。");
        var originals = source.Sources.Select(item => new HistoricalOriginal(item.File, item.Sha256?.ToLowerInvariant() ?? "",
            item.Opening ? HistoricalOriginalKind.Opening : item.Purpose == "AnnualIncomeSummary"
                ? HistoricalOriginalKind.Income : HistoricalOriginalKind.Activity)).Distinct().ToArray();
        if (originals.Length is < 1 or > 37 || originals.All(o => o.Kind == HistoricalOriginalKind.Opening)
            || originals.Count(o => o.Kind == HistoricalOriginalKind.Opening) > 1
            || originals.Any(o => string.IsNullOrWhiteSpace(o.Name) || o.Name is "." or ".."
                || o.Name.IndexOfAny(['/', '\\', ':']) >= 0 || o.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || o.Sha256.Length != 64 || !o.Sha256.All(Uri.IsHexDigit))
            || originals.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != originals.Length)
            throw new CrsException("旧任务缺少可核对的券商原件清单。请从「数据导入」选择完整原件发起新核算。");
        return originals.OrderBy(o => o.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HistoricalCarryRequirement CarryRequirement(CalculationResult source)
    {
        if (string.IsNullOrWhiteSpace(source.SnapshotJson)) return new(false);
        try
        {
            using var document = JsonDocument.Parse(source.SnapshotJson);
            var root = document.RootElement;
            var hash = root.TryGetProperty("carryFingerprint", out var fingerprint) ? fingerprint.GetString() : null;
            var id = root.TryGetProperty("carry",out var carry) ? carry.GetString() : null;
            return new(hash is not null || id is not null,id,hash);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new CrsException("旧任务的期初依据记录无法读取。请从「数据导入」核对期初资料后发起新核算。");
        }
    }

    private CarryDocument? FindVaultCarry(HistoricalCarryRequirement requirement, CancellationToken token)
    {
        if (requirement is not { SourceId: { } id, Fingerprint: { } hash }) return null;
        var carry = operations.Repository.ReadHistoricalCarry(id,token);
        return carry is not null && MatchesCarry(carry,hash) ? carry : null;
    }

    private static bool MatchesCarry(CarryDocument carry, string expected)
        => expected.Equals(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(carry))),StringComparison.OrdinalIgnoreCase);
}
