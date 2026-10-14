namespace CRS.Domain.Performance;

public sealed record XirrResult(decimal? Rate,string Explanation);
/// <summary>按实际日期/365 折现；在 log(1+r) 空间有界求根，不能把不确定根显示为零。</summary>
public static class XirrSolver
{
    public static XirrResult Solve(IEnumerable<(DateOnly Date,decimal Amount)> input,CancellationToken cancellation=default)
    {
        var rows=input.GroupBy(x=>x.Date).Select(g=>(Date:g.Key,Amount:g.Sum(x=>x.Amount))).Where(x=>x.Amount!=0).OrderBy(x=>x.Date).ToArray();
        if(rows.Length<2 || rows[0].Date==rows[^1].Date || !rows.Any(x=>x.Amount<0) || !rows.Any(x=>x.Amount>0)) return new(null,"现金流日期不足或缺少正、负两类流量。");
        if(rows.Length>20000 || rows.Any(x=>Math.Abs(x.Amount)>1_000_000_000_000_000_000m)) return new(null,"现金流超过数值求解限额。");
        var scale=(double)rows.Sum(x=>Math.Abs(x.Amount)); var first=rows[0].Date.DayNumber;
        var terms=rows.Select(x=>(T:(x.Date.DayNumber-first)/365d,A:(double)x.Amount/scale)).ToArray();
        double Npv(double x)
        {
            // 缩放指数避免溢出；只用于求根，不改变零点。
            var max=terms.Max(t=>-t.T*x); double sum=0,correction=0;
            foreach(var t in terms) {var y=t.A*Math.Exp(-t.T*x-max)-correction; var next=sum+y; correction=(next-sum)-y; sum=next;}
            return sum;
        }
        var changes=rows.Zip(rows.Skip(1),(a,b)=>Math.Sign(a.Amount)!=Math.Sign(b.Amount)?1:0).Sum();
        if(changes>1)
        {
            // 多次变号时不宣称有限扫描能够证明唯一；发现多根或无法保证唯一均拒绝任意选根。
            var roots=0; var previous=Npv(-32);
            for(var i=1;i<=4096;i++) {if(i%32==0)cancellation.ThrowIfCancellationRequested(); var next=Npv(-32+i/64d); if(previous*next<0) roots++; previous=next;}
            return new(null,roots>1?"发现多个有效 XIRR 根，不能确定唯一年化收益率。":"现金流多次变号，无法保证 XIRR 根唯一；请核对流量或缩小区间。");
        }
        double low=-32,high=32,fl=Npv(low),fh=Npv(high);
        if(fl==0 || fh==0 || fl*fh>=0) return new(null,"在支持的年化收益率范围内没有可靠数值根。");
        for(var i=0;i<256;i++)
        {
            cancellation.ThrowIfCancellationRequested(); var mid=(low+high)/2; var fm=Npv(mid);
            if(fm==0 || high-low<1e-12)
            {
                var rate=Math.Exp(mid)-1;
                if(!double.IsFinite(rate) || Math.Abs(fm)>1e-9 || rate<=-1) return new(null,"XIRR 残差或精度未达到门槛。");
                return new((decimal)rate,"资金加权年化：实际天数/365，有界二分与残差校验通过。");
            }
            if(fl*fm<0) high=mid; else {low=mid; fl=fm;}
        }
        return new(null,"XIRR 达到迭代上限仍未收敛。");
    }
}
