namespace CRS.Application.Abstractions;

/// <summary>提供审计摘要，隔离应用编排与本地文件和程序集的读取。</summary>
public interface ICalculationEvidence
{
    /// <summary>计算指定字节的稳定摘要。</summary>
    string Hash(byte[] bytes);
    /// <summary>计算当前 JSON 表示的摘要，沿用既有格式语义。</summary>
    string HashJson(object value);
    /// <summary>读取文件并计算摘要，不向调用方返回敏感原件。</summary>
    string FileHash(string path);
    /// <summary>取得当前基础设施程序集摘要。</summary>
    string InfrastructureAssemblyHash { get; }
}
