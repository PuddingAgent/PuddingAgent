# Pudding Agent 项目指令

## 项目概述
Pudding 是一个 .NET 10 的自主 Agent 框架，支持六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

dev-up.py是守护进程脚本，用于控制后端、前端的编译、启动。也可以直接直接启动前端和后端。

## 兼容性和补丁约定

不要为了兼容性而牺牲性能和可维护性，除非有明确的业务需求。比如旧的数据格式或者旧的 API 版本。

除非必要，建议直接对D:\data的数据库和配置文件进行原地升级和修补，而不是通过兼容性层。

建议对于配置，配置文件优先，而非数据库优先。比如LLM服务商和模型配置、Agent配置、系统配置（系统预制的，放到程序所在目录）、用户自定义配置（放到用户指定的data目录，见PathHelper）

因为我们还在开发阶段（所以没有历史的需要兼容的数据），所以不建议使用兼容性层，除非有明确的业务需求。不建议为了SQL迁移，增加兼容性层。因为兼容性层会增加维护成本，降低性能。重置数据库，比迁移代码更简单。对于配置类的数据，建议使用配置文件，而不是数据库。

可以清理D:\data下的数据存储和缓存还原一个干净的开发环境，但是建议备份llm.providers.json，因为包含了LLM服务商的信息。

重置开发环境之后，需要访问Bootstrap页面，完成初始化。当然，也需要重新配置一下配置文件，因为Bootstrap是根据配置文件(Bootstrap.Initialized=true)判断是否可以初始化的。


## 版本号约定
- 版本号格式：`主版本号.次版本号.修订号`    

## dev-up脚本python：

dev-up是用于开发环境的调试和代理python工具，方便快速启动环境。修改代码之后，需要重启或者重新编译。

```bash
# 只启动前端端，然后使用命令行启动后端，用于调试后端服务：
python E:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --frontend-only
# 关闭（如果你想手动启动，那么先down，否则会占用端口）
python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --down
# 重启
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --restart
 # 重新编译，用于排除编译缓存问题：
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --rebuild
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --status
```

## 开发环境约定

用户名：admin
密码：Admin@123

测试脚本：
- TestScripts目录
必读文件：
- Agents.md


## 项目路径
- 代码目录： `E:\github\AgentNetworkPlan\PuddingAgent`
- 数据存储: `D:\data` 开发环境数据存储的目录（见PathHelper，dev-up指定的环境变量或启动参数确定）
- 工作空间: `D:\data\workspaces\default`
- 编译入口: `dotnet build PuddingRuntime`
- 代码地图: `Source\code_map.md`  这是项目的代码快速索引，要求必须在开始前阅读，并在任务结束后维护。
- 文档: `Docs`  这是项目的架构文档目录，要求必须在开始前阅读，并在任务结束后维护。


## 运行时配置

> 这里指的是pudding的运行时配置，主要是指运行时的环境变量和工作目录。而不是你的或者项目开发代码的。

- Shell: `pwsh` (PowerShell Core)
- OS: Windows 10
- 工作目录: `D:\data`  （见PathHelper，dev-up指定的环境变量或启动参数确定）

## 代码修改约定
- 所有修改先 dry_run 预览 → 确认后 dry_run=false
- 编译命令: `dotnet build PuddingRuntime --no-restore`


## 长效学习管道（已建成）
1. Pre-Compaction Flush — 压缩前抢救事实
2. Background Extractor — 会话后搬运事实
3. Auto-Dream — 定期整理（每6h）
4. 管道2：经验→SKILL — 黄金路径→技能（每12h）
5. Skill Self-Improvement — 技能自进化（每4h）

## 已知问题
- PuddingAgent.dll 编译后被运行中进程锁定，需重启 Pudding 部署
- SubconsciousWorkerService 新版代码需重启后生效
- 记忆库有 11 个 archived Books 待 Auto-Dream 清理
