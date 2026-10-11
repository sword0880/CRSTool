using System.Collections.ObjectModel;

namespace CRS.Domain;

/// <summary>构建期间可写，发布后冻结；不让界面或后续任务修改已核算集合。</summary>
public sealed class SnapshotList<T>:Collection<T>,IReadOnlyList<T>
{
    private bool frozen;
    public SnapshotList() { }
    public SnapshotList(IEnumerable<T> values):base(values.ToList()) { }
    public static implicit operator SnapshotList<T>(List<T> values)=>new(values);
    public static implicit operator SnapshotList<T>(T[] values)=>new(values);
    public void AddRange(IEnumerable<T> values) {foreach(var value in values) Add(value);}
    public void Freeze()=>frozen=true;
    private void Check() {if(frozen) throw new CrsException("计算结果已冻结，请创建新的任务，不能修改原结果。");}
    protected override void InsertItem(int index,T item) {Check(); base.InsertItem(index,item);}
    protected override void SetItem(int index,T item) {Check(); base.SetItem(index,item);}
    protected override void RemoveItem(int index) {Check(); base.RemoveItem(index);}
    protected override void ClearItems() {Check(); base.ClearItems();}
}
