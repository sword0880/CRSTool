using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>适配既有 C# 结转校验策略，向应用层隐藏文件实现。</summary>
public sealed class CarryValidator : ICarryValidator
{
    /// <summary>核对结转年份、账户及批次约束，保持原有校验行为。</summary>
    public void Validate(CarryDocument document, int year, string[] accounts) => CarryFiles.Validate(document, year, accounts);
}
