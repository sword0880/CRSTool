using CRS.Application.Abstractions;

namespace CRS.Application;

/// <summary>单次明文授权声明；它不替代保险库会话认证，也不保存到配置。</summary>
public sealed record SensitiveExportConsent(bool PlaintextConfirmed,bool TemporaryLocationConfirmed=false);
public enum SensitiveExportOperation {Excel,Carry,EncryptedArchive,RestoreArchive,PerformanceReport}
public enum SensitiveExportOutcome {Started,Completed,Cancelled,Failed}

/// <summary>统一导出门槛、目标验证、会话取消和无敏感字段审计，不实现文件或密码学操作。</summary>
public sealed class SensitiveDataExportService(IDesktopOperations operations,CancellationToken session)
{
    // 绩效与税务输出共用一次明文确认、临时路径拒绝、会话取消及固定类别审计。
    public Task ExportPerformanceAsync(string path,bool encrypted,CancellationToken cancellation,SensitiveExportConsent? consent,Action<CancellationToken> write)
        =>Run(encrypted?SensitiveExportOperation.EncryptedArchive:SensitiveExportOperation.PerformanceReport,path,consent,cancellation,token=>{write(token);return true;});
    public Task ExportAsync(string path,CalculationResult result,string fingerprint,CancellationToken cancellation,SensitiveExportConsent? consent)
        =>Run(SensitiveExportOperation.Excel,path,consent,cancellation,token=>
        {CheckRates(fingerprint); operations.Reports.Export(path,result,operations.Repository.Reviews(result.SnapshotId),token); return true;});
    public Task ExportCarryAsync(string path,CalculationResult result,CancellationToken cancellation,SensitiveExportConsent? consent)
        =>Run(SensitiveExportOperation.Carry,path,consent,cancellation,token=>
        {if(!result.CarryEligible) throw new CrsException("年末成本尚未核实，不能导出结转。"); operations.SaveCarry(path,result,token); return true;});
    public Task ExportEncryptedAsync(string path,CalculationResult result,string fingerprint,string password,CancellationToken cancellation)
        =>Run(SensitiveExportOperation.EncryptedArchive,path,null,cancellation,token=>
        {CheckRates(fingerprint); operations.Reports.ExportEncrypted(path,result,password,operations.Repository.Reviews(result.SnapshotId),token); return true;});
    public Task<string> RestoreEncryptedAsync(string path,string parent,string password,CancellationToken cancellation,SensitiveExportConsent? consent)
        =>Run(SensitiveExportOperation.RestoreArchive,parent,consent,cancellation,token=>operations.Reports.RestoreEncrypted(path,parent,password,token));
    private void CheckRates(string fingerprint)
    {if(fingerprint.Length>0 && operations.LoadRates().Fingerprint!=fingerprint) throw new CrsException("汇率配置已更改，请重新计算后导出。");}
    private Task<T> Run<T>(SensitiveExportOperation operation,string destination,SensitiveExportConsent? consent,CancellationToken cancellation,Func<CancellationToken,T> action)
        =>Task.Run(()=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,session);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // 所有后台明文入口都要求本次确认，不能只依靠界面弹窗。
                var plaintext=operation!=SensitiveExportOperation.EncryptedArchive;
                if(plaintext && consent?.PlaintextConfirmed!=true) throw new CrsException("请明确确认明文文件不受数据库加密保护，或选择加密归档导出。");
                operations.ValidateExportDestination(destination,plaintext,consent?.TemporaryLocationConfirmed==true);
                linked.Token.ThrowIfCancellationRequested();
                operations.LogExportEvent(operation,SensitiveExportOutcome.Started);
                var result=action(linked.Token);
                operations.LogExportEvent(operation,SensitiveExportOutcome.Completed); return result;
            }
            catch(OperationCanceledException) {operations.LogExportEvent(operation,SensitiveExportOutcome.Cancelled); throw;}
            catch(Exception) when(linked.IsCancellationRequested)
            {operations.LogExportEvent(operation,SensitiveExportOutcome.Cancelled); throw new OperationCanceledException(linked.Token);}
            catch {operations.LogExportEvent(operation,SensitiveExportOutcome.Failed); throw;}
        });
}
