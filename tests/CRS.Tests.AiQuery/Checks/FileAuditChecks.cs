using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Infrastructure;
using CRS.AI.Query;
using CRS.AI.Adapters;
using CRS.Tests.AiQuery.Fixtures;

namespace CRS.Tests.AiQuery.Checks;

internal static class FileAuditChecks
{
    public static async Task RunAsync(Action<bool, string> Check, AiCaller caller, AiCall call, string coverageArgs)
    {
        // 使用实际本地文件验证审计写入路径，不依赖外部服务、正式 Worker 或真实账户数据。
        var auditTestDirectory = Path.Combine(Path.GetTempPath(), "CRS-AI-Audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditTestDirectory);
        try
        {
            var auditFile = Path.Combine(auditTestDirectory, "events.jsonl");
            await File.WriteAllTextAsync(auditFile, "");
            var fileWorker = new FakeWorker();
            var fileGate = AiReadGatewayFactory.Create(fileWorker,
                new AiAppendOnlyFileAuditSink(auditFile, maxFileBytes: 4096));
            var fileResult = await new McpReadToolAdapter(fileGate).CallToolAsync(caller, call, default);
            var lines = await File.ReadAllLinesAsync(auditFile);
            Check(fileResult.Status == "Partial" && fileWorker.Calls == 1 && lines.Length == 2,
                "预配置本地审计文件成功保存请求和结果两个阶段");
            using (var first = JsonDocument.Parse(lines[0]))
            using (var second = JsonDocument.Parse(lines[1]))
            {
                Check(first.RootElement.GetProperty("Phase").GetString() == "Attempt" &&
                      second.RootElement.GetProperty("Phase").GetString() == "Outcome" &&
                      first.RootElement.GetProperty("RequestId").GetGuid() ==
                      second.RootElement.GetProperty("RequestId").GetGuid(),
                    "审计记录是可解析 JSONL 且两阶段关联同一请求标识");
            }
            var rawAudit = await File.ReadAllTextAsync(auditFile);
            Check(!rawAudit.Contains(caller.Subject, StringComparison.Ordinal) &&
                  !rawAudit.Contains("AccountNumber", StringComparison.Ordinal) &&
                  !rawAudit.Contains(coverageArgs, StringComparison.Ordinal),
                "落盘审计不包含主体明文、账户字段或原始查询参数");

            // 即使有人直接调用存储实现，也不能把主体明文冒充指纹写入审计文件。
            var originalLength = new FileInfo(auditFile).Length;
            var invalidEventRejected = false;
            try
            {
                await new AiAppendOnlyFileAuditSink(auditFile).WriteAsync(
                    new AiReadAuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow,
                        caller.Subject, AiCatalog.Overview, "Attempt", "Received", "invalid"), default);
            }
            catch (InvalidDataException)
            {
                invalidEventRejected = true;
            }
            Check(invalidEventRejected && new FileInfo(auditFile).Length == originalLength,
                "本地审计写入器拒绝非 SHA-256 主体字段且不落盘");

            // 审计文件缺失时不允许自动创建，也不能继续请求业务 Worker。
            var missingFile = Path.Combine(auditTestDirectory, "not-provisioned.jsonl");
            var missingWorker = new FakeWorker();
            var missingGate = AiReadGatewayFactory.Create(missingWorker,
                new AiAppendOnlyFileAuditSink(missingFile));
            Check((await missingGate.ExecuteAsync(caller, call, default)).Code == "AuditUnavailable" &&
                  missingWorker.Calls == 0 && !File.Exists(missingFile),
                "审计路径未预配置时关闭业务读取且不自动建立文件");

            // 模拟异常断电后的半条日志，禁止从残缺尾部继续追加。
            await File.WriteAllTextAsync(auditFile, "{incomplete");
            var corruptWorker = new FakeWorker();
            var corruptGate = AiReadGatewayFactory.Create(corruptWorker,
                new AiAppendOnlyFileAuditSink(auditFile));
            Check((await corruptGate.ExecuteAsync(caller, call, default)).Code == "AuditUnavailable" &&
                  corruptWorker.Calls == 0,
                "审计文件尾部不完整时失败关闭");

            // 日志到达空间上限也不应自动截断、覆盖或静默丢失审计。
            await File.WriteAllTextAsync(auditFile, new string('x', 4095) + "\n");
            var fullWorker = new FakeWorker();
            var fullGate = AiReadGatewayFactory.Create(fullWorker,
                new AiAppendOnlyFileAuditSink(auditFile, maxFileBytes: 4096));
            Check((await fullGate.ExecuteAsync(caller, call, default)).Code == "AuditUnavailable" &&
                  fullWorker.Calls == 0 && new FileInfo(auditFile).Length == 4096,
                "审计容量用尽时拒绝查询且不覆盖已有日志");

            // 模拟 Worker 查询期间审计文件丢失，确保结果不会绕过完成审计流出。
            await File.WriteAllTextAsync(auditFile, "");
            var lostFileWorker = new FakeWorker
            {
                Handler = (_, _, _) =>
                {
                    File.Delete(auditFile);
                    return Task.FromResult(FakeWorker.Page("[]"));
                }
            };
            var lostFileGate = AiReadGatewayFactory.Create(lostFileWorker,
                new AiAppendOnlyFileAuditSink(auditFile));
            var lostFileResult = await lostFileGate.ExecuteAsync(caller, call, default);
            Check(lostFileResult.Code == "AuditUnavailable" && lostFileResult.Data is null &&
                  lostFileWorker.Calls == 1,
                "结果审计文件丢失后不释放 Worker 已返回的数据");
        }
        finally
        {
            Directory.Delete(auditTestDirectory, recursive: true);
        }
    }
}
