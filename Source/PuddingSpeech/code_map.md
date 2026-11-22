# PuddingSpeech 代码地图


> 源指纹: Source/PuddingSpeech/**=7-a7a0a5dcc89b · 条目数: 6 · 最近整理: 2026-11-22
独立语音协议叶子，BCL-only，零 ProjectReference/PackageReference。上层提供冻结路由、凭据与音频；组件不读取配置，不引用宿主。

| 文件 | 用途 |
|---|---|
| `SpeechFailure.cs` | 脱敏、服务商无关的错误分类与 HTTP 建议 |
| `PcmWave.cs` | 实际 RIFF/PCM、采样率、时长与大小校验 |
| `PcmStreamDecoder.cs` | 流式 WAV 头增量解析、PCM16 单声道采样校验与裸 PCM 归一 |
| `QwenAsr.cs` | Qwen 实时识别的 WebSocket 会话 |
| `RealtimeAsrSession.cs` | 实时识别会话的服务商无关端口 |
| `QwenTts.cs` | 显式 Qwen3/Qwen-Audio 请求协议和真实增量 SSE 音频读取 |

独立测试：`../PuddingSpeechTests/`。
