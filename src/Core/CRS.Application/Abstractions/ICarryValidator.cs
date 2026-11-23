namespace CRS.Application.Abstractions;

/// <summary>年度结转校验端口，与文件格式实现解耦。</summary>
public interface ICarryValidator
{
    /// <summary>验证结转年度、账户范围及批次余额。</summary>
    void Validate(CarryDocument document, int year, string[] accounts);
}
