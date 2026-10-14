using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain.Performance;
using CRS.Security.Cryptography;
using NPOI.XSSF.UserModel;

namespace CRS.Infrastructure;

/// <summary>原件一次读入并归档；普通与加密绩效报表均通过统一敏感导出门槛。</summary>
internal sealed class PerformanceFiles(Action<Action> guard,SensitiveDataExportService exports) : IPerformanceFiles
{
    public PerformanceDocument Read(string path,string broker,CancellationToken cancellation)
        =>Read(FrozenInput.Read(path,cancellation),broker,cancellation);
    internal PerformanceDocument Read(FrozenInput input,string broker,CancellationToken cancellation)
    {
        var raw=input.CopyBytes();PerformanceData data;var ext=Path.GetExtension(input.Name).ToLowerInvariant();
        if(ext==".json")data=Stamp(PerformanceCodec.Decode<PerformanceData>(raw),input.Sha256,true);
        else if(ext==".xml" && broker=="IBKR")data=PerformanceBrokerImporter.Ibkr(input,cancellation);
        else if(ext==".xlsx" && broker=="FUTU")data=PerformanceBrokerImporter.Futu(input,cancellation);
        else throw new CrsException("绩效导入支持模板 JSON、IBKR Activity Flex XML 和富途年度交易 XLSX；不假定任意文件包含 NAV。");
        Check(data);return new(input.Sha256,input.Name,data,raw,ext);
    }
    public PerformanceDocument ReadManual(string json)
    {
        if(json.Length>25*1024*1024)throw new CrsException("手工资料超过导入限额。");var raw=Encoding.UTF8.GetBytes(json);
        if(raw.Length is <=0 or >25*1024*1024)throw new CrsException("手工资料为空或超过导入限额。");var hash=PerformanceCodec.Hash(raw);
        var data=Stamp(PerformanceCodec.Decode<PerformanceData>(raw),hash,true);Check(data);return new(hash,"手工补录",data,raw,"ManualJSON");
    }
    private static PerformanceData Stamp(PerformanceData data,string hash,bool manual)
    {
        PerformanceSource Source(PerformanceSource source)=>new(hash,source.RowIdentity);
        PerformanceQuality Quality(PerformanceQuality q)=>manual && q<PerformanceQuality.Estimated?PerformanceQuality.Estimated:q;
        // 手工/离线转换 JSON 保留用户录入身份，不接受客户端自行提升为券商已核对。
        return data with {Events=data.Events.Select(e=>e with {Source=Source(e.Source),Quality=Quality(e.Quality)}).ToArray(),
            Valuations=data.Valuations.Select(v=>v with {Source=Source(v.Source),Quality=Quality(v.Quality)}).ToArray(),
            Prices=data.Prices.Select(p=>p with {Source=Source(p.Source),Quality=Quality(p.Quality)}).ToArray(),
            Fx=data.Fx.Select(f=>f with {Source=Source(f.Source),Quality=Quality(f.Quality)}).ToArray(),
            Coverage=data.Coverage.Select(c=>c with {Source=Source(c.Source)}).ToArray()};
    }
    private static void Check(PerformanceData data)
    {
        if(data.Accounts.Length==0 && data.Events.Length==0 && data.Valuations.Length==0 && data.Prices.Length==0 && data.Fx.Length==0 && data.Coverage.Length==0)throw new CrsException("绩效导入没有可用资料。");
        if(data.Revisions.Length!=0 || data.Transfers.Length!=0)throw new CrsException("分类修订和转账确认须走检查台，不从文件直接发布授权。");
        // 价格/汇率独立补录也要校验，不能因没有账户数组跳过负汇率检查。
        var validation=data.Accounts.Length>0?data:data with{Accounts=[new("validation","MANUAL","格式校验","CNY",new(2000,1,1))]};
        _=PerformanceCalculator.Calculate(validation,new(validation.Accounts.Select(a=>a.Id).Distinct().ToArray(),new(2000,1,1),new(2000,1,2),"CNY"));
        // 允许只补充价格/NAV，但禁止空来源行号及字符串超限。
        var sources=data.Events.Select(e=>e.Source).Concat(data.Valuations.Select(v=>v.Source)).Concat(data.Prices.Select(p=>p.Source)).Concat(data.Fx.Select(f=>f.Source)).Concat(data.Coverage.Select(c=>c.Source));
        if(data.Version!=1 || sources.Any(s=>s is null || string.IsNullOrWhiteSpace(s.RowIdentity) || s.RowIdentity.Length>256))throw new CrsException("绩效格式版本或来源行身份无效。");
        _=PerformanceCodec.Encode(data);
    }
    public string Hash(PerformanceData data)=>PerformanceCodec.Hash(PerformanceCodec.Encode(PerformanceCodec.Ordered(data)));
    public void Export(PerformanceRun run,string path,bool encrypted,string password,SensitiveExportConsent? consent,CancellationToken cancellation)
        =>exports.ExportPerformanceAsync(path,encrypted,cancellation,consent,token=>Write(run,path,encrypted,password,token)).GetAwaiter().GetResult();
    private void Write(PerformanceRun run,string path,bool encrypted,string password,CancellationToken cancellation)
    {
        // 在构建工作簿前限制行数，防止第三方对象模型先占满内存才发现密文超限。
        var rowCount=(long)run.Result.Metrics.Length+run.Result.Accounts.Length+run.Result.Flows.Length+run.Result.Issues.Length+run.Result.Reconciliations.Length+run.Result.Exposure.Length+run.Input.Fx.Length;
        if(rowCount>50000)throw new CrsException("绩效报告超过 50,000 行导出限额，请缩小账户或期间范围。");
        if(encrypted)try{PasswordEncryption.ValidatePassword(password);}catch(CryptographicException){throw new CrsException("加密密码需为 12—1024 个字符。");}
        using var book=new XSSFWorkbook();
        void Sheet(string name,string[] headers,IEnumerable<object?[]> rows)
        {
            var sheet=book.CreateSheet(name);int index=0;var header=sheet.CreateRow(index++);for(var i=0;i<headers.Length;i++)header.CreateCell(i).SetCellValue(headers[i]);
            foreach(var values in rows)
            {
                cancellation.ThrowIfCancellationRequested();var row=sheet.CreateRow(index++);
                for(var i=0;i<values.Length;i++) {var cell=row.CreateCell(i);if(values[i] is decimal amount)cell.SetCellValue((double)amount);else cell.SetCellValue(values[i]?.ToString()??"");}
            }
            // 外部文本始终写字符串，不调用 SetCellFormula；账户键及原账号不进入展示表。
            sheet.CreateFreezePane(0,1);for(var i=0;i<headers.Length;i++)sheet.SetColumnWidth(i,Math.Min(18000,i==headers.Length-1?14000:6000));
        }
        Sheet("绩效指标",["指标","数值","单位","质量","定义与依据"],run.Result.Metrics.Select(m=>new object?[]{m.Name,m.Value,m.Unit,m.Quality,m.Explanation}));
        Sheet("账户汇总",["账户别名","券商","期初NAV","期末NAV","现金","负债","外部净投入","投资损益","币种"],run.Result.Accounts.Select(a=>new object?[]{a.AccountAlias,a.Broker,a.OpeningNav,a.EndingNav,a.Cash,a.Liabilities,a.NetInvestment,a.Profit,a.Currency}));
        Sheet("流量边界",["账户别名","日期","金额","内部消除","说明"],run.Result.Flows.Select(f=>new object?[]{f.AccountAlias,f.Date.ToString("yyyy-MM-dd"),f.Amount,f.Internal,f.Explanation}));
        Sheet("异常与来源",["问题","账户别名","来源摘要","原始行","原因","修复建议"],run.Result.Issues.Select(i=>new object?[]{i.Code,i.AccountAlias,i.SourceDocument,i.SourceRow,i.Explanation,i.Action}));
        Sheet("净值核对",["账户别名","日期","组成NAV","结单NAV","差额","容差","一致"],run.Result.Reconciliations.Select(r=>new object?[]{r.AccountAlias,r.Date.ToString("yyyy-MM-dd"),r.CalculatedNav,r.StatementNav,r.Difference,r.Tolerance,r.Matched}));
        Sheet("原币资产组成",["账户别名","日期","资产类别","证券标识","市场","数量","原币","原币金额","报告币金额","来源摘要","原始行"],run.Result.Exposure.Select(r=>new object?[]{r.AccountAlias,r.Date.ToString("yyyy-MM-dd"),r.Kind,r.Security,r.Market,r.Quantity,r.Currency,r.NativeAmount,r.ReportAmount,r.SourceDocument,r.SourceRow}));
        Sheet("历史汇率依据",["日期","原币","目标币","汇率","质量","来源摘要","原始行"],run.Input.Fx.Select(f=>new object?[]{f.Date.ToString("yyyy-MM-dd"),f.From,f.To,f.Rate,f.Quality,f.Source.DocumentId,f.Source.RowIdentity}));
        Sheet("版本依据",["运行编号","输入摘要","引擎版本","成本口径","可信起点"],[new object?[]{run.Id,run.InputHash,run.Result.EngineVersion,run.Result.CostMethodVersion,run.Result.EffectiveFrom.ToString("yyyy-MM-dd")}]);
        if(!encrypted) {AtomicExport.Write(path,stream=>book.Write(stream,true),cancellation,guard);return;}
        byte[]? plain=null;byte[]? excel=null;
        try
        {
            using(var stream=new BoundedMemory(cancellation)){book.Write(stream,true);excel=stream.ToArray();}
            var evidence=PerformanceCodec.Encode(new{run.Id,run.ParentId,run.CreatedAt,run.InputHash,run.Result.EngineVersion,run.Result.CostMethodVersion,run.Result.Request,Sources=run.Input.Events.Select(e=>e.Source.DocumentId).Concat(run.Input.Valuations.Select(v=>v.Source.DocumentId)).Distinct().Order().ToArray()});
            plain=JsonSerializer.SerializeToUtf8Bytes(new {Version=1,Files=new[]{new{Name="report.xlsx",Content=excel},new{Name="evidence.json",Content=evidence}}});
            var payload=PasswordEncryption.Seal(plain,password);cancellation.ThrowIfCancellationRequested();
            // 沿用已有加密底稿容器，报表页的“恢复加密底稿”可明确确认后解密到新目录。
            AtomicExport.Write(path,stream=>JsonSerializer.Serialize(stream,new{Format="CRS.EncryptedExport.v1",Payload=payload}),cancellation,guard);
        }
        catch(CryptographicException){throw new CrsException("加密密码需为 12—1024 个字符，或加密操作失败。");}
        finally {if(plain is not null)CryptographicOperations.ZeroMemory(plain);if(excel is not null)CryptographicOperations.ZeroMemory(excel);}
    }
    private sealed class BoundedMemory(CancellationToken cancellation):MemoryStream
    {
        private void Check(long length){cancellation.ThrowIfCancellationRequested();if(length>32*1024*1024)throw new CrsException("绩效报表超过 32 MiB 内存导出限额。");}
        public override void Write(byte[] buffer,int offset,int count){Check(Position+count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(Position+buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Check(Position+1);base.WriteByte(value);}
        public override void SetLength(long value){Check(value);base.SetLength(value);}
        // 清理自己持有的缓冲，避免关闭流后还有一份可访问的工作簿明文副本。
        protected override void Dispose(bool disposing){if(disposing && TryGetBuffer(out var segment) && segment.Array is { } bytes)CryptographicOperations.ZeroMemory(bytes);base.Dispose(disposing);}
    }
}
