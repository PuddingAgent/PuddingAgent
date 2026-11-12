# Samples/ModelEvaluationClient

| File | Purpose |
|---|---|
| `ModelEvaluationClient.csproj` | net10.0 console sample (TFM inherited from the root build properties); only `ProjectReference` is `Pudding.Api.Sdk`; no packages. |
| `EvaluationDemo.cs` | Fixed public entry point `RunAsync(args, api, output, error, ct)`: `classify <requestJson>` or `review <requestJson>` (strict 2 args, command and JSON used verbatim). Argument/JSON binding errors (no command, unknown command, wrong count, blank argument, invalid JSON, `null` request, missing `input`/`options`) → fixed usage on stderr + exit 2 with zero API calls; success → exactly one camelCase JSON line of the original receipt (exit 0, including Abstain/Degraded/Reject); `ModelEvaluationException` → only `code=...` on stderr, exit 1; caller cancellation → exit 130. Exactly one Classify or one Review call through `ModelEvaluationsClient`; no retry, generated CallId, file/stdin input, domain validator copy, List command or durable-delivery claim. |
| `Program.cs` | Offline shell only: writes the fixed notice “这是可注入 IModelEvaluationApi 的离线示例；请由上层提供已授权的 API。” to stderr and returns 2. No credential reading, no online connection, no host wiring. |
| `code_map.md` | This index. |
