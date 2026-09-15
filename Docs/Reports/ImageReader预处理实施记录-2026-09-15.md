# Image Reader 预处理实施记录

需求与合同见 [原生阅读与预处理](../Features/ImageReader原生阅读与预处理-2026-09-15.md)。本轮扩展原生取图工具，移除残留 helper 路由合同；同时修复 low 在 Files 场景下失效的问题，保留聊天原图并在模型请求边界统一处理。

## 验证

- Runtime/Host 定向链接原测试源码：43项通过，覆盖多轮流式图片、metadata/prepare 无图片输出、四档 detail、缩略图后多区域原图读取、灰度/降噪/旋转/编码、EXIF、GIF、WebP、裁剪越界与实际 Responses 图片像素。
- Platform 定向：23项通过，覆盖附件说明、原图存储回放和冻结快照。
- 正常 CoreTests 工程筛选相关测试：117项通过，覆盖 Planner、各 Gateway 图片请求体、48 MiB 最终请求限制、混合图片总量两个输入顺序和失败行为。
- 前端原图保留：5项通过（JPEG/PNG/GIF/WebP/BMP）。合计188项定向测试。
- Runtime 全测试工程存在本轮之前的 `PuddingToolInfrastructureTests.cs:4378` / `Assert.ThrowsExceptionAsync` 编译阻挡，因此 Runtime/Host 使用隔离 harness 链接原测试文件；不声称全库测试通过。

构建、部署与在线合同检查完成后在下节记录；真实模型图片识别任务验收须单独报告。
