# 原生视觉流式上下文与 Image Reader 修复（2026-09-15）

## 故障与证据

用户报告 Session `206a9b48ec904ebb93e7541131fbb835`、页面 Message ID `2783ac83412746baa4e6c88ea85403d4` 出现 `Visual inputs require a workspace and a vision-capable route.`。只读查询 canonical 记录确认，该 ID 是 message_id；实际 turn_id 为 `b9348894a82249b7a225b03cf4cdd0f0`，command 2046、trace `8ecade9abc654153a24677fb9a101e3d`。

前一轮 `e1914d27c722443d8371e945a23cbc16` 的事件 872381、872391 请求两次 `image_reader mode=delegate`；872471 请求 `mode=native`，872472 已成功返回图片。随后事件 872477 在 LLM 请求准备阶段失败。错误日志 `pudding-error-20260915_001.log` 的调用栈进入 `ResponsesLlmGateway.AddInputItemsAsync → PlanVisualInputsAsync`，对应工具图片；后续消息继续携带该历史图片，因此再次失败。

工具执行上下文的 CallerLlmSnapshot 支持视觉，workspace 是 default。失效的是流式运行的环境上下文：Agent Streaming 入口把冻结快照 Push 到 AsyncLocal 后先 yield SSE；下次 MoveNext 从调用方上下文恢复，不会自动带回上次 yield 前设置的值。DirectLlmClient 因而看见空快照，没有提供 VisualArtifactResolver。独立 .NET 10 探针复现为 yield 前 vision，yield 后及其后的 await 均为 null。

## 修复

- `FrozenVisionContextAccessor.MoveNextAsync` 在每次读取 LLM 流时绑定该 Run 的冻结快照，完成、取消或异常后恢复调用方上下文。`AgentExecutionService.Streaming` 统一使用此边界，覆盖首次请求和后续工具轮次。
- `image_reader` 只取图、校验、导入 Artifact 与可选裁剪/缩放/旋转，然后返回 `LlmImagePart` 给调用模型。移除 auto/native/delegate 参数、helper 选择、第二模型调用和文本观察缓存。
- 当前路由无视觉能力或无法传输工具图片时返回明确错误。保持现有 Responses 工具图片传输限制；本次不扩充其他协议，也不通过删掉图片恢复文本请求。
- 删除模型设置页 Vision Helper 选择项；修正文本模型附件提示，停止引导辅助模型代读。已有配置 DTO/manifest 的历史 helper 字段本次未做数据迁移，但 Reader 已没有读取或执行依赖。

## 验证

- 定向链接原测试源码的 harness：Runtime 流式可观测性、AsyncLocal 隔离、Image Reader 共 30 项通过。请求体测试在外层先 yield、两次模型请求的条件下，验证用户 `input_image`、工具 `function_call_output` 的图片及 call_id 全部保留。
- 正常 RuntimeTests 工程编译受其他文件现有 `PuddingToolInfrastructureTests.cs:4378` 的 `Assert.ThrowsExceptionAsync` 不存在阻挡。定向 harness 没有更改或屏蔽被测生产逻辑；不能把它表述为全套测试通过。
- 平台附件提示与内容合同 11 项通过；合计 41 项定向测试通过。前端在隔离的已提交源码副本中叠加本次设置页变更，生产构建及 bundle budget 检查通过。
- 源码/请求体回归通过不等于在线模型已加载新代码，也不等于真实视觉验收完成。部署结果另行记录。
