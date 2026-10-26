# CRS.Common

跨插件基础设施实现（纯 .NET BCL，`net10.0`）。用于其他插件复用**机制**，不复用业务数据、密钥或政策。

## 引用方式

在插件的后台项目中增加：

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

### 合成验证

```powershell
dotnet run --project tests/CRS.Tests.NationalTeamWatch -c Release -- --shared-read-admission
```

生产环境打包依旧由 `tests/Publish-NationalTeamPackage.ps1` 的 dotnet publish 收集依赖，发行索引必须对新增DLL逐字节哈希。不能将 `CRS.Common.dll` 手工放进未核验的全局插件目录或通过默认上下文覆盖已加载的不同版本。此仓库更新不代表安装的Worker已升级。
