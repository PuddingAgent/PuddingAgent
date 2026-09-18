# FastRouter 资源池配置（2026-09-18）

按用户提供的 OpenCode 配置，将独立服务商 `fastrouter`（FastRouter）追加至本机 `D:\data\config\llm.providers.json`，端点为 `https://fastrouter.cloud/v1`。API Key 仅写入运行时配置，本文及版本库不保存凭据。

| 模型 ID | 名称 | 协议 | 上下文 tokens | 最大输出 tokens |
| --- | --- | --- | --- | --- |
| gpt-6 | GPT-6 (Astra) | responses | 1050000 | 128000 |
| gpt-6-astra | GPT-6 Astra | responses | 1050000 | 128000 |

`ResponsesLlmGateway` 固定发送 `store: false`，对应输入配置。OpenCode 的 low/medium/high/xhigh/max 是思考档位，不拆为模型；Pudding 通过 Agent 的 `reasoningEffort` 传入，本次不强制默认档位。模型能力只登记 text、reasoning、long-context，未推断视觉能力、价格或服务商配额。

写入前已在忽略目录 `temp/` 备份原文件；写入时核对并发改动，采用同目录临时文件替换。JSON 解析、服务商/模型标识唯一性、协议枚举、写后回读通过，并确认移除新增服务商后与原配置语义完全一致。

未修改产品代码、既有路由或 Agent 绑定，未向服务商发起请求。直接改文件不会调用 `PuddingFileLlmConfigService.Reload`，需下次重启 Core 加载；本次未重启或宣称线上调用验收。
