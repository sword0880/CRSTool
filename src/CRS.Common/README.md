# CRS.Common

CRS 主程序、后台 Worker 与各业务插件共同使用的基础实现库（纯 .NET BCL，`net10.0`）。只复用**机制**，不复用业务数据、密钥或政策。

## 引用方式

例如从 `src/Plugins/<Plugin>/` 下的插件后台项目引用公共项目：

```xml
<ProjectReference Include="../../CRS.Common/CRS.Common.csproj" />
```

示例：每个插件或证据仓储单独创建自己的实例：

```csharp
using CRS.Common.Concurrency;
using CRS.Common.Integrity;

// Per store; do not make static or share across unrelated plugins.
var reads = new PriorityReadAdmission(capacity: 4, maximumQueryBurst: 8);
using (await reads.EnterAsync(priority: true, cancellationToken))
{
    // Stream and verify the caller's private data in the caller's own storage.
}
string identity = ContentFingerprint.Sha256(payload);
if (!ContentFingerprint.Matches(identity, payload))
    throw new InvalidDataException("Content changed.");
```

- `PriorityReadAdmission`：有界异步读取租约、查询优先与后台防饥饿、取消、纯数量指标。调用方必须 Dispose 租约；实例隔离，不使用静态单例。
- `ContentFingerprint`：标准 SHA256 内容身份生成与校验。不证明来源许可、原件可知时点或财务可靠性。
- `CRS.Common` 不允许引用 `CRS.Plugins.*`、`CRS.Worker.*`、`CRS.Infrastructure`、WPF、Sqlite 或税务业务。
- 已有宿主级来源共享预算在 `CRS.Worker.Abstractions.IBackgroundSourceContext`，通知发送在 `CRS.Notifications`；不要在这里重复造全局来源限速或通知引擎。
- `RawEvidenceStore` 的发行人白名单、8MiB/受审16MiB等单源大小规则、SQLite证据生命周期及Q15审计仍属于 `NationalTeam`；后续分离文件原子写入时只能共享原语，必须由业务插件注入其限制与授权。

## 时间处理已经归并

`BeijingClock` 和 `BeijingDateTimeOffsetConverter` 的实现已迁到 `CRS.Common/Time/`，两个公开类型原有的 `CRS.Timing` 命名空间不变。其他 CRS 项目可以通过 `CRS.Common` 项目引用直接使用：

```csharp
using CRS.Timing;

var now = BeijingClock.Read(TimeProvider.System);  // 同一UTC时间点，以 +08:00 显示
var reportDay = BeijingClock.Normalize(now).Date;
```

**兼容升级规则：** `CRS.Time.dll` 暂时保留原始程序集身份与 `TypeForwardedTo`，旧版客户端或插件仍能解析先前编译的 `CRS.Timing` 类型。发行包需要同时带上 `CRS.Time.dll`（兼容层）和 `CRS.Common.dll`（真正实现）；只拷贝其中一个不符合本次兼容方案。Window Service Runner 与插件可信包需要重新建立 SHA256 索引；不能混用不同提交的 DLL。

### 合成验证

```powershell
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release -- --shared-read-admission
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release -- --common-time-compat
```

生产环境打包依旧由 `tests/Publish-NationalTeamPackage.ps1` 的 dotnet publish 收集依赖，发行索引必须对新增DLL逐字节哈希。不能将 `CRS.Common.dll` 手工放进未核验的全局插件目录或通过默认上下文覆盖已加载的不同版本。此仓库更新不代表安装的Worker已升级。
