using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>审计摘要基础设施，集中处理文件和程序集读取。</summary>
public sealed class FileCalculationEvidence : ICalculationEvidence
{
    /// <summary>计算字节摘要，复用既有 SHA256 格式。</summary>
    public string Hash(byte[] bytes) => XmlFields.Hash(bytes);
    /// <summary>计算当前 JSON 摘要，保持历史序列化口径。</summary>
    public string HashJson(object value) => XmlFields.HashJson(value);
    /// <summary>读取本地文件并计算摘要。</summary>
    public string FileHash(string path) => Hash(File.ReadAllBytes(path));
    /// <summary>获取当前基础设施构建摘要，避免应用层识别实现程序集。</summary>
    public string InfrastructureAssemblyHash => FileHash(typeof(FileCalculationEvidence).Assembly.Location);
}
