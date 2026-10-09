using System.Collections;

namespace CRS.Application;

/// <summary>后台提供的只读底稿投影，不包含桌面控件类型。</summary>
public sealed record ReportSection(string Title, IEnumerable Rows);
