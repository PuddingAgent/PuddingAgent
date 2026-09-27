# PuddingApproval.Sqlite

独立存储适配器，仅引用 PuddingApproval 叶合同与 Microsoft.Data.Sqlite；构建目标拒绝其他 ProjectReference。尚未登记产品解决方案或 Host DI，不操作默认 D:\data。

`SqliteApprovalStore`：

- OpenAsync 使用显式绝对路径初始化 approvals / approval_outbox；连接不启用池，每操作释放，WAL 模式。
- CreateAsync 仅创建 Pending/version=0；执行身份组合唯一，防止用不同审批 ID 给同一 invocation 建立多个独立许可。AwaitingHuman 准入仍由 Core 生产者证明，存储不判策略。
- CreateAsync 校验操作快照与指纹匹配；CompareExchangeAsync 在 SQLite 写事务内校验版本及不可变绑定/到期时间/原始操作快照，然后更新记录并追加相同版本事件；任一步失败整体回滚。
- ReadOutboxAsync 按 sequence 返回有界未确认事件；消费者必须先持久去重 (approval ID, version)，再 AcknowledgeAsync。只保证至少一次交付，不假装已有消费者或调度续行。

版本化状态由 ApprovalService 负责；适配器不另建业务状态机。Microsoft.Data.Sqlite 的 Task API 不保证后台异步 I/O，Host 接线必须使用 Core 后台操作路径，不能在 UI 线程同步执行长存储操作。

依赖固定 Microsoft.Data.Sqlite 10.0.9 / SQLitePCLRaw.bundle_e_sqlite3 2.1.13，避免默认传递版本 2.1.11 导致 NU1903（[依赖告警](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)）；未关闭 NuGet 审计。

独立测试：`dotnet test Source/PuddingApproval.SqliteTests/PuddingApproval.SqliteTests.csproj --artifacts-path temp/build/native-approval-sqlite --nologo -p:CollectCoverage=false`。覆盖重新打开、双实例并发、事件故障回滚、重复执行身份、消费后重新打开及程序集边界。断电、进程强杀和真实 Runtime 续行尚未验收。
