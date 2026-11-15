# PuddingSpeech 代码地图

独立语音协议叶子，BCL-only，零 ProjectReference/PackageReference。上层提供冻结路由、凭据与音频；组件不读取配置，不引用宿主。

| 文件 | 用途 |
|---|---|
| `SpeechFailure.cs` | 脱敏、服务商无关的错误分类与 HTTP 建议 |
| `PcmWave.cs` | 实际 RIFF/PCM、采样率、时长与大小校验 |
| `QwenAsr.cs` | Message ASR WebSocket 状态机、并发音频发送与最终结果排空；可替换 socket 端口 |
| `QwenTts.cs` | 显式 Qwen3/Qwen-Audio 请求协议和真实增量 SSE 音频读取 |

独立测试：`../PuddingSpeechTests/`。
