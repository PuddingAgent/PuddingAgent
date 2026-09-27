# PuddingApproval

BCL-only 独立叶组件；未登记解决方案、未引用宿主、未装配 DI。`PuddingApproval.csproj` 在 ResolveReferences 前拒绝 ProjectReference/PackageReference。

- `ApprovalService.cs`：精确执行绑定、人工决定幂等、版本 CAS、过期、单次消费、DispatchUnknown 状态。无执行器、无 UI、无策略模型、无自动创建请求入口。
- `ApprovalOperation.cs`：工具 ID、原始参数 JSON、工具定义 JSON、已解析绝对执行目录的不可变快照；有界 JSON 规范化及带版本 SHA-256 指纹。ApprovalRecord 必须携带快照，决定/消费前核对指纹；不根据未知文件系统语义合并路径或解析资源。
- `IApprovalStore`：强制版本比较交换边界。`../PuddingApproval.Sqlite` 已提供独立 SQLite 状态/outbox 同事务适配器并完成组件测试，尚未接入 Host；测试内存 store 不用于交付。
- `../PuddingApprovalTests/ApprovalTests.cs`：32 路竞争决定/消费、重复请求、身份/操作/策略变化、硬边界、过期、拒绝、未知执行及程序集依赖检查。

Core 必须先证明请求是 AwaitingHuman 才能创建记录；DenyPolicy 与 DeferredDependency 不得通过此组件转换成人工批准。ConsumeAsync 只在执行提交点调用，hardBoundariesPassed 来自 Core 当前校验，不能采信客户端布尔值。只有 Outcome=Applied 的消费允许本次派发；Replayed 只表示相同决定的回执，不是再次执行权。

此组件不承诺外部副作用 exactly-once；消费后崩溃恢复需要持久派发状态，未知结果进入 DispatchUnknown 并由 Core 对账。生产存储/执行接线验收前保持 S5 未完成。

独立命令：`dotnet test Source/PuddingApprovalTests/PuddingApprovalTests.csproj --artifacts-path temp/build/native-approval --nologo -p:CollectCoverage=false`。
