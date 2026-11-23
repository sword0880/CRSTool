namespace CRS.Plugins.NationalTeamWatch;

/// <summary>单页实际等待与解码计数；ItemsBytes是逻辑行JSON字节，不冒充管道封包字节或金融覆盖率。</summary>
public sealed record EtfQueryPageMeasurement(string Operation,int Offset,int RequestedRows,int ReceivedRows,int ItemsBytes,long ElapsedMs);
