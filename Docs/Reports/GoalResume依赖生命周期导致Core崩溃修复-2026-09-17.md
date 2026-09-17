# GoalResume 依赖生命周期导致 Core 崩溃修复（2026-09-17）

## 现场与根因

Desktop 运行中心报告 Core 自动恢复熔断，最后 PID 37776，退出码 -532462766。Windows Application / .NET Runtime 在 07:52:18、07:52:24、07:52:31、07:52:43 记录相同未处理异常：

```
Cannot consume scoped service 'PuddingPlatform.Services.Goals.GoalRunStore'
from singleton 'PuddingPlatform.Services.Goals.GoalResumeService'.
```

异常发生于 `PuddingApplicationHost.Build`，在监听端口和就绪信号之前。07:51 业务日志仍是上一健康进程的日志，不能据此认为新进程已就绪。Desktop 正常按恢复策略重试后熔断；根因在 Core 新增 GoalResume 服务的依赖生命周期，未修改 Desktop 熔断阈值。

## 修复

- `GoalResumeService` 保持单例，保存进程内 epoch 自动恢复台账。
- 构造函数只接收 `IServiceScopeFactory` 与 logger，每次 `ResumeAsync` 通过 `CreateAsyncScope` 解析 `GoalRunStore` 和 `IGoalCommandService`，调用结束/拒绝/异常时异步释放作用域；每次调用独立使用数据库上下文。
- 不把 `GoalRunStore`/DbContext 提升为单例，不关闭 ValidateScopes/ValidateOnBuild，不改恢复权限、证据闸门、状态机或 CAS。
- 宿主回归发现 `GoalResumeTool` 也漏注册至产品工具表，补齐 `AddPuddingAgentTool<GoalResumeTool>()`。
- 原服务测试直接手工 new 实例，无法发现生命周期错误；现在使用与产品一致的 Singleton/Scoped 容器及严格校验。

## 验证

- 修复前真实 DesktopChild composition test：1/1 失败，与 Windows 日志同一异常。
- 修复后：GoalResumeService/GoalResumeTool 18/18 通过；真实宿主 composition test 1/1 通过（包含服务/工具单例解析和作用域内 store 解析）。
- Core 独立输出构建成功；产物位于 `.tmp-build/goal-resume-scope-fix`。
- 部署保留当时已加载的前端产物（index SHA256 49886b0ac50d1c704f1b785d9292a7484f2c06e530e60817846c2c5ce47fc221），避免源码 dist 的旧版本覆盖上一轮输出上限修复界面。
- Desktop 部署重启成功；新 Core PID 37556，08:05:01 启动，运行超过 60 秒熔断窗口仍为 Ready。/health/ready 返回 200；prepared/loaded manifest、PuddingHost.dll、PuddingPlatform.dll SHA256 均匹配，前端 index 哈希保持不变。

未清理或重置数据库、配置及会话数据，未触发模型消息或模拟业务 Goal 恢复。
