# PuddingCodexServiceTests
> 源指纹: *.cs=1-69b477346eef, *.csproj=1-0698fd78a719 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingCodexService；用例全部为进程内单测，不需要真实宿主或外部服务。

`Services/CodexTaskCoordinatorTests.cs` 覆盖 6 条用例：
- `Submitted_Task_Continues_After_Request_Returns_And_Is_Persisted`
- `Reply_Uses_Completed_Parent_Thread`
- `SelfHeal_Task_Uses_Safe_Policy_And_Schedules_One_Staged_Restart`
- `Failed_SelfHeal_Task_Does_Not_Request_A_Restart`
- `Complete`
- `Reset`
