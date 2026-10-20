# 券商不可变来源事实（ARCH-208）

本轮先收敛IBKR来源读取；税务和绩效继续拥有各自投影、计算与验收，不把Tax/FIFO结果作为Performance输入。

## 来源所有权

Application定义`BrokerSourceDocument`及`BrokerSourceRecord`：券商、原件SHA256、批次ID、来源读取版本、不可变属性/子记录/叶文本和原件内位置。使用ImmutableArray和ImmutableSortedDictionary，不持有可变XML节点；未知区段、字段和叶文本保留，完整原件继续按SHA256留存。该模型不替代原始XML的完整归档，不因当前投影不支持而丢弃原件。

Infrastructure的`IbkrSourceReader`是Activity Flex正文唯一结构读取入口，版本`IBKR.SourceFacts.v1`。继续使用既有安全XML读取器（禁DTD/外部实体、原件25MiB上限），归一元素命名空间并冻结；转换期响应取消，增加64层深度、2048字符位置及250000节点上限，超限整份拒绝，不返回半份事实。

位置格式为原件根及按原始子节点顺序定位的路径，结合原件SHA256唯一定位；不是跨版本经济交易ID。属性保留原值，数字/日期的既有校验仍由共享字段工具和各投影完成，不把未知金额补成零。

## 调用路径

1. `FrozenInput`冻结原件字节与SHA256。
2. `IbkrSourceReader`生成不可变来源文档。
3. `IbkrXmlParser`保留现有公共入口，内部消费来源记录，继续执行既有成交、现金、持仓/LOT、去重、账户/期间及对账规则。
4. 证券流水同步绩效时，把**同一个来源文档实例**随原冻结输入传给Vault/PerformanceFiles，不再重新解析XML。
5. 独立绩效导入也调用同一来源读取器，再由`IbkrPerformanceAdapter`投影；它核对原件SHA256、券商和读取版本后才消费快照。
6. 账号遮蔽/主动展示读取加密原件时也调用同一入口。不同业务请求可重新读取，不做跨会话明文全局缓存。

富途继续使用既有`BrokerActivityFacts.Freeze`规范事实投影路径；本轮仅让通用内部同步回调携带可选来源事实，富途传null，不改其解析或税务规则。

## 兼容与未关闭事项

本次未改税务、FIFO、汇率规则，未更新既有税务来源记录、绩效历史JSON、数据库Schema、服务身份或插件契约。绩效事件ID、来源行标签和金额映射保留原值；新来源路径不反写历史结果。内部同步委托增加来源文档参数，Infrastructure及调用方须配套构建。

此层是原始来源事实，不等于已完成所有交易分类/公司行动的统一规范账本。各投影对未知事件、缺币种/估值、复杂转仓的既有保守门槛继续保留，源记录被保留不表示已经支持其金融语义。

读取版本目前属于本次运行的来源快照，**尚未作为新历史字段持久化**。解析版本持久化与旧记录兼容、跨版本投影差异、完整类型化投资事实，以及独立签认Tax/Performance真实黄金样本仍为ARCH-208未关闭事项；不能以本次代码收敛宣称全项Closed或金融发布通过。

## 验证

- `BrokerSourceFactsChecks`：18条共享来源检查，覆盖确定性、同原件/批次/版本、真实税务同步回调、同快照与独立绩效导入一致、原件不匹配、不可变派生、未知记录保留、取消、深层结构与DTD拒绝。
- 未改写既有`performance-imports-v1.json`；IBKR/FUTU冻结导入结果逐字段一致。
- 两组IBKR夹具的税务解析结果与工作前Debug构建逐字段一致；旧程序集归档于`artifacts/broker-tax-before/`，SHA256为`E80B64EB5844A03BDFE8A7D033DCF063B553F83FA658164CFB952EB564214CDA`。这是变化前后回归，不是独立人工金额基准。
- 既有Verification合成验证68项、绩效SQLCipher/原件去重/冻结与备份回归、WPF交互通过。
- 架构门禁禁止IBKR税务、现金对账、绩效和账号投影直接使用ReadXml/XElement/XDocument。复杂度基线仅将原XElement[]签名键改为BrokerSourceRecord[]，仍为68分支/55行，没有放宽预算或重写预期结果。

详见[实施续记](../implementation/ARCHITECTURE_REMEDIATION_20261020_CONTINUED.md)。
