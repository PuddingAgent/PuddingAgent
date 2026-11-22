# Samples/ModelEvaluationClient

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `IModelEvaluationApi` 的注入式离线用法。

> 源指纹: Samples/ModelEvaluationClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelEvaluationClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `EvaluationDemo.cs` | 固定入口 `RunAsync`：`classify`/`review` 各转成恰好一次评估调用，并原样输出回执 JSON | `EvaluationDemo.RunAsync` | `ModelEvaluationsClient`、`ModelEvaluationException` | 参数或 JSON 绑定错误零 API 调用；不重试、不自造 CallId、不复制域校验 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `EvaluationDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
