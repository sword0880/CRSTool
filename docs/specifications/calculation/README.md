# 计算证据与确定性复算

[计算证据与确定性复算规范](CALCULATION_EVIDENCE_SPEC.md) 是跨税务与绩效模块的证据契约，定义单次输入冻结、原件摘要、规范化版本、计算程序集身份、规则/汇率版本、冻结输出、历史查看与复算资格。

## 职责边界

- **本规范负责**：输入一致性、证据封套、不可变运行、来源追踪、输出摘要、复算条件和导出证据边界。
- **税务法律适用性**：见 [税务规则验证规范](../tax/TAX_RULE_VALIDATION.md)。
- **当前税额与 FIFO 算法**：见 [税额引擎实现](../../implementation/TAX_ENGINE_SPEC.md) 和 [FIFO 实现约定](../../implementation/CAPITAL_GAIN_FIFO_SPEC.md)。
- **绩效计算与数据事实**：见 [绩效实现记录](../../implementation/PERFORMANCE_ANALYTICS.md) 和 [绩效需求规范](../PERFORMANCE_ANALYTICS_SPEC.md)。
- **数据加密、密钥与认证**：见 [安全设计](../security/SECURITY_DESIGN.md)。
- **发布资格**：见 [发布计划](../RELEASE_PLAN.md) 和 [发布验收](../../validation/RELEASE_ACCEPTANCE.md)。

本规范不改变税务/FIFO 算法，不定义税法结论，不替代独立真实金额基准，也不代表所有证据字段均已进入源码。
