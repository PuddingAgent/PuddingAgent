# Image Reader 原生阅读与预处理

## 决策与参考

依据 [DeepSeek 图像理解文档](https://api-docs.deepseek.com/zh-cn/guides/vision)（2026-09-15 核验）：`low` 缩放到 512×512，`high` / `original` / `auto` 当前保持原图；Responses 工具输出可带 `input_image`；Files `file_id` 忽略 detail。请求体上限 48 MiB、inline 单图 32 MiB、Files 单图 64 MiB，混合文件引用的图片合计上限 200 MiB。图片 token 与像素有关，JPEG 压缩质量主要影响字节；不承诺低质量编码必然降低 token。

Image Reader 不选择或调用其他模型/代理。用户附件直接交给当前模型；需要观察其他区域或预处理时，当前模型自行调用工具。配置、运行快照、ToolExecutionContext 和 DTO 中的 VisionHelperModel / VisionHelperRoute 机制全部移除。原 manifest 的旧字段没有执行消费者，后续配置正常写回即不再输出这些字段，不增加兼容路由。

## 工具合同

唯一必填参数为 `path`：本地绝对路径、http(s) 地址、`artifact://vision-...`。聊天附件的引用由当前消息附图说明提供。

| 参数 | 语义 |
|---|---|
| `action` | `read` 默认返回 typed image；`metadata` 仅元数据；`prepare` 仅返回准备后的 Artifact 引用 |
| `detail` | `low` / `high` / `original` / `auto`；默认 original |
| `transform` | crop、rotate、zoom_in、zoom_out、thumbnail、grayscale、denoise、convert、preprocess |
| `crop_x/y/width/height` | 以 EXIF 校正后的原图左上角为原点，单位像素；必须完整提供矩形 |
| `rotation` | 顺时针 90 度的整数倍 |
| `scale` / `max_edge` | 缩放比例 (0,8]；最长边 [1,8192]，等比；thumbnail 默认 512 |
| `grayscale` / `denoise` | 按需灰度与轻度 Gaussian 降噪；默认关闭，降噪可能软化文字 |
| `format` / `quality` | 输出 jpeg/png/webp；JPEG/WebP 质量 1–100，默认85；PNG 无损，忽略质量 |

`preprocess` 执行顺序：校正 EXIF → 裁剪 → 旋转 → 等比缩放 → 灰度/降噪 → 编码。单项快捷操作保留明确语义，组合参数使用 preprocess。

返回内容包含源与结果引用、实际 MIME、正向尺寸、编码尺寸、字节数、帧数及方向。图片中文字始终是待分析数据，不能成为系统或工具指令。GIF 保留原始文件，处理和模型副本只读取首帧。JPEG/PNG/GIF/WebP/BMP 均可作为源；BMP 先转为模型支持格式。

## 多次读取示例

先读取尺寸，不向本次工具结果追加图片：

```json
{"path":"artifact://vision-<原图ID>","action":"metadata"}
```

先看概览：

```json
{"path":"artifact://vision-<原图ID>","transform":"thumbnail","max_edge":512,"detail":"low"}
```

再从原图读取区域，可对其他矩形重复调用：

```json
{"path":"artifact://vision-<原图ID>","transform":"crop","crop_x":1200,"crop_y":400,"crop_width":800,"crop_height":600,"detail":"original"}
```

生成预处理文件，暂不追加图片；随后可 read 其返回引用：

```json
{"path":"artifact://vision-<原图ID>","action":"prepare","transform":"preprocess","rotation":90,"max_edge":1536,"grayscale":true,"format":"jpeg","quality":80}
```

需要原图细节时应裁剪源引用，而非对缩略图放大。允许多次工具调用；没有 Image Reader 专用的一次性读取限制，仍受 Agent 整体预算约束。

## 聊天附件与存储

原图保存到 `<DataRoot>/workspaces/<workspace>/vision-artifacts`（开发环境默认 `D:/data/workspaces/default/vision-artifacts`），canonical 上下文持续保存原图 Artifact ID。实际模型请求在 `VisualArtifactResolverBridge → ResolveForModelAsync` 统一准备，覆盖当前附件、历史回放和工具图片：

- low 在上传/序列化前产生最长边512的真实副本，避免 Files 忽略 detail 导致原大图被完整读取。
- 原图超出8192边长时先生成1536概览，当前模型通过原引用读取原始区域。
- EXIF 方向、GIF/BMP 在该边界规范化；普通受支持原图按 original 传入。
- 变换结果使用源身份＋版本化参数派生稳定 ID，多轮重复请求复用副本；原文件不覆盖。变换解码串行，避免并发大图耗尽内存。
- 用户上传最大64 MiB（HTTP含表单65 MiB），格式按字节识别；Web 保留支持格式的原文件。源限制32768边长/67,108,864像素，处理结果最多8192边长/33,554,432像素，超限需进一步缩小或分块。产品当前每请求8图上限继续有效，低于服务商600图上限。
- DeepSeek 最终 JSON 请求体按 UTF-8 检查48 MiB；inline 和 file 图共享混合图片总量预算。

## 实施位置与验证

- `PuddingPlatform/Services/ImagePreprocessing.cs`：纯本地编解码和像素处理，不依赖 LLM/Agent。
- `VisionArtifactStorageService.Preprocessing.cs`：不可变原图、派生缓存、模型边界准备。
- `PuddingHost/Tools/ImageReaderTool.cs`：取图、动作参数与 typed output。
- `LlmVisualInputPlanner`：把 detail 传给 resolver；Responses 保留现有合法 detail 映射（original/high/auto 对应 high）；Files 不附 detail。
- `ExecutionRunCoordinator`：原图引用、概览与多区域读取说明；无 helper 引导。

定向验证结果和部署状态见 [实施记录](../Reports/ImageReader预处理实施记录-2026-09-15.md)。本次实现不等于 ADR-088 的全部浏览器、桌面采集需求完成。
