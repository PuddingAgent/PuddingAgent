# Image Reader 预处理实施记录

需求与合同见 [原生阅读与预处理](../Features/ImageReader原生阅读与预处理-2026-09-15.md)。本轮扩展原生取图工具，移除残留 helper 路由合同；同时修复 low 在 Files 场景下失效的问题，保留聊天原图并在模型请求边界统一处理。

## 验证

- Runtime/Host 定向链接原测试源码：43项通过，覆盖多轮流式图片、metadata/prepare 无图片输出、四档 detail、缩略图后多区域原图读取、灰度/降噪/旋转/编码、EXIF、GIF、WebP、裁剪越界与实际 Responses 图片像素。
- Platform 定向：23项通过，覆盖附件说明、原图存储回放和冻结快照。
- 正常 CoreTests 工程筛选相关测试：117项通过，覆盖 Planner、各 Gateway 图片请求体、48 MiB 最终请求限制、混合图片总量两个输入顺序和失败行为。
- 前端原图保留：5项通过（JPEG/PNG/GIF/WebP/BMP）。合计188项定向测试。
- Runtime 全测试工程存在本轮之前的 `PuddingToolInfrastructureTests.cs:4378` / `Assert.ThrowsExceptionAsync` 编译阻挡，因此 Runtime/Host 使用隔离 harness 链接原测试文件；不声称全库测试通过。

## 构建与部署

- 实现提交：`74ae4e0`。独立归档该提交构建，保留已在线的两处 heartbeat projection 源文件（未纳入本次提交）；来源与哈希记录于 `temp/reader-release-build/build-source.json`。
- Core 构建通过：0错误、235警告；前端生产构建与 bundle budget 通过。
- Desktop 的 prebuilt-artifact 部署成功，Core PID `29572 → 32480`，状态 Ready，busy=false，errors为空；`/health/ready` 返回200。
- 准备包与加载包 manifest SHA256 一致：`b1cf0fb99fc55ddc09ab92489fd146e5adff8694571a24e6a84eca3f883b8b10`。
- 在线 `image_reader` 合同确认 action/detail/transform/max_edge/crop/rotation/grayscale/denoise/format/quality 全部存在，mode不存在。工具描述明确由调用模型自己读图。
- 两处前端静态目录均更新，线上入口与聊天、设置资源哈希匹配本次构建。
- 部署证据：`temp/reader-release-deployment-result.json`、`temp/reader-release-deployment-verification.json`、`temp/reader-release-ui-publish.json`。
- 本轮未发送真实 LLM 读图任务，不能把自动化与部署验证表述为真实模型识别验收。
