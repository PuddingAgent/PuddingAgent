# Task 21：视觉感知 — 摄像头驱动的智能交互

## 概述

为 PuddingAssistant 实现基于摄像头的视觉感知能力，包括人脸识别、表情分析、视线追踪、姿态检测等。整个方案基于 **本地端推断（Local Inference）** 架构，图像数据仅在内存中处理，绝不落盘、绝不上传。

---

## 一、技术选型

### 1.1 可选方案对比

| 方案 | 技术栈 | 优势 | 局限 |
|------|--------|------|------|
| **A. 深度学习（推荐）** | OpenCvSharp + ONNX Runtime | 毫秒级响应，完全本地，精度最高 | 需要打包模型文件 |
| B. 传统视觉 | Emgu CV (Haar/LBP 级联) | 底层控制力强 | 表情准确度偏低，对光线敏感 |
| C. 系统级 API | Windows.Media.FaceAnalysis | 无需第三方库 | 功能有限（存在/朝向/微笑） |

### 1.2 推荐选型

**`OpenCvSharp` + `Microsoft.ML.OnnxRuntime`**

* **模型**：使用 **Mini-Xception**（几 MB），适合打包在单机 EXE 中。
* **人脸检测**：**UltraFace** 或 **YuNet**（轻量级，极速）。
* **表情分类**：**FER** 模型，识别 7 种情绪（生气、厌恶、恐惧、开心、悲伤、惊讶、中性）。

---

## 二、核心算法模型

### 2.1 视线追踪 (Gaze Estimation)

* **原理**：基于人脸朝向（Head Pose）与瞳孔位置（Iris Landmarks）进行几何投影。
* **模型**：**MediaPipe Face Mesh (Iris)** 或 **L2CS-Net**。
* **校准**：让用户看屏幕四个角，将 3D 眼睛向量映射到屏幕 2D 坐标。

### 2.2 表情与疲劳度检测 (Emotion & Fatigue)

* **表情**：Mini-Xception 实时分类 7 种基本情绪。
* **疲劳度**：通过 **EAR (Eye Aspect Ratio)** 算法计算。
  * 监测眨眼频率、闭眼时长、打哈欠（嘴部张开度 MAR）。
  * EAR 持续低于阈值则判定为疲劳。

### 2.3 动作与姿态感知 (Action & Pose)

* **模型**：**MoveNet** 或 **MediaPipe Pose**。
* **站立/休息**：通过肩、髋关键点的高度变化判断。
* **喝水检测**：手臂关键点与嘴部关键点的距离重合度判定，检测到后在 `MEMORY.md` 记录时间戳。

### 2.4 距离感知 (Depth Perception)

通过人脸在画面中的比例估算用户与屏幕的距离。

---

## 三、系统架构

### 3.1 视觉感知总线 (Visual Perception Bus)

视觉识别模块作为独立后台服务，通过高频事件向布丁精灵发送感知数据。

* **推断后端**：ONNX Runtime (C#)，通过 DirectML/CUDA 调用 GPU。
* **图像采集**：OpenCvSharp 捕获摄像头流。

### 3.2 处理管线

```
采集层 → 前处理 → 推断层 (并行) → 聚合层 → 反馈层
```

| 阶段 | 职责 |
|------|------|
| **采集层** | OpenCvSharp 抓取帧（15-30 FPS） |
| **前处理** | 缩放、灰度化、标准化 |
| **推断层 Thread 1** | 人脸与注视点追踪 |
| **推断层 Thread 2** | 身体关键点（站立/喝水） |
| **推断层 Thread 3** | 表情与疲劳度分析 |
| **聚合层 (Brain)** | 将所有数值聚合，判断当前状态 |
| **反馈层 (Pudding UI)** | 发送指令（如 `TriggerAnimation("Comfort")`） |

---

## 四、NPU 硬件加速 (Intel AI Boost)

利用 Intel NPU 实现低功耗、全天候运行，即使 GPU 满载（游戏/渲染）也不影响视觉感知。

### 4.1 OpenVINO 集成

* 引入 `Intel.OpenVINO` NuGet 包。
* 启动时通过 OpenVINO 编译模型，显式指定 `DeviceName = "NPU"`。
* 利用异步推断请求（Async Inference Requests），不抢占 CPU/GPU 资源。

### 4.2 硬件任务分流

| 任务 | 运行设备 | 理由 |
|------|----------|------|
| Gaze & Pose (高频) | NPU | 15-30 FPS 持续运行，功耗极低 |
| Emotion & Fatigue | NPU | 分类任务适合 NPU 张量计算 |
| LLM (布丁大脑) | GPU (DirectML) / NPU | NPU 空间充裕时可跑量化 Tiny-LLM |

### 4.3 零配置适配

启动时自动探测硬件环境：

1. **硬件扫描**：通过 `DeviceQuery` 识别是否存在 NPU。
2. **模型选择**：检测到 NPU 时加载 OpenVINO 格式（`.xml` + `.bin`），否则加载通用 `.onnx`。
3. **驱动校验**：检查驱动版本是否满足最低要求。

### 4.4 NPU 推理伪代码

```csharp
var core = new Core();
var model = core.ReadModel("pudding_vision_model.xml");
var compiledModel = core.CompileModel(model, "NPU");
var inferRequest = compiledModel.CreateInferRequest();

while (isPuddingActive)
{
    var frame = camera.Capture();
    inferRequest.SetInput(frame);
    inferRequest.StartAsync(); // NPU 异步处理，不阻塞 UI 线程

    var results = inferRequest.GetResults();
    PuddingBrain.UpdateState(results);
}
```

---

## 五、能效管理

### 5.1 能效阶梯策略 (Energy Tier Strategy)

| 电源状态 | 推理策略 | 视觉表现 | 目标 |
|----------|----------|----------|------|
| **插电 (AC)** | NPU 全速：注视点 + 喝水 + 姿态 + LLM 实时待命 | 60 FPS，全特效 | 极致体验 |
| **电池 (> 30%)** | NPU 降频：仅疲劳度 + 注视点，LLM 手动唤醒 | 30 FPS，关闭复杂物理形变 | 平衡续航 |
| **低电量 (< 30%)** | 事件驱动：视觉关闭，仅保留系统钩子 | 1 FPS 或静止，布丁"睡眠态" | 生存优先 |

### 5.2 硬件级省电

* **GPU → NPU 迁移**：电池模式下强制将 AI 任务从 GPU（10-30W）迁移到 NPU（1-2W）。
* **性能提示切换**：通过 OpenVINO `perf_hint` 设为 `LATENCY`（插电）或 `THROUGHPUT`（电池）。
* **模型常驻**：视觉模型持久化加载在 NPU 内存中，避免频繁 IO。

### 5.3 动态采样策略

* **忙碌态**：用户高速打字/鼠标移动时，视觉感知降频至每 2 秒一次。
* **空闲态**：用户停下工作时升频进行深度分析。
* **离席态**：系统 5 分钟无输入，关闭 NPU 推断，鼠标移动时恢复。

### 5.4 UI 渲染优化

* **局部刷新**：仅重绘布丁所在的小矩形区域。
* **Composition API**：使用 `Windows.UI.Composition`（Visual Layer），动画在 DWM 中运行。
* **帧率锁定**：布丁静止时渲染帧率降至 0 FPS。

### 5.5 电源状态监听

```csharp
SystemEvents.PowerModeChanged += (s, e) =>
{
    if (e.Mode == PowerModes.Battery)
    {
        VisionEngine.SwitchToLowPowerMode();
        PuddingMascot.LimitFPS(30);
        SwarmManager.HibernateNonEssentialAgents();
    }
    else
    {
        VisionEngine.RestoreHighPerformance();
    }
};
```

### 5.6 省电模式的宠物化表达

* **视觉暗示**：拔掉电源时布丁打哈欠，换上"省电睡衣"，或抱着电池图标。
* **动作收敛**：不再大范围爬行，坐在任务栏"闭目养神"，偶尔睁开一只眼。
* **低电量提示**：发出卡片："主人，我也快没能量了，插上电源再叫醒我吧。"

---

## 六、隐私与非干扰设计

### 6.1 隐私保护 (Privacy-First)

* **显式授权**：UI 上提供显眼的"摄像头遮盖"图标，仅用户开启"视觉互动模式"时启动摄像头。
* **内存丢弃**：视频帧提取特征点后立即从内存销毁，绝不存储/传输。
* **脱敏显示**：调试模式仅显示骨架连线，不显示原始录像。

### 6.2 分级触发（不干扰工作）

* **正常态**：布丁安静地看着你，不做任何打扰。
* **关怀态**（疲劳/久坐）：不跳弹窗，而是慢慢爬到窗口边缘，做"伸懒腰"动作或举小牌子："要不要喝杯水？"

---

## 七、交互场景

### 7.1 情绪同步 (Mood Mirroring)

* 检测到 `Happy`：兴奋转圈，身体变粉红色。
* 检测到疲惫/沮丧：安静爬到手边，变出咖啡图标安慰。

### 7.2 视线追踪 (Eye Tracking)

* 看向左边 → 布丁从右边跑来，试图进入你的视线。
* 盯着它看 → 害羞地缩成一团。

### 7.3 距离感 (Depth Perception)

* 靠得很近 → 表现出被"压迫"的变形特效。
* 离开座位 → 表现失落，找地方打盹。

---

## 八、开发路线图

| 模块 | 实现关键 | 功能描述 |
|------|----------|----------|
| **GazeTracker** | ONNX + Iris Model | 视线追踪，布丁跟随注视点 |
| **PoseMonitor** | MediaPipe Pose | 站立状态判断与喝水计数 |
| **FatigueAnalyzer** | EAR + MAR Algorithm | 疲劳指数计算，驱动提醒行为 |
| **MemorySync** | Markdown Logger | 喝水频率、专注时间写入本地记忆系统 |
| **EnergyManager** | SystemEvents + OpenVINO | 电源状态联动，自动切换能效阶梯 |

---

## 性能优化策略

* **模型量化**：使用 INT8 量化模型，文件小且运行快。
* **动态帧率**：忙碌时降频至 5 FPS，空闲时升频深度分析。
* **模型剪枝**：针对 C# 环境裁剪不必要的网络层。
