namespace CRS.Infrastructure;

/// <summary>在目标同目录暂存明文导出，完成写入且会话有效后才替换目标文件。</summary>
internal static class AtomicExport
{
    internal static void Write(string path,Action<Stream> write,CancellationToken cancellation,Action<Action>? guard=null)
    {
        cancellation.ThrowIfCancellationRequested();
        guard?.Invoke(()=>cancellation.ThrowIfCancellationRequested());
        var target=Path.GetFullPath(path);
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                // 写入过程不持有保险库门闩，锁定无需等待整份 Excel 压缩完成。
                using var output=new CancellationOutput(file,cancellation);
                write(output); cancellation.ThrowIfCancellationRequested(); file.Flush(true);
            }
            void Commit()
            {
                // 提交是唯一持锁阶段；取消或旧会话失效时保留现有目标文件。
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary,target,true);
            }
            if(guard is null) Commit(); else guard(Commit);
        }
        finally
        {
            // 仅清理本次随机命名的临时文件，不删除已有报告。
            if(File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>分块写入检查取消；包装器释放时保留底层文件供持久刷新。</summary>
    private sealed class CancellationOutput(Stream output,CancellationToken cancellation) : Stream
    {
        public override bool CanRead=>false;
        public override bool CanSeek=>output.CanSeek;
        public override bool CanWrite=>output.CanWrite;
        public override long Length=>output.Length;
        public override long Position {get=>output.Position;set=>output.Position=value;}
        public override void Flush() {cancellation.ThrowIfCancellationRequested(); output.Flush();}
        public override long Seek(long offset,SeekOrigin origin)=>output.Seek(offset,origin);
        public override void SetLength(long value) {cancellation.ThrowIfCancellationRequested(); output.SetLength(value);}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>Write(buffer.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while(!buffer.IsEmpty)
            {
                cancellation.ThrowIfCancellationRequested();
                var count=Math.Min(buffer.Length,65536); output.Write(buffer[..count]); buffer=buffer[count..];
            }
        }
    }
}
