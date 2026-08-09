# PuddingPlatformAdmin CodeMAP

> React 管理前端 | Ant Design Pro v6 · React 19 · UmiJS

## 技术栈

| 项 | 值 |
|------|------|
| 框架 | Ant Design Pro v6 / React 19 / UmiJS |
| 包管理 | pnpm |
| 测试 | Jest |

## 目录

| 目录 | 用途 |
|------|------|
| `src/` | 前端源码 |
| `config/` | Umi 配置 |
| `public/` | 静态资源 |
| `mock/` | 模拟数据 |
| `tests/` | 测试 |
| `e2e/` | 端到端测试 |
| `scripts/` | 构建脚本 |
| `docker/` | Docker 配置 |
| `dist/` | 构建产物 |

## 关键文件

| 文件 | 用途 |
|------|------|
| `package.json` | 依赖（3KB） |
| `tsconfig.json` | TypeScript 配置 |
| `biome.json` | 代码格式化 |
| `jest.config.ts` | 测试配置 |
| `Dockerfile` | Docker 构建 |

## LLM 资源池

| 文件 | 用途 |
|------|------|
| `src/pages/llm-resource-pool/index.tsx` | Provider 与模型管理；协议只在模型新增/编辑表单选择，支持 Chat Completions、Responses、Anthropic Messages |
| `src/pages/llm-resource-pool/providerTemplates.ts` | Provider 模板及模型级协议默认值；OpenCode Go 模板含同一 Provider 下三种协议的五个模型 |
| `src/services/platform/api.ts` | Provider DTO 不含协议；Model DTO/Upsert 必须携带协议 |

## Chat 性能热路径

| 文件 | 职责与性能边界 |
|------|----------------|
| `src/pages/chat/client/chatClientStore.ts` | 会话/状态缓存；相同状态轮询必须短路，不重复写缓存或通知订阅者 |
| `src/pages/chat/components/MessageList.tsx` | 将历史消息与 active run 快照投影为消息块 |
| `src/pages/chat/components/MessageProcessSummary.tsx` | 思考/工具过程摘要；折叠时不得构建完整 rounds、trace chips 和展示项 |
| `src/pages/chat/components/MessageItem.tsx` | 消息文本轻量壳；立即显示纯文本 fallback，并异步加载 Markdown 增强器 |
| `src/pages/chat/components/MarkdownBlock.tsx` | ReactMarkdown、KaTeX、HTML parser 和 Prism 的独立按需 chunk |
| `src/pages/chat/components/ChatMain.tsx` | Chat 主壳；子代理运行检查器仅在存在运行卡片或显式打开时加载 |
| `src/pages/chat/components/AgentMessageBubble.tsx` | Agent 消息气泡；每条消息不得预挂载关闭状态的会话诊断 Drawer |
| `src/pages/chat/components/IntentConsole.tsx` | Composer；摄像头弹窗只在用户打开视觉输入时加载和挂载 |
| `src/pages/chat/viewport/useMessageViewportRuntime.ts` | 虚拟列表、锚点与贴底；scroll 状态未变化时不得触发 React commit，首屏稳定轮询应尽快结束 |
| `src/utils/perfEventRuntime.ts` | 首屏常驻的轻量性能事件缓冲；不得反向静态导入完整诊断模块 |
| `src/utils/debug.ts` | 完整性能快照、观察器和诊断建议；仅由 perf/debug 模式或开发面板异步加载 |

后端对应入口：`PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs` 首屏只返回最近 20 条消息；active run 返回最近 64 条可见过程明细，同时用 `processSummary` 保留全量计数。`PuddingPlatform/Controllers/Api/SessionEventsController.cs` 的 bootstrap 子代理事件快照上限为 500 条。

全局壳：`src/app.tsx` 的开发态 `SettingDrawer` 必须异步加载，不能把仅供 `?debug` 使用的 Pro Components 依赖放进生产主包。不要仅为减小 `umi.js` 启用 `granularChunks`；必须合计 HTML 同步引用的 framework chunk，确认真实首载字节和请求顺序确有改善。

## 测试

—（项目内 tests/ + e2e/）
