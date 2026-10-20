---
title: 2026-10-20 Responses 终态与 DeepSeek 请求修复
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "修复 Responses 提前断流被判成功及终态等待 EOF，并减小请求序列化体积。"
categories: [docs, changelog]
tags: [responses, deepseek, streaming]
related_docs: []
related_files: [Source/PuddingCore/Core/ResponsesLlmGateway.cs, Source/PuddingCoreTests/ResponsesLlmGatewayTests.cs, Source/PuddingCore/code_map.md]
slug: changelog-2026-10-20-responses-terminal-deepseek
draft: false
---

# 2026-10-20 Responses 终态与 DeepSeek 请求修复

## 改动

- `Source/PuddingCore/Core/ResponsesLlmGateway.cs`：收到 completed/incomplete 终态后立即结束枚举，不再等待 EOF；未收到终态的 EOF 抛出 `HttpIOException(ResponseEnded)`，避免半截回复被误判成功。
- DeepSeek 无状态请求不发送不支持的 store/include 字段；其他服务商保持加密 reasoning 回放。完整 output items、明文 reasoning 与工具调用继续回放。
- 请求 JSON 使用紧凑 UTF-8，避免中文与嵌套工具 JSON 无谓转义，保持解码后的文本不变；体积门禁仍按实际序列化字节计量。
- `Source/PuddingCoreTests/ResponsesLlmGatewayTests.cs`：新增中文回放、终态后禁止再读流、提前 EOF 保留部分输出但报错的回归；同步 Core 代码地图。

## 验证

`dotnet test Source/PuddingCoreTests/PuddingCoreTests.csproj --artifacts-path temp/build/llm-responses --no-restore --nologo -p:CollectCoverage=false --filter FullyQualifiedName~ResponsesLlmGateway`：52/52 通过。

`py Tools/Docs/front_matter.py --check`：1705 个文档，0 个不合规。全量链接检查发现 25 个存量失效目标（架构旧 QA 引用等），本次文档无失效引用；未越界修改历史文档。

## 未完成部分

当前运行实例尚未加载本次构建。真实网络上传问题未宣称解决：合成大请求在 .NET 与 curl 下均出现中止或超时；gzip 探测被服务商当作非法 JSON，未启用该方案。会话压缩与错误展示另行修复。

## 关联

用户本次三项修复请求；[DeepSeek Responses API](https://api-docs.deepseek.com/zh-cn/api/create-response/) 与[兼容性指南](https://api-docs.deepseek.com/zh-cn/guides/responses_api/)。
