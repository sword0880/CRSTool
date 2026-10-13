using CRS.Application;
using CRS.Domain;

internal static class SensitiveExportPolicyChecks
{
    /// <summary>直接调用后台也必须确认明文，不能靠按钮禁用或默认参数授权。</summary>
    internal static async Task RunAsync()
    {
        var operations=new StubOperations(); var workflow=new DesktopWorkflow(operations);
        var result=new CalculationResult {Year=2025,SnapshotId="SYNTHETIC",Summary=new(0,0,0,0,0,0,0),CarryEligible=true};
        await Reject(workflow.ExportAsync("unused",result,""));
        await Reject(workflow.ExportCarryAsync("unused",result));
        await Reject(workflow.RestoreEncryptedAsync("unused","unused","unused"));
        await Reject(workflow.ExportAsync("unused",result,"",consent:new(false)));
        if(operations.Export.Count!=0 || operations.CarryCount!=0 || operations.ExportEvents.Any(e=>e.Outcome==SensitiveExportOutcome.Started))
            throw new Exception("未确认明文就调用写入适配器或记录开始。");
        operations.ValidateDestination=(_,plaintext,temporary)=>
        {if(!plaintext || temporary) throw new Exception("普通导出错误传递目录策略。");};
        await workflow.ExportAsync("unused",result,"",consent:new(true));
        if(operations.Export.Count!=1 || !operations.ExportEvents.Contains((SensitiveExportOperation.Excel,SensitiveExportOutcome.Completed)))
            throw new Exception("确认后正常导出或固定事件丢失。");
        operations.ValidateDestination=(_,_,_)=>throw new CrsException("合成目的地拒绝");
        await Reject(workflow.ExportCarryAsync("unused",result,consent:new(true)));
        if(operations.CarryCount!=0) throw new Exception("目录策略失败后仍写入结转。");
        workflow.EndSession();
        try {await workflow.ExportAsync("unused",result,"",consent:new(true)); throw new Exception("明文确认绕过会话锁定。");}
        catch(OperationCanceledException) { }
        Console.WriteLine("敏感导出策略通过：后台默认拒绝、单次确认、目录校验前置、固定事件及会话锁定。");
    }
    private static async Task Reject(Task task)
    {try {await task;} catch(CrsException) {return;} throw new Exception("未授权敏感导出未拒绝。");}
}
