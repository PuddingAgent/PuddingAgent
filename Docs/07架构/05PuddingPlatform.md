# PuddingPlatform

## 定位

PuddingPlatform 是上层平台层与系统组合层。它不等于控制面进程，而是承载产品语义、业务模型、服务暴露策略与平台能力编排的上层概念。
比如：
工作台
Workspace 产品管理
业务流程
服务目录
运营配置
产品入口
管理后台
用户可见的业务语义



参考：
### 前端PuddingPlatformAdmin

前端PuddingPlatformAdmin，admin 管理后台，使用vue-element-admin开发。提供产品级别的服务（待补充设计）。
https://github.com/PanJiaChen/vue-element-admin

使用vue+pnpm+Typescript开发

#### PuddingPlatform 

asp.net core 提供API



## 负责的内容

- Workspace 业务模型与产品入口。
- 服务暴露策略、业务流程编排、产品能力组合。
- 平台级部署形态、运维模型与演进路线。
- 组织 Controller、Runtime、Agent、客户端之间的产品协作关系。


## 与 Controller 的边界

- Controller 负责底层控制逻辑：接入、路由、鉴权、审批、调度、审计。
- Platform 负责上层业务逻辑：Workspace 服务形态、产品语义、平台能力编排。

## 与 Runtime 的边界

- Runtime 承担实际执行与会话权威。
- Platform 不直接持有执行态，而是通过 Controller 和控制协议治理 Runtime。

## 当前阶段的 Platform 关注点

- 把产品从 coding-agent prototype 推进到 Agent OS。
- 把多 Agent、多渠道、多工作空间的业务语义稳定下来。
- 为后续 Web、Avalonia 和外部集成提供统一的产品面定义。
