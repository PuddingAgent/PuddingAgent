# PuddingReviewsTests

评审组件的独立测试工程：用内存端口替身锁定幂等受理、owner 栅栏、证据降级与恢复路径。

> 源指纹: Source/PuddingReviewsTests/**=pending · 条目数: 6 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ReviewTestHost.cs` | 内存版全端口适配器，可配置证据与执行器 | `ReviewTestHost` | `IReviewJobStore`、`IReviewExecutor`、`IReviewEventOutbox` | 只是测试替身，不是生产存储；其宽松语义不得反推产品行为 |
| `ReviewFixtures.cs` | 提供评审作业与快照的构造夹具 | `ReviewFixtures` | `ReviewTestHost` | 夹具默认值被四个测试类共用，改动会同时影响它们 |
| `ReviewJobTests.cs` | 覆盖幂等受理与 owner 栅栏下的作业推进 | `ReviewJobTests` | `ReviewJobCoordinator`、`ReviewTestHost` | 已取消作业的迟到完成必须被拒绝，否则并发回归不可见 |
| `ReviewEvidenceTests.cs` | 覆盖证据缺失与版本不符时的降级与判据覆盖 | `ReviewEvidenceTests` | `ReviewResultValidator` | 调用方自带证据永远拿不到 CheckVerified |
| `ReviewRecoveryTests.cs` | 覆盖 snapshot-required 与证据读取异常后的等待与重试 | `ReviewRecoveryTests` | `ReviewJobCoordinator`、`ReviewTestHost` | 缺证据时不得调用模型；换 owner 重试必须显式发起 |
| `PuddingReviewsTests.csproj` | MSTest.Sdk 的可执行测试工程，只引用 Reviews 实现 | — | `../PuddingReviews` | `UseVSTest=false`，产物是自宿主测试可执行文件 |
