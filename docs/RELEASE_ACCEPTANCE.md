# 发布验收

运行 `./tests/Run-ReleaseAcceptance.ps1 -Smoke`，产物在被 Git 忽略的 `artifacts/acceptance`。构建失败、回归失败、基准金额不符或窗口失败时脚本返回错误并保存当前验收状态；清理只针对本次隔离临时目录，不删除用户数据。`-SkipBuild` 仅使用已有 Release 文件，报告明确标注构建未重新运行。

## 已提供的验收工具

- 四个生产项目及五组控制台回归、架构边界检查、可选 WPF 窗口冒烟。
- 1 万／10 万笔合成全年闭合买卖；每个文件不超过现有 25 MB 门槛。独立校验匹配笔数、收益、税额和对账完成状态后，测量导入、FIFO、对账及引用快照合计耗时。预热后每个规模三次，中位数、最大托管分配、进程累计峰值工作集和运行环境写入 `benchmark.json`。不包含 SQLite、Excel、WPF，也不包含真实复杂成本链；性能阈值尚未约定。
- 从生产项目实际 `project.assets.json` 收集所有直接与传递 NuGet 依赖的版本、还原摘要、许可证元数据和可用许可证／NOTICE 文件。清单在 `licenses/inventory.csv`；元数据及文本收集不等于分发义务审核。项目自身尚无 LICENSE，需要权利人决定许可，程序不会自动代为选择。
- [隐私说明](PRIVACY.md) 与实际本地持久化、凭证隐私和取消测试。
- 若已安装官方 `dotnet-coverage` 或 Visual Studio `Microsoft.CodeCoverage.Console`，脚本用每组实际执行及窗口冒烟收集数据，只计算四个生产程序集，并合并 `coverage.cobertura.xml`。也可用 `-CoverageTool` 指定工具路径。工具缺失时记录 `not_collected_tool_unavailable`，不能把检查数量或通过率当作覆盖率。工具用法见 [Microsoft 官方说明](https://learn.microsoft.com/en-us/dotnet/core/additional-tools/dotnet-coverage)。本次 NuGet 下载未成功，已改用本机 Visual Studio 官方工具取得实际覆盖率；隔离环境阻止初始化时，需要可用的本机进程通信环境。

## 真实全年样本

复制 [清单模板](../tests/templates/real-sample.example.json) 到本地非版本库目录，填写脱敏真实原件路径、券商、年度、配置目录、期初依据、授权说明及独立人工核对的金额／笔数／对账状态／问题代码；然后运行：

```powershell
./tests/Run-ReleaseAcceptance.ps1 -Smoke -RealManifest 'D:/本地验收/real-sample.json'
```

模板中的零值和说明是占位符，不能直接视为人工基准。相对路径以清单所在目录解析。脚本严格比较期望值，不以程序输出倒填人工基准；只保存原件文件名、摘要、金额核对结果，不复制原始报表。样本真实来源由提供方声明，程序无法自动证明。现有 fixture 和性能样本均为合成，不能列为真实全年验收。

## 尚未通过的发布门槛

真实全年样本与独立人工基准、覆盖率验收目标、性能与内存阈值、项目许可与第三方分发审查、实际发布包隐私审核仍待补齐。`acceptance.json` 始终保留 `ReleaseApproved=false`，只有权利人和业务验收方可以作最终发布判断。

本次完整 Release 构建零警告、零错误，五组回归及 WPF 冒烟通过；本机官方工具覆盖 2394／2716 个可计量生产代码行，覆盖率 88.14%。合成 1 万／10 万笔三次中位耗时为 401／3779 ms；进程累计峰值工作集约为 117／411 MiB，最大托管累计分配约为 71／704 MiB（分配量不是同时占用内存）。均为本机单次验收记录，不能代替阈值或真实场景结论。产物位于 `artifacts/coverage-acceptance`；构建与首次性能测量在 `artifacts/acceptance/build.log`、`benchmark.json`，覆盖收集使用同一批 Release 文件并复用该性能测量。

48 个依赖中有 10 个包没有明确的 NuGet 许可证声明：OpenTK.Compute、Core、GLWpfControl、Graphics、Input、Mathematics、OpenAL、Windowing.Common、Windowing.Desktop、Windowing.GraphicsLibraryFramework。不能直接把父包的 MIT 声明套用到这些包；需按对应发行版本核对上游源码许可证和原生组件声明。清单以 `license_metadata_requires_review` 标记，全部分发许可与 NOTICE 义务仍待权利人审核。
