# Retrieval evaluation — production-auto

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `production-auto` |
| set | `production-negative-v1` v1 |
| scope label | `workspace:default` |
| scope root | `D:\CodeProject\PuddingAgent\PuddingAgent` |
| started (UTC) | 2026-10-30T09:39:51.6898273+00:00 |
| total elapsed (ms) | 9537 |
| cases | 4 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project Source/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode production --db "D:\CodeProject\PuddingAgent\PuddingAgent\temp\r0-02-snapshot\20261030-093937\code_index.db" --workspace default --set "Source\PuddingRetrievalEval\eval\sets\production-negative-v1.json" --out "Source\PuddingRetrievalEval\eval\reports\r0-02-production-negative-v1" --max-results 20 --warmup 1 --measured 3 --anchor-symbol ClassifyForEmpty --pagination-query "code" --pagination-page-size 3
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.0000 |
| recall@5 | 0.0000 |
| recall@10 | 0.0000 |
| MRR | 0.0000 |
| precision@5 | 0.0000 |
| precision@10 | 0.0000 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 4 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 4 | 351.815 | 359.176 | 2622.815 | 2622.815 | 2622.815 | 924.45 |
| warm (all later calls) | 16 | 342.381 | 360.005 | 413.462 | 413.462 | 413.462 | 364.67 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 2622.8154 | 0 | yes |  |
| 1 | 359.1764 | 0 | yes |  |
| 2 | 351.815 | 0 | yes |  |
| 3 | 363.9869 | 0 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Intent | CSharp | 签发符号引用的令牌 | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 379.5013 | yes |
| 1 | Intent | CSharp | 空结果怎么区分没查过和确实没有 | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 351.2325 | yes |
| 2 | Intent | CSharp | 两种写法的路径怎么算成同一个身份 | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 352.8455 | yes |
| 3 | Intent | CSharp | 翻页游标是谁签发的 | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 364.7758 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | 签发符号引用的令牌 | `Source/PuddingCodeIndex/Services/Retrieval/SymbolRefIssuer.cs` | _(none)_ |
| 1 | 空结果怎么区分没查过和确实没有 | `Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalClaim.cs` | _(none)_ |
| 2 | 两种写法的路径怎么算成同一个身份 | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | _(none)_ |
| 3 | 翻页游标是谁签发的 | `Source/PuddingCodeIndex/Services/Retrieval/RetrievalPortV1.cs` | _(none)_ |
