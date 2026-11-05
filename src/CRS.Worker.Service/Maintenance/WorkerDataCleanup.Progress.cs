using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private void Phase(Operation operation,string phase,string message)
    {
        lock(stateGate)operation.Status=operation.Status with
        {
            Message=message,UpdatedAt=Now,
            Progress=(operation.Status.Progress??new(phase,null)) with{Phase=phase,CurrentItem=null}
        };
    }
    private void CurrentItem(Operation operation,string path)
    {
        var relative=Path.GetRelativePath(root,path);
        lock(stateGate)operation.Status=operation.Status with
        {UpdatedAt=Now,Progress=(operation.Status.Progress??new("Scanning",null)) with{CurrentItem=relative}};
    }
    private void ScannedFile(Operation operation,FileInfo file)
    {
        var relative=Path.GetRelativePath(root,file.FullName);var length=file.Length;
        lock(stateGate)
        {
            var progress=operation.Status.Progress??new("Scanning",null);
            operation.Status=operation.Status with{UpdatedAt=Now,Progress=progress with
            {CurrentItem=relative,ScannedFiles=checked(progress.ScannedFiles+1),ScannedBytes=checked(progress.ScannedBytes+length)}};
        }
    }
    private void Verified(Operation operation,int bytes,bool completed=false)
    {
        lock(stateGate)
        {
            var progress=operation.Status.Progress??new("Verifying",null);
            operation.Status=operation.Status with{UpdatedAt=Now,Progress=progress with
            {VerifiedBytes=checked(progress.VerifiedBytes+bytes),VerifiedFiles=checked(progress.VerifiedFiles+(completed?1:0))}};
        }
    }
}
