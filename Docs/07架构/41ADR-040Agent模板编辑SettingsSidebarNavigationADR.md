# 41 ADR-040 Agent 模板编辑 Settings Sidebar Navigation

> 状态：proposed
> 日期：2026-05-23
> 范围：`/admin/global-agent-template`、`/admin/workspace-agent-template`、Workspace 详情页内 Agent 模板编辑抽屉
> 关联：ADR-034 Agent 头像服务端管理、ADR-036 Admin Console 去 Ant Design Pro 化与 Pudding 设计语言统一

## 1. 背景

Agent 模板编辑表单已经从简单 CRUD 演进为运行画像配置页。当前 `/admin/global-agent-template` 的编辑抽屉在 `Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx` 中使用 `width={600}` 的单列 `Drawer + ProForm`，字段从基础信息一路堆叠到能力、Skill、Prompt、人设、记忆、模型、护栏、Token、启用状态。

这导致：

1. 配置项过多，用户无法快速判断当前位置。
2. 高风险能力授权、模型、执行护栏与基础字段混在一起，复核成本高。
3. 后续继续增加 Agent 运行策略字段时，单列长表单不可持续。
4. Global / Workspace 模板页存在重复表单结构，继续扩展会扩大维护成本。

## 2. 决策

采用 Settings Sidebar Navigation：把 Agent 模板编辑抽屉升级为宽抽屉内的双栏设置页。

左侧为固定分组导航，右侧为当前表单内容。仍然使用单个 `ProForm`、单次保存、现有 API DTO，不引入局部保存。

分组如下：

| 分组 | 字段 |
|------|------|
| 基础信息 | 模板 ID、Workspace、继承自全局模板、名称、角色类型、描述、头像、启用、排序 |
| 能力与 Skill | 默认能力、高权限能力、Skill 包 |
| Prompt 与个性 | 系统 Prompt、人设、工具使用约定、首次引导模板、用户 Prompt 模板 |
| 模型与记忆 | 主模型服务商、主模型、潜意识模型服务商、潜意识模型、记忆搜索模式、推理深度 |
| 执行护栏 | 最大轮次、最大耗时、最大工具调用、容器镜像、上下文 tokens、最大回复 tokens |

## 3. 取舍

接受：

- 抽屉宽度从 600/620px 提升到 960px，换取可扫描性。
- 首版只做前端结构重组，不改变保存 API。
- 首版只支持分组锚点和错误定位，不做局部保存。

拒绝：

- Tabs：横向空间不足，分组超过 4 个后拥挤。
- Steps/Wizard：适合创建流程，不适合高级配置反复跳转。
- Collapse：能减少高度，但不能解决"当前位置"和"快速跳转"。

## 4. 后果

正向：

- 用户可一眼看到所有设置组。
- 校验错误可以定位到对应分组。
- 高风险能力和执行护栏获得独立区域。
- Global / Workspace 模板编辑可以逐步收敛到共享组件。

成本：

- 需要抽出 Agent 模板设置表单组件。
- 需要实现 section registry、active section、error section 标记。
- 窄屏需要降级为顶部 sticky 分组导航。

## 5. 验收标准

1. 1251x1270 视口下，编辑 Agent 模板时左侧显示设置分组导航。
2. 点击任一分组能滚动到对应 section。
3. 表单校验失败后自动跳转到第一个错误字段所在分组。
4. 分组导航项能标记错误状态。
5. 不再同时出现 Transfer 版和 Checkbox 版 Skill 包选择。
6. 现有创建、编辑、保存、删除 API 行为不变。
