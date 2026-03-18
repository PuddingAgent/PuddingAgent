# task30 - 知识库、统一存储与知识图谱

最后更新：2026-03-15

## 任务目标

建立 Workspace 级知识基础设施，包括知识库、统一存储层和知识图谱，并打通 Controller 持有服务端能力、Runtime 透明访问、Agent 无感使用的链路。

这条线还应为 Workspace 共享工具台提供底层承载能力，例如 Wiki、对象存储、表格与可版本化共享资产。

对应架构：
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)

## 前置依赖

- Controller 基础宿主与 Runtime 基础宿主已建立。
- Workspace 配置模型已经具备知识能力挂接点。

## 可并行关系

- 知识库、统一存储、知识图谱三条底层服务可部分并行。
- 可与 [task28-platform-workspace-governance.md](task28-platform-workspace-governance.md) 并行推进业务语义。
- 客户端展示与调试查询可与 [task31-client-surfaces.md](task31-client-surfaces.md) 后段并行。

## 顺序任务

1. 建立 `KnowledgeBaseService`
说明：支持目录文件导入、RAG 检索、向量召回和候选提升。
输出：Workspace 级知识库服务骨架。

2. 建立 `UnifiedStorageService`
说明：统一管理 NFS 和对象存储访问，支持 Runtime 跨网络访问共享产物。
输出：统一存储访问层。
前置依赖：任务 1 可并行，不强依赖。

3. 建立 `KnowledgeGraphService`
说明：基于 PostgreSQL 提供 Workspace 级实体、关系和查询能力。
输出：知识图谱骨架。
前置依赖：任务 1 可并行，不强依赖。

4. 建立 `KnowledgeAccessRuntime`
说明：让 Runtime 能透明访问知识库、知识图谱和统一存储，而不暴露底层实现给 Agent。
输出：Runtime 侧透明访问桥接层。
前置依赖：任务 1、任务 2、任务 3；依赖 [task26-runtime-foundation.md](task26-runtime-foundation.md)。

5. 接入 Platform 的业务语义
说明：把知识能力接入 WorkspaceBusinessService 和服务暴露策略。
输出：可被 Platform 统一编排的知识能力。
前置依赖：任务 4；关联 [task28-platform-workspace-governance.md](task28-platform-workspace-governance.md)。

6. 预留共享工具台底层能力
说明：为 Wiki、对象存储、表格和版本化共享资产预留统一访问能力，使 Agent 通过 Runtime/HTTP API 访问这些工具，而不是直接接触底层存储。
输出：共享工具台底层访问抽象。
前置依赖：任务 2、任务 3、任务 4、任务 5。

## 验收标准

- Workspace 可挂接知识库、统一存储和知识图谱。
- Controller 持有服务端能力与授权控制。
- Runtime 可透明访问知识基础设施。
- Agent 使用知识能力时不直接感知底层数据库和存储协议。
- 共享 Wiki / 对象资产 / 表格能力至少具备统一访问抽象。
