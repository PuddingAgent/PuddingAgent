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
