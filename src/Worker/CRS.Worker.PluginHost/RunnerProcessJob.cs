using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CRS.Worker.PluginHost;

/// <summary>限制单个可信插件的进程故障范围；同一服务身份不构成权限沙箱。</summary>
internal sealed class RunnerProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public RunnerProcessJob(Process process)
    {
        handle=CreateJobObject(IntPtr.Zero,null);
        if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Runner job creation failed.");
        try
        {
            var limit=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000|0x400|0x100|0x8,ActiveProcesses=1},ProcessMemory=(UIntPtr)(1024UL*1024*1024)};
            var size=Marshal.SizeOf<ExtendedLimits>();var pointer=Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limit,pointer,false);
                if(!SetInformationJobObject(handle,9,pointer,(uint)size)||!AssignProcessToJobObject(handle,process.Handle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Runner job limits could not be applied.");
                // CPU硬上限为整机额度的一半，单插件忙循环不能独占整台电脑。
                var cpu=new CpuLimit{Flags=1|4,Rate=5000};
                Marshal.StructureToPtr(cpu,pointer,false);
                if(!SetInformationJobObject(handle,15,pointer,(uint)Marshal.SizeOf<CpuLimit>()))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Runner CPU limit could not be applied.");
            }
            finally{Marshal.FreeHGlobal(pointer);}
        }
        catch{handle.Dispose();throw;}
    }
    public void Dispose()=>handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential)] private struct CpuLimit {public uint Flags,Rate;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job,int type,IntPtr value,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
}
