using System.Diagnostics;
using System.IO;
using System.Reflection;
using CRS.DesktopClient.Services;

/// <summary>独立进程验证真实指纹、被替换／缺失／抢先加载拒绝，以及校验后不能覆盖组件。</summary>
internal static class DocumentAssemblyTrustChecks
{
    public static int Probe(string path,bool preload)
    {
        try
        {
            if(preload)System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            TrustedDocumentAssembly.Load(path);TrustedDocumentAssembly.Load(path);
            try {using var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);return 3;}
            catch(IOException) { }
            try {File.Move(path,path+".replaced");return 3;}
            catch(IOException) {return 0;}
        }
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException) {return 2;}
    }
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS-Document-Trust-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        // 默认加载上下文按 deps 清单定位程序集；正向场景验证实际运行目录中的组件。
        var good=Path.Combine(AppContext.BaseDirectory,"CRS.Documents.dll");
        await Child(good,false,0);
        await Child(good,true,2);
        var altered=Path.Combine(root,"altered.dll");var data=File.ReadAllBytes(good);data[^1]^=1;File.WriteAllBytes(altered,data);
        await Child(altered,false,2);await Child(Path.Combine(root,"missing.dll"),false,2);
        Console.WriteLine("文档防替换通过：真实构建指纹、改动／缺失／未经验证预加载拒绝、固定句柄阻止会话中覆盖。");
    }
    private static async Task Child(string path,bool preload,int expected)
    {
        var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--document-trust-probe");
        start.ArgumentList.Add(path);start.ArgumentList.Add(preload?"preload":"normal");
        using var process=Process.Start(start)??throw new Exception("无法启动组件校验子进程。");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try {await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
        catch {if(!process.HasExited)process.Kill(true);throw;}
        await stdout;var failure=await stderr;
        if(process.ExitCode!=expected)throw new Exception($"组件防替换子进程失败：预期 {expected}，实际 {process.ExitCode}。{failure}");
    }
}
