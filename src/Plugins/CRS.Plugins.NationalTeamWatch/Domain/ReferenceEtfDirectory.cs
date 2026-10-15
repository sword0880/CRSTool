namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>公开参考项目的标的事实快照，仅作为待核验候选，不作为机构持仓证据或自动监控清单。</summary>
public static class ReferenceEtfDirectory
{
    public const string Source="https://github.com/wwww1998/national-team-etf-flow/blob/main/refresh.py";
    // 2026-10-15核对参考项目23个代码；名称为参考简称，需用户按官方资料核验，可在配置页修改。
    public static IReadOnlyList<(string Market,string Ticker,string Name,string Index)> Entries {get;}=[
        ("SSE","510300","华泰柏瑞沪深300ETF","CSI300"),("SSE","510310","易方达沪深300ETF","CSI300"),
        ("SSE","510330","华夏沪深300ETF","CSI300"),("SZSE","159919","嘉实沪深300ETF","CSI300"),
        ("SSE","510050","华夏上证50ETF","SSE50"),("SSE","510100","易方达上证50ETF","SSE50"),
        ("SSE","510500","南方中证500ETF","CSI500"),("SSE","512500","华夏中证500ETF","CSI500"),
        ("SZSE","159922","嘉实中证500ETF","CSI500"),("SSE","512100","南方中证1000ETF","CSI1000"),
        ("SZSE","159845","华夏中证1000ETF","CSI1000"),("SSE","560010","广发中证1000ETF","CSI1000"),
        ("SZSE","159629","富国中证1000ETF","CSI1000"),("SZSE","159915","易方达创业板ETF","ChiNext"),
        ("SZSE","159952","广发创业板ETF","ChiNext"),("SZSE","159977","天弘创业板ETF","ChiNext"),
        ("SSE","588080","易方达科创板50ETF","STAR50"),("SSE","588050","工银科创板50ETF","STAR50"),
        ("SSE","510180","华安上证180ETF","SSE180"),("SSE","510230","国泰上证180金融ETF","SSE180Finance"),
        ("SZSE","159901","易方达深证100ETF","SZSE100"),("SSE","515800","汇添富中证800ETF","CSI800"),
        ("SSE","560050","汇添富MSCI中国A50ETF","MSCIChinaA50")];
}
