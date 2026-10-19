# PuddingHomeAssistant · code_map

> 组件化交付规程（`Docs/10_conventions/组件化交付规程.md`）状态：**S1–S5 已交付**
> （已登记解决方案，并已被 `PuddingHost` 消费；见 §5）。
> 边界硬规则：`ProjectReference` 数量必须为 **0**，只允许 BCL + `System.Text.Json`；
> 判据是**编译期失败**，并由 `Source/PuddingHomeAssistantTests/ComponentBoundaryTests.cs`
> 在运行期复核（S4，含控制组防假绿）。

## 1. 这个组件是什么

Home Assistant 的 **REST 叶子组件**：把「读状态 / 查历史 / 调动作」三件事的协议细节与
安全策略从宿主里抽出来，使它们可以在宿主运行中独立构建与测试（规程 R1/R4）。

**不负责**：凭据读取与存储、DataRoot 解析、DI 登记、审批与审计、持久化、WebSocket 订阅、
模板渲染、Admin 界面。输入是最小不可变 DTO（`HaInstance`），宿主在边界处完成转换。

## 2. 文件索引

| 文件 | 职责 | 关键符号 | 约束 |
|---|---|---|---|
| `Contracts/HaContracts.cs` | 实例、状态、动作请求/结果、分页视图的不可变合同 | `HaInstance`、`HaState`、`HaStateView`、`HaStatePage`、`HaServiceCall` | `Token` 标 `[JsonIgnore]` 且 `ToString()` 掩码；`HaStateView.Name` 恒填 `friendly_name` |
| `Policy/HaDomainPolicy.cs` | 三档域策略：永久拒绝 / 默认允许 / 需 Admin 显式放开 | `HaDomainPolicy.Decide`、`HaDomainDecision` | 永久黑名单**不可**被 opt-in 解除；未知域仅在已 opt-in 时放行 |
| `Policy/HaOutputBudget.cs` | 状态筛选、投影与字节预算 | `HaOutputBudget.Project/NormalizeLimit/ResolveName/ResolveDeviceClass/Measure` | 先按 domain/实体/device_class/名称过滤→按 entity_id 排序→游标→投影→按条回退；截断必须回报 `total/returned/truncated/nextAfter` |
| `Policy/HaHistoryBudget.cs` | 历史响应的结构化裁剪 | `HaHistoryBudget.Trim`、`HaHistoryTrim` | 裁剪结果**始终是合法 JSON**；按 entity→state 保序；回报丢弃计数 |
| `Diagnostics/HaErrorCodes.cs` | 稳定错误码与 HTTP 映射 | `HaErrorCodes.*`、`HaClientException` | 错误码是调用方契约，不得随手改名；消息不得含令牌 |
| `Rest/HaRestClient.cs` | REST 传输与解析（状态/历史/动作/健康检查） | `GetStatesAsync`、`GetHistoryAsync`、`CallServiceAsync`、`GetStatusAsync` | 不重试、不跨实例复用、诊断载荷有界；只读接口不产生副作用 |

## 3. 不变量（改这个组件前先读）

1. **策略先于网络**：域策略在**发 HTTP 之前**判定，被拒的调用不会产生任何请求。
2. **永久拒绝不可配置**：`shell_command` / `hassio` / `homeassistant` / `script` / `lock` /
   `alarm_control_panel` / `camera` 写进 `OptInDomains` 也无效。
3. **默认不放开**：`media_player` / `fan` / `climate` 等归 `OptInRequired`，Admin 未勾选一律拒绝。
4. **投影先于预算**：默认不带 `attributes`（体积大且可能含坐标、base64 相机帧），
   但 `friendly_name` 恒带 —— 否则实体 ID 不可读，挑错实体等同挑错设备。
5. **截断必须诚实且可解析**：任何截断都要回报 `truncated`（历史另报 `droppedEntities`），
   且结果必须仍是合法 JSON；逐字符截断字符串会产生半个对象。
6. **令牌不出组件**：不进日志、不进错误消息、不进工具回执；`HaInstance` 由引用方冻结后传入。
7. **只依赖 BCL**：任何 `ProjectReference` / `PackageReference` 都是边界破裂（S4 会取红）。

## 4. 测试与门禁

| 位置 | 覆盖 |
|---|---|
| `…/ComponentBoundaryTests.cs` | S4 边界断言：引用闭包只允许 BCL + `System.Text.Json`，**含控制组**（故意植入禁引用必须被检出） |
| `…/HaDomainPolicyTests.cs` | 三档决策矩阵；永久黑名单写入 opt-in 后仍被拒（10 条 DataRow）；未知域仅在 opt-in 时放行 |
| `…/HaOutputBudgetTests.cs` | 限额归一、域/实体过滤优先级、截断标志、attributes 默认剥离、**`friendly_name` 恒带**、畸形名称不误判、字节预算回退 |
| `…/HaHistoryBudgetTests.cs` | 未超预算原样返回、超预算仍可解析、保序与丢弃计数、单实体前缀裁剪、非数组根不改写、极小预算仍合法 |
| `…/HaErrorCodesTests.cs` | 错误码字符串稳定性（改名即取红） |
| `…/HaRestClientTests.cs` | 用伪传输验证路径/查询串/授权头/错误映射；不触真实实例 |
| `Source/PuddingHomeAssistant.Probe/` | **S3 无宿主真实探针**：对本机实例读 `/api/`、`/api/config`、`/api/states`，核对实体数、预算内字节数与 `lock` 被策略拦截 |

**当前基线**：`dotnet test Source/PuddingHomeAssistantTests` → **98/98 通过**，行覆盖 ≈ 84%。

**取红证据（S4 可证伪性）**：
- `HaHistoryBudget.Trim` 的 `fullBytes <= maxBytes` 改为恒真 ⇒ **4 红 / 91 绿**；
  复原后 `SHA256` 逐字节一致（`80E42CBB…47FDC8A`）。
- `Project` 里 `ResolveName(...)` 改为 `null` ⇒ **1 红 / 97 绿**；
  复原后 `SHA256` 逐字节一致（`90953B6B…F9026C`）。
- 域策略的永久黑名单判定短路 ⇒ 14 例转红。

## 5. 接入状态（S5）

- **已登记解决方案**：`PuddingAgentNetwork.slnx` 含组件、测试与探针三个工程。
- **已被消费**：`Source/PuddingHost/PuddingHost.csproj` 通过 `ProjectReference` 引用本组件。
- **工具装配**（`Source/PuddingHost/Tools/HomeAssistantTools.cs`）：
  `ha_instances`（Low，只读）、`ha_states`（Low）、`ha_history`（Low）、
  `ha_call_service`（Medium，副作用）。
- **实例来源**：`Source/PuddingHost/HomeAssistant/HaInstanceStore.cs` 从
  `<DataRoot>/agents/<agentId>/secrets/home-assistant.json` 读取，按 mtime+length 失效缓存，
  单条非法配置跳过并告警（fail-soft 到配置粒度）。**工具参数不接受 url/token**。
- **Admin 管理面**：`api/admin/home-assistant/*`（列表/写入/删除/连通性测试），
  令牌写入后只回预览，永久黑名单域写入直接 400。
- **尚未实现**：WebSocket 订阅（实时状态）、`ha_template`（模板渲染，需 Admin 权限，刻意不做）、
  按 area/device 目标调用（当前只支持 `entity_ids`）。
