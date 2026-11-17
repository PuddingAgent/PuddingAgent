---
title: 前端已知失败：InputArea.test.tsx 两条语音用例已对不上当前 UI（非回归）
author: hyfree
date: 2026-11-17
last_reviewed: 2026-11-17
status: active
draft: false
description: 全量 jest 267 套件跑出 2 条失败，均在 InputArea.test.tsx 的语音用例。用「提交前版本 + 阳性对照」证明该失败在我动手之前就存在，属陈旧测试而非回归。记录证据、归因方法与处置建议，避免下次被误判为回归而浪费时间。
categories: [docs, qa]
tags: [frontend, jest, stale-test, attribution, voice]
related_docs: [Docs/11_design/语音唤醒可行性-2026-11-17.md]
related_files: [Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.test.tsx, Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx, Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx, Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceInputButton.tsx]
slug: qa-stale-input-area-voice-tests
---

# 前端已知失败：InputArea.test.tsx 两条语音用例已对不上当前 UI

## 0. 结论

```
Test Suites: 1 failed, 266 passed, 267 total
Tests:       2 failed, 2396 passed, 2398 total
```

失败集中在 `InputArea.test.tsx` 的两条语音用例：

- `switches into voice mode and sends a transcript with voice metadata`
- `shows the voice mode unavailable state when browser microphone capture is unavailable`

**这不是回归**：在 `4c6a4f9`（我最近一次改语音 UI 之前）的版本里，它们引用的 UI 就已经不存在了。

## 1. 失败现象

两处都停在 `getByRole('button', { name: '开始语音输入' })` 找不到元素（`InputArea.test.tsx:176`、`:227`）。

## 2. 归属方法：版本回溯 + 阳性对照

`InputArea.tsx` 只有 4 行，是 `IntentConsole` 的别名导出：

```ts
import IntentConsole, { type ChatStatus } from './IntentConsole';
export default IntentConsole;
```

所以测试实际打的是 `IntentConsole.tsx`。把该文件取到我最后一次改动**之前**的版本再查：

```powershell
$p = "Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx"
$out = & git show "4c6a4f9^:$p"
($out | Select-String '开始语音输入').Count        # → 0
($out | Select-String '按住说话').Count            # → 4   ← 阳性对照
```

- 阴性结果：`开始语音输入` 在**我改动之前**就已经是 0 命中
- 阳性对照：同一份内容里 `按住说话` 命中 4 次 ⇒ 检测器本身有效，0 不是"扫不到"

> 这条纪律值得记住：**任何"不存在"的断言，必须同命令同作用域先跑阳性对照**。这次若不做对照，我会把它当成自己的回归去改。

## 3. 真实原因

语音入口经历了一次重设计：从「开始语音输入 / 开始语音会话 + voice-conversation-panel」变成现在的
「按住说话，松开发送」+ 下拉（转写到草稿 / 免按键）。

- 当前 `IntentConsole.tsx` 里能查到 `按住说话`、`免按键`、`Dropdown`
- 查不到 `开始语音输入`，测试要的 `voice-conversation-panel`、`开始语音会话` 同样不存在

`VoiceInputButton.tsx` 仍然保留着 `开始语音输入` 这个 label，但**已不在 `IntentConsole` 的渲染路径上**，
所以 `VoiceInputButton.test.tsx` 通过、`InputArea.test.tsx` 失败——测试文件与组件都还在，只是不再相连。

## 4. 处置建议

| 选项 | 评价 |
|---|---|
| **A（推荐）改为按当前 UI 断言**：点击「按住说话」入口，断言 `data-testid` 与 `onSendWithMetadata` 的 voice 元数据 | 与实现同步；但要等语音 UI 定稿，否则会连续改两次 |
| B 删除这两条用例 | 掩盖能力：语音发送元数据仍是值得锁的行为 |
| C 保持现状并登记（本文件） | 只解决"不被误判"，不解决"没覆盖" |

当前选择 **C**，理由：语音入口仍在演进（免按键、唤醒词都在排期），此刻把断言绑到易变结构上会反复返工。
定稿后再做 A。

## 5. 对提交门禁的影响

`git commit` 的门禁只跑 C# 测试，**不跑前端 jest** ⇒ 这类陈旧前端测试不会被发现，也不会阻塞提交。
本次是全量 jest 才暴露出来。

如果要防住这一类，需要把 `npx jest` 纳入门禁；代价是提交时间显著变长，需单独评估。
