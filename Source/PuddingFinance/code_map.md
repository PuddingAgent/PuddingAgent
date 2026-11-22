# PuddingFinance
> 源指纹: Source/PuddingFinance/**=19-c05a48bd4c46 · 条目数: 19 · 最近整理: 2026-11-22

对外部金融数据源的采集适配：必盈端点包装、巨潮公告、官方源白名单抓取。零工程依赖的叶子组件。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Biying/BiyingClient.cs` | 必盈 API 的单次调用与配额回执 | `BiyingClient`、`BiyingCallResult`、`BiyingCallException` | `Biying/BiyingUrlBuilder.cs` | 回执必须带配额余量；失败抛异常，不得返回空结果冒充成功 |
| `Biying/BiyingEndpoint.cs` | 端点模板的分段模型 | `BiyingEndpoint`、`BiyingSegment`、`BiyingField` | `Biying/BiyingEndpointCatalog.cs` | 段类型（字面量 / 占位符 / 证书位）不可混用 |
| `Biying/BiyingEndpointCatalog.cs` | 必盈端点注册表的只读视图 | `BiyingEndpointCatalog` | `Data/biying.endpoints.json` | 数据是嵌入资源，由 `Tools/Biying/extract_endpoints.py` 机械抽取，禁止手改 |
| `Biying/BiyingFinancialTool.cs` | 年报三表的整表穿透查询 | `BiyingFinancialTool` | `Biying/BiyingShaper.cs` | 三表**不做字段投影**（整表即研究对象）；控体积靠只取最近 N 期，默认 5 |
| `Biying/BiyingMarketTool.cs` | 行情研究动作到端点与字段投影的翻译 | `BiyingMarketTool` | `Biying/BiyingToolContracts.cs` | 字段投影决定回执体积；不得让模型直接面对全量端点 |
| `Biying/BiyingOptions.cs` | 必盈调用的配置与时钟抽象 | `BiyingOptions`、`IBiyingClock`、`SystemBiyingClock` | `Biying/BiyingClient.cs` | 凭据来自数据根配置文件，**不进仓库** |
| `Biying/BiyingQuota.cs` | 日配额的 UTC 日切计数与预警 | `BiyingQuota` | `Biying/BiyingClient.cs` | 配额耗尽后所有查询都会失败，必须提前告警而不是当天静默 |
| `Biying/BiyingRawTools.cs` | 任意已注册端点的直通请求 | `BiyingRawTool`、`BiyingRawRequest` | `Biying/BiyingEndpointCatalog.cs` | 只能在已注册端点内直通，不得接受任意 URL |
| `Biying/BiyingResponseCache.cs` | 进程内有界响应缓存 | `BiyingResponseCache` | `Biying/BiyingUrlBuilder.cs` | 缓存键用脱敏 URL（不含证书）；内存镜像与日志里不得出现凭据 |
| `Biying/BiyingSectorAndFlowTools.cs` | 板块与资金流的查询动作 | `BiyingSectorTool`、`BiyingFlowTool`、`BiyingSectorView`、`BiyingFlowView` | `Biying/BiyingToolContracts.cs` | — |
| `Biying/BiyingShaper.cs` | 回执整形与体积计量 | `BiyingShaper`、`BiyingShapeResult`、`BiyingShapeOptions` | `Biying/BiyingMarketTool.cs` | 体积数字取实测值，不得估算（回执要能说明省了多少） |
| `Biying/BiyingToolContracts.cs` | 行情与财务动作的请求结果契约 | `BiyingMarketRequest`、`BiyingFinancialRequest`、`BiyingToolResult` | `Biying/BiyingFinancialTool.cs` | 按「研究动作」切分，不按 API 分组切 |
| `Biying/BiyingUrlBuilder.cs` | 端点与参数拼成真实请求 URL | `BiyingUrlBuilder` | `Biying/BiyingClient.cs` | 形如 `{组}/{接口}/{代码}/{licence}`，证书在路径位——与官方文档示例不同，已实测校正 |
| `Collection/AnnouncementSelector.cs` | 公告类别到研究用途的筛选映射 | `AnnouncementSelector`、`AnnouncementKind` | `Collection/CninfoAnnouncements.cs` | 只覆盖研究真会区分的类别，不做全量分类 |
| `Collection/CninfoAnnouncements.cs` | 巨潮公告的分页查询与解析 | `CninfoAnnouncementSource`、`CninfoAnnouncementQuery`、`CninfoAnnouncement` | `Collection/AnnouncementSelector.cs` | `Stock` 用巨潮自己的 `代码,orgId` 形式；orgId 只能从公告页或前次查询取 |
| `Collection/OfficialSourceFetcher.cs` | 官方数据源的有界抓取 | `OfficialSourceFetcher`、`SourceFetchRequest`、`SourceFetchResult` | `Collection/OfficialSourceHosts.cs` | `MaxBytes` 超限即截断并**如实置位** `Truncated`，不得静默全量读入 |
| `Collection/OfficialSourceHosts.cs` | 官方数据源的后缀白名单 | `OfficialSourceHosts` | `Collection/OfficialSourceFetcher.cs` | **fail-closed** 白名单；一个接受任意地址的抓取组件在服务端就是 SSRF 面 |
| `Data/` | 嵌入的必盈端点注册数据 | `biying.endpoints.json` | `Biying/BiyingEndpointCatalog.cs` | 由抽取脚本生成，禁止手改 |
| `PuddingFinance.csproj` | 金融数据源的零依赖叶子工程 | `PuddingFinance` | — | 构建门禁禁止本工程向外发工程 / 包引用 |
