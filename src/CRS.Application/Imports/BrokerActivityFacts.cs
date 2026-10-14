using System.Collections.Immutable;

namespace CRS.Application;

/// <summary>解析后可共享的交易/资产/现金事实，不包含 FIFO 结果、应税金额或收益率。</summary>
public sealed record BrokerActivityFacts(string Broker,string SourceFileHash,string ImportBatchId,string ParserVersion,
    ImmutableArray<Trade> Trades,ImmutableArray<FundMovement> FundMovements,ImmutableArray<SecurityMovement> SecurityMovements,
    ImmutableArray<Position> BeginningPositions,ImmutableArray<Position> Positions,ImmutableArray<SourceReport> Sources,
    ImmutableArray<CashReconciliation> CashChecks,ImmutableArray<Issue> Issues)
{
    public static BrokerActivityFacts Freeze(ImportData data,string sourceFileHash,string parserVersion)=>new(
        data.Broker,sourceFileHash,sourceFileHash,parserVersion,[..data.Trades],[..data.FundMovements],[..data.SecurityMovements],
        [..data.BeginningPositions],[..data.Positions],[..data.Sources],[..data.CashChecks],[..data.Issues]);
}
