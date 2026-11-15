# PuddingSpeechTests 代码地图

仅引用 PuddingSpeech 的独立测试。

`SpeechTests.cs`：组件引用边界、实际 WAV 格式/时长、额度错误脱敏、ASR run/finish 排空和取消、TTS SSE 增量/提前 EOF/多行帧、跨帧 WAV 头归一及协议分派。

`RealtimeAsrTests.cs`：开放音频输入期间即返回文字、停止后的最终排空、句子替换/去重、取消/断连释放、音频校验与有界队列背压。
