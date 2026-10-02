# FastRouter 资源池配置（2026-09-18）

按用户提供的 OpenCode 配置，将独立服务商 `fastrouter`（FastRouter）追加至本机 `D:\data\config\llm.providers.json`，端点为 `https://fastrouter.cloud/v1`。API Key 仅写入运行时配置，本文及版本库不保存凭据。

| 模型 ID | 名称 | 协议 | 上下文 tokens | 最大输出 tokens |
| --- | --- | --- | --- | --- |
| gpt-6 | GPT-6 (Astra) | responses | 1050000 | 128000 |
| gpt-6-astra | GPT-6 Astra | responses | 1050000 | 128000 |

`ResponsesLlmGateway` 固定发送 `store: false`，对应输入配置。OpenCode 的 low/medium/high/xhigh/max 是思考档位，不拆为模型；Pudding 通过 Agent 的 `reasoningEffort` 传入，本次不强制默认档位。模型能力只登记 text、reasoning、long-context，未推断视觉能力、价格或服务商配额。

写入前已在忽略目录 `temp/` 备份原文件；写入时核对并发改动，采用同目录临时文件替换。JSON 解析、服务商/模型标识唯一性、协议枚举、写后回读通过，并确认移除新增服务商后与原配置语义完全一致。

未修改产品代码、既有路由或 Agent 绑定，未向服务商发起请求。直接改文件不会调用 `PuddingFileLlmConfigService.Reload`，需下次重启 Core 加载；本次未重启或宣称线上调用验收。

## 17:15 后续兼容性诊断

本次使用同一个本机 Key、相同 `https://fastrouter.cloud/v1` 端点，只发送合成的 OK/echo 测试消息。未把既有会话上传服务商，未修改运行时配置或模型绑定。

### 生产错误定位

`D:\data\logs\system\pudding-20260918_025.log` 第365行：17:12:28 子会话 `206a9b48ec904ebb93e7541131fbb835-sub-2de340ec` 实际使用 fastrouter / gpt-6-astra / responses，3条消息、10个工具，请求 `/v1/responses`。错误日志 `pudding-error-20260918_002.log` 第24行起记录重试后503。说明运行时已加载该路由，但上游拒绝调用。

### 实测结果

| 请求 | 结果 |
| --- | --- |
| 带 Key GET /models | 200；仅列出 gpt-5.4、gpt-5.5、gpt-5.6-sol、gpt-5.6-terra |
| 不带 Key GET /models | 401 / API_KEY_REQUIRED |
| 两个 GPT-6 ID，Responses / Chat Completions，小输出额度与 low | 四项均503 / Service temporarily unavailable |
| gpt-6 最简 Responses / Chat Completions | 两项均404 / model_not_found |
| gpt-6-astra 最简 Responses | 404 / model_not_found |
| gpt-5.6-sol，Responses SSE + function tool | 200；完整工具名、call_id、JSON参数，response.completed |
| gpt-5.6-sol，回放工具调用并提交 function_call_output | 200；正文 OK，response.completed |
| gpt-5.6-sol，Chat Completions SSE | 200；正文 OK、finish_reason=stop及最终usage |

404 原文：`Model "gpt-6-astra" is not supported by any configured account in this group`（gpt-6 同义）。因此当前 Key 分组没有可用 GPT-6 路由；不能把503单独归因于 Pudding 请求字段，也不能靠切换 Chat Completions 解决模型授权/可用性。

Responses 工具闭环采用与当前 Gateway 相同的核心字段：`stream=true`、`store=false`、`include=["reasoning.encrypted_content"]`、input_text、扁平 function schema、function_call_output。续轮还接受 `max_output_tokens=128000`、temperature=0.7、reasoning.effort=low。这里验证的是字段接受及短输出，不证明128000实际输出、百万上下文、全部思考档位或视觉能力。成功事件类型均由当前 ResponsesLlmGateway 内嵌 ResponsesStreamParser 处理。

诊断请求ID：模型列表 `14708310-324c-4b1b-8457-ff68cc188b1d`；gpt-6 404 `fe2a0736-92f5-453e-8a3b-d4a46d570278`；gpt-6-astra 404 `70279cf1-f2d3-44cb-9de7-50ad7a216c05`；sol工具调用 `2f188b7f-eec6-4d71-9077-66a265189657`；sol工具回传 `71fbee74-33d2-4a6c-b0ed-a273c085a87d`。

### Pudding 调用方式与边界

保留 FastRouter Base URL、Key 与 responses 协议。若继续使用 GPT-6，需服务商为当前 Key 所属分组接入支持该模型的账户，随后重新查 /models 并运行功能 smoke。若采用已验证可调用的替代模型，在资源池新增 gpt-5.6-sol，再显式选择该模型；不要把 gpt-6 的名称偷偷映射到 sol。输入容量、价格、其他档位需单独核实。本次未替用户选定替代模型，未重启 Core，未执行 Pudding 内完整 Agent smoke；直接 HTTP 合成探针不是完整产品验收。

额外观察：极短合成请求的上游响应报告约4.4K输入tokens，并在 response.instructions 中附带额外默认编码助手指令。因此该服务存在请求改写，不能假定它是完全透明的转发；长期缓存与Agent行为验收需计入这部分。未采用上游返回指令作为本次执行指令。

公开来源核查：[fastrouter.cloud 首页](https://fastrouter.cloud/) 链接到[其接入教程](https://my.feishu.cn/wiki/R8tvwVkgAiNzGzkyAvJcM1iWn4g)。搜索结果中的 fastrouter.ai 使用不同主机及接口路径，未确认同一服务，未据其文档推断此 Key 的模型权限；结论以 .cloud 当前接口响应为准。原始合成探针保存在忽略目录 temp/fastrouter-*-probes-20260918.json。

## 服务商配置更新后的复测（2026-09-18T17:20:23.937665+08:00）

此节更新此前两个GPT-6均不可用的结论：同一个Key的 `/v1/models` 现已列出 `gpt-6-astra`，仍未列出 `gpt-6`。

| 模型 | 流式工具调用 | 工具结果回传 | 结论 |
| --- | --- | --- | --- |
| gpt-6-astra | HTTP 200，6.01秒，正确返回 compatibility_echo、call_id 和 value=OK | HTTP 200，2.88秒，最终正文 OK | 两轮均 response.completed；响应模型字段为 gpt-6-astra |
| gpt-6 | HTTP 404 / model_not_found | 未继续 | 当前分组仍没有支持该ID的账户 |

请求使用 Pudding Responses 网关格式：input_text、扁平function schema、function_call_output、stream=true、store=false、include=reasoning.encrypted_content、max_output_tokens=128000；首轮未指定思考档位，续轮使用 low 和 temperature=0.7。验证的是小规模合成工具闭环与参数接受，不代表完整Pudding会话、实际128K输出、百万上下文、全部思考档位或视觉验收。

当前调用配置：服务商 fastrouter，模型 gpt-6-astra，协议 responses，Base URL保持 https://fastrouter.cloud/v1。原资源池已经包含该配置，无须改协议或重启来应用服务商端权限变更。本次未编辑运行时配置、替换模型绑定或重启Core。

模型列表请求ID：12f9ea8d-aef2-43d4-8af9-54389bb82e53；astra工具调用：1aa06d35-a256-445e-b77a-b5dbcb91f0c6；astra工具回传：046ca2eb-855f-43a2-9c0f-32462bb08ea8；gpt-6失败：854de406-49ac-4505-9456-222b8d51be04。脱敏证据：忽略目录 temp/fastrouter-gpt6-retest-20260918.json。

## 最终模型清理（2026-09-18 17:22）

按用户要求，从本机 llm.providers.json 的 fastrouter.models 删除唯一 gpt-6 条目，仅保留 gpt-6-astra。修改前备份至忽略目录temp；JSON回读及结构比较确认仅删除该模型，其余字段完全不变。未修改Agent绑定或重启Core；运行时内存模型目录需下次Core重启或资源池保存触发Reload后同步。

## 新增 GPT-5.6 Sol（2026-09-18 17:25）

按用户要求，在本机 fastrouter.models 新增 gpt-5.6-sol（GPT-5.6 Sol），保留 gpt-6-astra，不恢复 gpt-6。协议responses，上下文1050000、最大输出128000；容量依据[OpenAI模型文档](https://developers.openai.com/api/docs/models/gpt-5.6-sol)。未强制默认思考档位或改变默认路由；仅登记此前已实测的文本、流式和工具能力以及推理/长上下文标签，不推断FastRouter视觉透传与价格。此前本任务中Sol的Responses工具闭环已通过；本轮仅配置写入与回读，未重复远端调用或宣称服务商容量压测。

原文件已备份至忽略目录temp，结构比较确认只增加该条目。直接文件编辑需Core重启或资源池保存触发Reload后同步运行时快照；本轮未重启。
